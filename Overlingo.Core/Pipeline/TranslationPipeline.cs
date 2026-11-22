using System.Diagnostics;
using System.Threading.Channels;

namespace Overlingo.Core;

public sealed record SubtitleTranslated(string English, string Czech, string Provider, int TranslateMs, bool FromCache, DateTime AtUtc, bool Replace = false, DateTime DetectedUtc = default, RegionF? Box = null, bool SourceGone = false);
public sealed record SubtitleFailed(string English, string Error, TranslationErrorKind Kind);

public sealed class PipelineStats
{
    public long Ticks, OcrRuns, Skipped, Subtitles, Errors, Suppressed, UiNoise;
    public int LastOcrMs, LastTickMs;
    public string LastOcrText = "", LastOcrMethod = "";
    public string LastEnglish = "", LastCzech = "";
    public double SourceFps;
    public string? SourceError;
    public double AvgOcrMs;
    /// <summary>Měření prodlev (ms): cover, ocr, translate, total.</summary>
    public LatencyTracker Latency { get; } = new();
}

/// <summary>
/// Smyčka: výřez oblasti → změna? → OCR → tracker → fronta překladu → události pro overlay.
/// Překlad běží v samostatném workeru, takže OCR nikdy nečeká na síť; pořadí vět se zachovává.
/// </summary>
public sealed class TranslationPipeline
{
    readonly IOcrEngine _ocr;
    readonly TranslationService _svc;
    Func<AppSettings> _settings;
    SubtitleTracker _tracker;
    ICaptureSource? _source;
    CancellationTokenSource? _cts;
    Task? _loop, _worker;
    string _prefetched = "", _announced = "";
    bool _announcedActive;
    Channel<string>? _queue;
    LightSignature? _lastSig;
    bool _forceOcr;
    int _idleTicks;
    DateTime _lastOcrAt = DateTime.MinValue;
    /// <summary>Nejdelší doba (ms), po kterou se smí OCR přeskakovat kvůli „beze změny“. Chrání před podobnými větami na stejném místě.</summary>
    public int HeartbeatMs { get; set; } = 600;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _replace = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _detected = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _emitted = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, RegionF> _boxes = new();
    DateTime _clearedUtc = DateTime.MinValue;
    RegionF? _lastBox;
    /// <summary>Poloha světlého textu v celém snímku (zlomky) při posledním čtení – pro vystředění překladu.</summary>
    public RegionF? LastTextBox => _lastBox;

    public PipelineStats Stats { get; } = new();
    /// <summary>Volitelný druhý OCR engine (Tesseract) pro slabá čtení.</summary>
    public Func<Frame, Task<string>>? SecondOcr { get; set; }
    public bool OcrPaused { get; set; }
    public bool Running => _loop is { IsCompleted: false };
    public ICaptureSource? Source => _source;

    public event Action<SubtitleTranslated>? Translated;
    public event Action<SubtitleFailed>? TranslationFailed;
    public event Action? SubtitleCleared;
    /// <summary>Při každém čtení, které v oblasti našlo text (i beze změny) – overlay tak pozná, že původní titulek stále visí.</summary>
    public event Action? SubtitleVisible;
    /// <summary>Při prvním čtení nového (ještě nepotvrzeného) originálního titulku: poloha jeho textu v celém snímku.
    /// Overlay podle ní hned přizpůsobí zakrytí novému originálu (velikost, poloha) a smaže starý překlad, dokud nedorazí nový.
    /// Spouští se až po přečtení použitelného textu, takže jasné předměty ve scéně falešný box nevyvolají.</summary>
    public event Action<RegionF>? OriginalSeen;
    /// <summary>Text, který ohlásil OriginalSeen, před potvrzením zmizel (nebo se ukázal jako šum) – prázdné zakrytí se má zrušit.</summary>
    public event Action? OriginalGone;

    public TranslationPipeline(IOcrEngine ocr, TranslationService svc, Func<AppSettings> settings)
    {
        _ocr = ocr; _svc = svc; _settings = settings;
        var s = settings();
        _tracker = new SubtitleTracker(s.StableReads, s.MinLetters);
    }

    public void ApplySettings()
    {
        var s = _settings();
        _tracker = new SubtitleTracker(s.StableReads, s.MinLetters);
        _svc.UseContext = s.UseContext; _svc.ContextLines = s.ContextLines;
        _forceOcr = true;
    }

    public async Task StartAsync(ICaptureSource source)
    {
        await StopAsync();
        _source = source;
        await source.StartAsync();
        _cts = new CancellationTokenSource();
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
        _tracker.Reset(); _lastSig = null; _forceOcr = true; _lastBox = null; _prefetched = ""; _announced = ""; _announcedActive = false;
        var ct = _cts.Token;
        _worker = Task.Run(() => WorkerAsync(_queue.Reader, ct));
        _loop = Task.Run(() => LoopAsync(ct));
        AppLog.Info($"Pipeline spuštěna: {source.Description}");
    }

    /// <summary>Pro testy: nastaví zdroj a frontu bez spuštění smyčky; tiky se volají ručně přes TickAsync.</summary>
    public ChannelReader<string> BeginManual(ICaptureSource source)
    {
        _source = source; _tracker.Reset(); _lastSig = null; _forceOcr = true;
        _queue = Channel.CreateUnbounded<string>();
        return _queue.Reader;
    }

    public async Task StopAsync()
    {
        var cts = _cts; _cts = null;
        if (cts != null)
        {
            cts.Cancel();
            _queue?.Writer.TryComplete();
            try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { }
            try { if (_worker != null) await _worker.ConfigureAwait(false); } catch { }
            cts.Dispose();
        }
        _loop = _worker = null;
        AnnounceGone();
        if (_source != null) { try { await _source.DisposeAsync(); } catch (Exception e) { AppLog.Warn($"Zavření zdroje: {e.Message}"); } _source = null; }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        var sw = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            var s = _settings();
            try { await TickAsync(s, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Stats.Errors++; AppLog.Error($"Chyba ve smyčce: {e.Message}");
            }
            Stats.LastTickMs = (int)sw.ElapsedMilliseconds;
            // Když se čeká jen na potvrzení právě objeveného titulku, přečte se znovu hned (nečeká se celý interval).
            int target = (_tracker.Pending || _tracker.Clearing) ? Math.Min(FastConfirmMs, s.IntervalMs) : EffectiveInterval(s, _idleTicks);
            var wait = Math.Max(20, target - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Při dlouhém klidu (žádný světlý text v oblasti) se interval zdvojnásobí (nejvýš na 300 ms, aby se nezpozdilo první zobrazení) – šetří CPU; po objevení textu se vrací.</summary>
    public const int FastConfirmMs = 100;

    public static int EffectiveInterval(AppSettings s, int idleTicks) =>
        s.AdaptiveInterval && idleTicks >= 10 ? Math.Max(s.IntervalMs, Math.Min(300, s.IntervalMs * 2)) : s.IntervalMs;

    /// <summary>Jeden průchod; veřejné kvůli testům.</summary>
    public async Task TickAsync(AppSettings s, CancellationToken ct)
    {
        var src = _source; if (src == null) return;
        Stats.Ticks++;
        Stats.SourceFps = src.Fps; Stats.SourceError = src.LastError;
        if (OcrPaused) return;

        var crop = await src.GrabAsync(s.EffectiveRegion).ConfigureAwait(false);
        if (crop == null) return;

        var sig = ImagePrep.LightMaskSignature(crop, s.WhiteThreshold);
        if (s.SkipWhenNoLightText && sig.LightFraction < 0.002)
        {
            Stats.Skipped++; _idleTicks++;
            _lastSig = null;
            AnnounceGone();
            var r = _tracker.Observe(null);
            if (r.Event == TrackerEvent.Cleared) { _clearedUtc = DateTime.UtcNow; SubtitleCleared?.Invoke(); }
            return;
        }
        _idleTicks = 0;
        bool heartbeat = (DateTime.UtcNow - _lastOcrAt).TotalMilliseconds >= HeartbeatMs;
        if (!_forceOcr && !heartbeat && _lastSig is { } prev && ImagePrep.Distance(prev, sig) < s.ChangeThreshold * 0.5)
        { Stats.Skipped++; return; }
        _forceOcr = false; _lastSig = sig; _lastOcrAt = DateTime.UtcNow;

        var opt = new OcrOptions { Mode = s.OcrMode, WhiteThreshold = s.WhiteThreshold, Upscale = s.Upscale, MaxDimension = _ocr.MaxDimension, MinLetters = s.MinLetters };
        var sw = Stopwatch.StartNew();
        var res = await OcrStrategy.RunAsync(crop, opt, _ocr.RecognizeAsync, s.UseTesseract ? SecondOcr : null).ConfigureAwait(false);
        sw.Stop();
        Stats.OcrRuns++; Stats.LastOcrMs = (int)sw.ElapsedMilliseconds;
        Stats.AvgOcrMs = Stats.AvgOcrMs == 0 ? Stats.LastOcrMs : Stats.AvgOcrMs * 0.9 + Stats.LastOcrMs * 0.1;
        Stats.Latency.Record("ocr", Stats.LastOcrMs);
        var cleaned = s.StripJunkTokens ? TextTools.StripJunk(res.Text) : res.Text;
        if (TextTools.ContainsIgnored(cleaned, s.IgnoreContains)) cleaned = "";
        if (TextTools.LooksLikeUiNoise(cleaned, s.MaxSubtitleChars)) { Stats.UiNoise++; cleaned = ""; }
        Stats.LastOcrText = cleaned; Stats.LastOcrMethod = res.Method;
        if (TextTools.IsPlausible(TextTools.Normalize(cleaned), s.MinLetters))
        {
            var rg = s.EffectiveRegion;
            if (_ocr is IOcrBounds ob && ob.LastBounds is { W: > 0, H: > 0 } lb)
            {   // poloha textu podle samotného OCR – neovlivní ji světlé předměty ve scéně
                _lastBox = new RegionF { X = rg.X + lb.X * rg.W, Y = rg.Y + lb.Y * rg.H, W = lb.W * rg.W, H = lb.H * rg.H };
            }
            else if (ImagePrep.LightBounds(crop, s.WhiteThreshold) is { } bb)
            {
                _lastBox = new RegionF { X = rg.X + bb.X0 * rg.W, Y = rg.Y + bb.Y0 * rg.H, W = (bb.X1 - bb.X0) * rg.W, H = (bb.Y1 - bb.Y0) * rg.H };
            }
            SubtitleVisible?.Invoke();
        }

        // Při stejném stabilním titulku nechceme, aby „stejný signál“ zablokoval další čtení nutná pro stabilitu.
        int supBefore = _tracker.Suppressed;
        var ev = _tracker.Observe(cleaned);
        if (s.VerboseLog)
            AppLog.Info($"OCR {res.Method} {Stats.LastOcrMs} ms | raw=\"{res.Text}\" | clean=\"{cleaned}\" | → {ev.Event}{(_tracker.Suppressed > supBefore ? Loc.T(" (duplicita potlačena)") : "")}{(_tracker.Pending ? Loc.T(" [čeká na potvrzení]") : "")}");
        Stats.Suppressed = _tracker.Suppressed;
        {   // první čtení nového originálu → overlay hned přizpůsobí zakrytí (viz OriginalSeen)
            var seen = TextTools.Normalize(cleaned);
            if (!TextTools.IsPlausible(seen, s.MinLetters)) AnnounceGone();
            else if (_tracker.Pending && _lastBox != null && IsNewOriginal(seen))
            {
                _announced = seen; _announcedActive = true;
                OriginalSeen?.Invoke(new RegionF { X = _lastBox.X, Y = _lastBox.Y, W = _lastBox.W, H = _lastBox.H });
            }
        }
        if ((ev.Event == TrackerEvent.NewSubtitle || ev.Event == TrackerEvent.Updated) && ev.Text != null)
        {
            Stats.Subtitles++; Stats.LastEnglish = ev.Text;
            if (ev.Event == TrackerEvent.Updated) _replace[ev.Text] = 1;
            _detected[ev.Text] = _tracker.CandidateSince;
            _emitted[ev.Text] = DateTime.UtcNow;
            if (_lastBox != null) _boxes[ev.Text] = _lastBox;
            _queue?.Writer.TryWrite(ev.Text);
        }
        else if (ev.Event == TrackerEvent.Cleared) { _clearedUtc = DateTime.UtcNow; SubtitleCleared?.Invoke(); }
        else if (_tracker.Pending)
        {
            // překlad „na zkoušku“: výsledek skončí v cache, takže potvrzení druhým čtením už nečeká na síť
            if (s.SpeculativeTranslate)
            {
                var norm = TextTools.Normalize(cleaned);
                if (norm.Length <= s.MaxSubtitleChars && TextTools.IsPlausible(norm, s.MinLetters) && TextTools.Similarity(norm, _prefetched) < 0.9)
                {
                    _prefetched = norm;
                    var token = ct;
                    _ = Task.Run(async () => { try { await _svc.PrefetchAsync(norm, token).ConfigureAwait(false); } catch { } });
                }
            }
            // dokud se čeká na stabilitu, vynuť další OCR (obraz se nemusí měnit)
            _forceOcr = true;
        }
    }

    void AnnounceGone()
    {
        if (!_announcedActive) return;
        _announcedActive = false; _announced = "";
        OriginalGone?.Invoke();
    }

    /// <summary>Je přečtený text opravdu NOVÝ titulek (ne to, co se už zobrazuje, ani jeho delší/kratší čtení)?</summary>
    bool IsNewOriginal(string seen)
    {
        if (TextTools.Similarity(seen, _announced) >= 0.8) return false;
        var live = _tracker.LastEmitted;
        if (live == null) return true;
        if (TextTools.Similarity(seen, live) >= 0.8) return false;
        return TextTools.ContainmentScore(live, seen) < 0.85 && TextTools.ContainmentScore(seen, live) < 0.85;
    }

    async Task WorkerAsync(ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var en in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var r = await _svc.TranslateAsync(en, ct).ConfigureAwait(false);
                    Stats.LastCzech = r.Text;
                    if (!r.FromCache) Stats.Latency.Record("translate", r.Ms);
                    // titulek mohl zmizet dřív, než dorazil (pomalý) překlad → overlay ho nesmí nechat viset
                    bool gone = _emitted.TryRemove(en, out var em) && _clearedUtc >= em;
                    _boxes.TryRemove(en, out var box);
                    Translated?.Invoke(new SubtitleTranslated(en, r.Text, r.Provider, r.Ms, r.FromCache, DateTime.UtcNow, _replace.TryRemove(en, out _), _detected.TryRemove(en, out var det) ? det : default, box, gone));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (TranslationException e)
                {
                    Stats.Errors++; AppLog.Error(e.Message);
                    TranslationFailed?.Invoke(new SubtitleFailed(en, e.Message, e.Kind));
                }
                catch (Exception e)
                {
                    Stats.Errors++; AppLog.Error($"Překlad: {e.Message}");
                    TranslationFailed?.Invoke(new SubtitleFailed(en, e.Message, TranslationErrorKind.Network));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
