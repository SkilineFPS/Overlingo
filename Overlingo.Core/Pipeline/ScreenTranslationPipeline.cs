using System.Diagnostics;

namespace Overlingo.Core;

/// <summary>Přeložený řádek textu na obrazovce včetně polohy (zlomky snímku).</summary>
public sealed record ScreenLine(string English, string Czech, double X, double Y, double W, double H);

/// <summary>
/// Režim „vše na obrazovce“: OCR celého snímku s polohou řádků → stabilizace → dávkový překlad → seznam řádků pro overlay,
/// který překryje původní text na stejném místě.
/// </summary>
public sealed class ScreenTranslationPipeline
{
    sealed class Tracked
    {
        public string En = ""; public double X, Y, W, H;
        public int Seen = 1, Missed; public string? Cs; public bool Queued; public DateTime RetryAfter;
    }

    readonly IScreenOcr _ocr;
    readonly TranslationService _svc;
    readonly Func<AppSettings> _settings;
    readonly List<Tracked> _tracked = new();
    readonly object _lk = new();
    ICaptureSource? _source;
    CancellationTokenSource? _cts;
    Task? _loop;
    LightSignature? _lastSig;
    bool _force = true;
    string _lastEmitted = "";
    int _translating;
    DateTime _lastOcrAt = DateTime.MinValue;
    public int HeartbeatMs { get; set; } = 1500;

    public PipelineStats Stats { get; } = new();
    public bool OcrPaused { get; set; }
    public bool Running => _loop is { IsCompleted: false };
    public ICaptureSource? Source => _source;
    /// <summary>Pro testy: úloha probíhajícího překladu.</summary>
    public Task? PendingTranslation { get; private set; }

    public event Action<IReadOnlyList<ScreenLine>>? Updated;
    public event Action<SubtitleFailed>? TranslationFailed;

    public ScreenTranslationPipeline(IScreenOcr ocr, TranslationService svc, Func<AppSettings> settings)
    { _ocr = ocr; _svc = svc; _settings = settings; }

    public void ApplySettings() { _force = true; }

    public async Task StartAsync(ICaptureSource source)
    {
        await StopAsync();
        _source = source;
        await source.StartAsync();
        lock (_lk) _tracked.Clear();
        _lastSig = null; _force = true; _lastEmitted = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct));
        AppLog.Info($"Překlad celé obrazovky spuštěn: {source.Description}");
    }

    public async Task StopAsync()
    {
        var cts = _cts; _cts = null;
        if (cts != null)
        {
            cts.Cancel();
            try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { }
            cts.Dispose();
        }
        _loop = null;
        if (_source != null) { try { await _source.DisposeAsync(); } catch (Exception e) { AppLog.Warn($"Zavření zdroje: {e.Message}"); } _source = null; }
        Updated?.Invoke(Array.Empty<ScreenLine>());
    }

    public void BeginManual(ICaptureSource source) { _source = source; lock (_lk) _tracked.Clear(); _lastSig = null; _force = true; _lastEmitted = ""; }

    async Task LoopAsync(CancellationToken ct)
    {
        var sw = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            var s = _settings();
            try { await TickAsync(s, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Stats.Errors++; AppLog.Error($"Chyba ve smyčce (celá obrazovka): {e.Message}"); }
            Stats.LastTickMs = (int)sw.ElapsedMilliseconds;
            var wait = Math.Max(30, Math.Max(s.IntervalMs, s.ScreenMinIntervalMs) - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(AppSettings s, CancellationToken ct)
    {
        var src = _source; if (src == null) return;
        Stats.Ticks++; Stats.SourceFps = src.Fps; Stats.SourceError = src.LastError;
        if (OcrPaused) return;

        var frame = await src.GrabAsync(RegionF.Full).ConfigureAwait(false);
        if (frame == null) return;

        var sig = ImagePrep.LumaSignature(frame);
        bool pending; lock (_lk) pending = _tracked.Any(t => t.Seen < s.StableReads);
        bool heartbeat = (DateTime.UtcNow - _lastOcrAt).TotalMilliseconds >= HeartbeatMs;
        if (!_force && !pending && !heartbeat && _lastSig is { } prev && ImagePrep.Distance(prev, sig) < 0.003)
        {
            Stats.Skipped++;
            StartTranslationIfNeeded(s, ct);
            return;
        }
        _force = false; _lastSig = sig; _lastOcrAt = DateTime.UtcNow;

        var sw = Stopwatch.StartNew();
        var lines = await _ocr.RecognizeLinesAsync(frame).ConfigureAwait(false);
        lines = await AddSubtitleZoneAsync(s, frame, lines).ConfigureAwait(false);
        sw.Stop();
        Stats.OcrRuns++; Stats.LastOcrMs = (int)sw.ElapsedMilliseconds;
        Stats.AvgOcrMs = Stats.AvgOcrMs == 0 ? Stats.LastOcrMs : Stats.AvgOcrMs * 0.9 + Stats.LastOcrMs * 0.1;

        var good = new List<OcrLine>();
        foreach (var l in lines)
        {
            var t = s.StripJunkTokens ? TextTools.StripJunk(l.Text) : TextTools.Normalize(l.Text);
            if (!TextTools.IsPlausible(t, s.MinLetters)) continue;
            if (TextTools.QualityScore(t) < 0.5 * TextTools.LetterCount(t)) continue;
            if (TextTools.ContainsIgnored(t, s.IgnoreContains)) continue;
            good.Add(l with { Text = t });
        }
        good = DedupOverlapping(good).OrderByDescending(l => l.W * l.H).Take(40).ToList();
        Stats.LastOcrText = $"{good.Count} řádků";

        lock (_lk)
        {
            var used = new HashSet<Tracked>();
            foreach (var l in good)
            {
                Tracked? best = null; double bs = 0.85;
                foreach (var t in _tracked)
                {
                    if (used.Contains(t)) continue;
                    var sim = TextTools.Similarity(t.En, l.Text);
                    if (sim >= bs) { bs = sim; best = t; }
                }
                if (best != null)
                {
                    used.Add(best); best.Seen++; best.Missed = 0; best.X = l.X; best.Y = l.Y; best.W = l.W; best.H = l.H;
                    if (best.Cs == null) best.En = l.Text;
                }
                else { var n = new Tracked { En = l.Text, X = l.X, Y = l.Y, W = l.W, H = l.H }; _tracked.Add(n); used.Add(n); }
            }
            foreach (var t in _tracked) if (!used.Contains(t)) t.Missed++;
            _tracked.RemoveAll(t => t.Missed >= 3);
        }
        StartTranslationIfNeeded(s, ct);
        Emit(s);
    }

    /// <summary>
    /// Druhý průchod jen přes oblast titulků: bílý text se izoluje a zvětší (jako v režimu „Jen konverzace“),
    /// takže se čtou i titulky na světlém / rušném pozadí, které čtení celého snímku míjí.
    /// Řádky z této oblasti mají přednost před řádky z celého snímku, které se s nimi překrývají.
    /// </summary>
    async Task<IReadOnlyList<OcrLine>> AddSubtitleZoneAsync(AppSettings s, Frame frame, IReadOnlyList<OcrLine> full)
    {
        try
        {
            var r = s.EffectiveRegion.Clamp();
            if (r.W <= 0 || r.H <= 0) return full;
            var crop = FrameCrop.Crop(frame, r);
            if (ImagePrep.LightBounds(crop, s.WhiteThreshold) == null) return full;
            int x0 = (int)Math.Round(r.X * frame.Width), y0 = (int)Math.Round(r.Y * frame.Height);
            x0 = Math.Clamp(x0, 0, frame.Width - 1); y0 = Math.Clamp(y0, 0, frame.Height - 1);
            int scale = Math.Clamp(s.Upscale, 1, 3);
            while (scale > 1 && (crop.Width * scale + 24 > _ocr.MaxDimension || crop.Height * scale + 24 > _ocr.MaxDimension)) scale--;
            const int pad = 12;
            var iso = ImagePrep.IsolateLight(ImagePrep.ScaleBilinear(crop, scale), s.WhiteThreshold, pad);
            var zone = await _ocr.RecognizeLinesAsync(iso).ConfigureAwait(false);
            if (zone.Count == 0) return full;
            var mapped = new List<OcrLine>();
            foreach (var l in zone)
            {
                double px = (l.X * iso.Width - pad) / scale, py = (l.Y * iso.Height - pad) / scale;
                double pw = l.W * iso.Width / scale, ph = l.H * iso.Height / scale;
                mapped.Add(new OcrLine(l.Text, (x0 + px) / frame.Width, (y0 + py) / frame.Height, pw / frame.Width, ph / frame.Height));
            }
            var rest = full.Where(f => !mapped.Any(m => Overlap(m.X, m.Y, m.W, m.H, f.X, f.Y, f.W, f.H) > 0.3));
            return mapped.Concat(rest).ToList();
        }
        catch (Exception e) { AppLog.Warn("Čtení oblasti titulků v režimu celé obrazovky selhalo: " + e.Message); return full; }
    }

    static double Overlap(double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh)
    {
        double ix = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx)), iy = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
        double smaller = Math.Min(aw * ah, bw * bh);
        return smaller <= 0 ? 0 : ix * iy / smaller;
    }

    /// <summary>Překrývající se řádky (stejný text čtený dvakrát / oříznutá část větší věty) se sloučí – zůstane ten s víc písmeny.</summary>
    public static List<OcrLine> DedupOverlapping(List<OcrLine> lines)
    {
        var keep = new List<OcrLine>();
        foreach (var l in lines.OrderByDescending(x => TextTools.LetterCount(x.Text)))
            if (!keep.Any(k => Overlap(k.X, k.Y, k.W, k.H, l.X, l.Y, l.W, l.H) > 0.5)) keep.Add(l);
        return keep;
    }

    void StartTranslationIfNeeded(AppSettings s, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _translating, 1, 0) != 0) return;
        List<Tracked> batch;
        lock (_lk)
        {
            var now = DateTime.UtcNow;
            batch = _tracked.Where(t => t.Seen >= s.StableReads && t.Cs == null && !t.Queued && t.RetryAfter <= now).Take(12).ToList();
            foreach (var t in batch) t.Queued = true;
        }
        if (batch.Count == 0) { Interlocked.Exchange(ref _translating, 0); return; }
        PendingTranslation = Task.Run(async () =>
        {
            try
            {
                var res = await _svc.TranslateManyAsync(batch.Select(b => b.En).ToList(), ct).ConfigureAwait(false);
                lock (_lk) for (int i = 0; i < batch.Count; i++) { batch[i].Cs = res[i].Text; batch[i].Queued = false; }
                Stats.Subtitles += batch.Count; Stats.LastEnglish = batch[^1].En; Stats.LastCzech = res[^1].Text;
                Emit(_settings());
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Stats.Errors++; AppLog.Error("Překlad obrazovky: " + e.Message);
                lock (_lk) foreach (var b in batch) { b.Queued = false; b.RetryAfter = DateTime.UtcNow.AddSeconds(5); }
                TranslationFailed?.Invoke(new SubtitleFailed(batch[0].En, e.Message, (e as TranslationException)?.Kind ?? TranslationErrorKind.Network));
            }
            finally { Interlocked.Exchange(ref _translating, 0); }
        });
    }

    void Emit(AppSettings s)
    {
        List<ScreenLine> list;
        lock (_lk)
            list = _tracked.Where(t => t.Cs != null && t.Seen >= s.StableReads)
                .OrderByDescending(t => TextTools.LetterCount(t.En))
                .Aggregate(new List<ScreenLine>(), (acc, t) =>
                {
                    if (!acc.Any(k => Overlap(k.X, k.Y, k.W, k.H, t.X, t.Y, t.W, t.H) > 0.5)) acc.Add(new ScreenLine(t.En, t.Cs!, t.X, t.Y, t.W, t.H));
                    return acc;
                });
        var sig = string.Join("|", list.Select(l => $"{l.English}@{(int)(l.X * 200)},{(int)(l.Y * 200)}"));
        if (sig == _lastEmitted) return;
        _lastEmitted = sig;
        Updated?.Invoke(list);
    }
}
