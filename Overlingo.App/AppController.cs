using System.Net.Http;
using System.IO;
using System.Windows;
using Overlingo.App.Capture;
using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Spojuje nastavení, zdroj, pipeline, překlad a overlay. Žádná UI logika.</summary>
public sealed class AppController
{
    public AppSettings Settings { get; private set; }
    public ISecretProtector Protector { get; } = new DpapiProtector();
    public TranslationCache Cache { get; private set; }
    public TranslationService Service { get; }
    public TranslationPipeline Pipeline { get; }
    public ScreenTranslationPipeline ScreenPipeline { get; }
    public OverlayWindow Overlay { get; }
    public ScreenOverlayWindow ScreenOverlay { get; }
    public double CpuPercent { get; private set; }
    public double RamMb { get; private set; }
    TimeSpan _lastCpu; DateTime _lastCpuAt = DateTime.UtcNow; int _wdTicks;
    public string OcrInfo { get; }
    public bool OcrAvailable { get; }
    public bool TranslationEnabled { get; private set; }
    public string? LastStartError { get; private set; }
    public string SourceDescription => ActiveSource?.Description ?? "(zdroj neběží)";
    HttpClient _http = TranslatorFactory.NewHttp();
    readonly WindowsOcrEngine? _ocr;
    TesseractOcr? _tess;
    public string TesseractInfo => _tess != null ? $"Tesseract nalezen: {_tess.Path} (použije se jen při slabém čtení)" : "Tesseract nenalezen – volitelné, aplikace funguje i bez něj.";
    readonly string _cachePath = Path.Combine(AppSettings.DefaultDir, "translation-cache.json");
    System.Windows.Threading.DispatcherTimer? _watchdog;
    DateTime _lastRestart = DateTime.MinValue;
    bool _starting;
    RegionF? _lastTextBox;
    public SubtitleHistory History { get; } = new();
    public CorrectionStore Corrections { get; } = new(Path.Combine(AppSettings.DefaultDir, "corrections.json"));
    public OverlayWebServer Web { get; } = new();
    int _webGen;
    /// <summary>Titulek zmizel ze hry – v OBS ho necháme na dobu čtení (jako overlay) a pak skryjeme.</summary>
    void HideWebLater()
    {
        if (!Settings.WebEnabled) return;
        int gen = ++_webGen;
        int ms = Settings.DisplaySeconds > 0 ? (int)(Settings.DisplaySeconds * 1000) : 3500;
        _ = Task.Delay(ms).ContinueWith(_ => { if (gen == _webGen) Web.Clear(); });
    }
    public string WebInfo => Web.Running ? $"OBS Browser Source: {Web.Url}" : "OBS server vypnut";

    void ApplyWeb()
    {
        try
        {
            if (Settings.WebEnabled && (!Web.Running || Web.Port != Settings.WebPort)) Web.Start(Settings.WebPort);
            else if (!Settings.WebEnabled && Web.Running) Web.Stop();
        }
        catch (Exception e) { AppLog.Warn($"OBS server nelze spustit na portu {Settings.WebPort}: {e.Message}"); Web.Stop(); }
    }
    public string ExportDiagnosticsZip()
    {
        var dir = Path.Combine(AppSettings.DefaultDir, "diagnostika"); Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, $"overlingo-diagnostika-{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        DiagnosticsBundle.Create(zip, Settings, AppLog.LogDir, dir, Loc.T($"Zdroj: {SourceDescription}\nOCR: {OcrInfo}\n{TesseractInfo}\nPřekladač: {Service.Translator.Name}"));
        return zip;
    }

    public AppController()
    {
        AppSettings.MigrateOld();
        Settings = AppSettings.Load();
        _ocr = WindowsOcrEngine.TryCreate(Settings.SourceLang);
        OcrAvailable = _ocr != null;
        OcrInfo = _ocr != null ? $"Windows OCR ({_ocr.Language})" : WindowsOcrEngine.MissingLanguageHelp;
        _tess = TesseractOcr.TryCreate(Settings.TesseractPath);
        Cache = new TranslationCache(Settings.CacheMax);
        Cache.Load(_cachePath);
        _http = TranslatorFactory.NewHttp(Settings.HttpTimeoutSec);
        Service = new TranslationService(TranslatorFactory.Create(Settings, _http, Protector), Cache)
        { UseContext = Settings.UseContext, ContextLines = Settings.ContextLines, RetryCount = Settings.RetryCount,
          LangTag = Languages.PairTag(Settings.SourceLang, Settings.TargetLang), Glossary = Glossary.Parse(Settings.Glossary), Corrections = Corrections };
        Pipeline = new TranslationPipeline(_ocr ?? (IOcrEngine)new NoOcr(), Service, () => Settings);
        Pipeline.SecondOcr = f => _tess != null ? _tess.RecognizeAsync(f) : Task.FromResult("");
        ScreenPipeline = new ScreenTranslationPipeline((IScreenOcr?)_ocr ?? new NoScreenOcr(), Service, () => Settings);
        Overlay = new OverlayWindow();
        ScreenOverlay = new ScreenOverlayWindow();
        ScreenPipeline.Updated += lines => Application.Current.Dispatcher.BeginInvoke(() => ScreenOverlay.SetLines(lines));
        ScreenPipeline.TranslationFailed += f => Application.Current.Dispatcher.BeginInvoke(() =>
            Overlay.ShowError($"Překlad se nezdařil: {Short(f.Error)}"));

        Pipeline.Translated += r => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (Settings.Scope == TranslationScope.FullScreen) return;   // pozdní událost po přepnutí režimu
            var delay = r.DetectedUtc == default ? "?" : ((int)(DateTime.UtcNow - r.DetectedUtc).TotalMilliseconds).ToString();
            AppLog.Info($"{Loc.T("ZOBRAZENO")}{(r.Replace ? Loc.T(" (nahrazuje předchozí)") : "")}: {Loc.T("originál")}: {r.English}  →  {Loc.T("překlad")}: {r.Czech}  [{Loc.T(r.Provider)}, {(r.FromCache ? "cache" : r.TranslateMs + " ms")}; {Loc.T("od prvního spatření textu do zobrazení")} {delay} ms]"
                + (r.Box != null ? Loc.T($" [poloha textu: střed {r.Box.X + r.Box.W / 2:0.000}, šířka {r.Box.W:0.000}, výška {r.Box.H:0.000}, y {r.Box.Y:0.000}]") : Loc.T(" [poloha textu neznámá]")));
            if (r.Box != null) { _lastTextBox = r.Box; if ((Settings.OverlayAutoAbove || Settings.OverlayCoverOriginal) && !Overlay.EditMode) PlaceOverlayFromSettings(false); }
            Overlay.AddSubtitle(r.English, r.Czech, r.Replace, r.SourceGone);
            History.Add(r.English, r.Czech, r.Replace);
            if (Settings.WebEnabled) { _webGen++; Web.Update(r.English, r.Czech, true); }
        });
        Pipeline.SubtitleVisible += () => Application.Current.Dispatcher.BeginInvoke(Overlay.MarkVisible);
        Pipeline.TranslationFailed += f => Application.Current.Dispatcher.BeginInvoke(() =>
            Overlay.ShowError($"Překlad se nezdařil: {Short(f.Error)}"));
        Pipeline.SubtitleCleared += () => Application.Current.Dispatcher.BeginInvoke(() => { Overlay.OnSourceCleared(); HideWebLater(); });
    }

    static string Short(string s) => s.Length > 90 ? s[..90] + "…" : s;

    sealed class NoScreenOcr : IScreenOcr
    {
        public int MaxDimension => 2000;
        public Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Frame f) => Task.FromResult<IReadOnlyList<OcrLine>>(Array.Empty<OcrLine>());
    }

    sealed class NoOcr : IOcrEngine
    {
        public int MaxDimension => 2000;
        public Task<string> RecognizeAsync(Frame f) => Task.FromResult("");
    }

    // ---------- životní cyklus
    public void InitOverlay()
    {
        Overlay.Show();
        Overlay.ApplySettings(Settings);
        PlaceOverlayFromSettings(forceDefault: false);
        Overlay.Visibility = Settings.OverlayVisible ? Visibility.Visible : Visibility.Hidden;
        ScreenOverlay.Show();
        ScreenOverlay.ApplySettings(Settings);
        PlaceScreenOverlay();
        ScreenOverlay.Visibility = Settings.OverlayVisible ? Visibility.Visible : Visibility.Hidden;
        Overlay.GeometryChanged += (x, y, w, h) =>
        {
            Settings.OverlayAutoAbove = false;
            Settings.OverlayLeft = x; Settings.OverlayTop = y; Settings.OverlayWidth = w; Settings.OverlayHeight = h;
        };
        _watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += async (_, _) => await WatchdogAsync();
        _watchdog.Start();
    }

    public Native.RECT TargetRect()
    {
        if (Settings.Source == SourceKind.Window || Settings.Source == SourceKind.Program)
        {
            var w = Settings.Source == SourceKind.Program ? SourceFactory.FindProgramWindow(Settings.ProgramName) : SourceFactory.FindWindow(Settings.WindowMatch);
            if (w != null) return w.Rect;
        }
        var mons = MonitorEnumerator.List();
        if (mons.Count > 0) return mons[Math.Clamp(Settings.MonitorIndex, 0, mons.Count - 1)].Rect;
        return new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>Obraz hry v pixelech obrazovky (okno / monitor zúžený o černé pruhy).</summary>
    public (int L, int T, int W, int H) GameRectPx()
    {
        var t = TargetRect(); var g = Settings.AutoGameArea ? Settings.GameArea : RegionF.Full;
        return (t.Left + (int)(g.X * t.Width), t.Top + (int)(g.Y * t.Height), Math.Max(1, (int)(g.W * t.Width)), Math.Max(1, (int)(g.H * t.Height)));
    }

    /// <summary>Aktuální překryv jako relativní obdélník vůči obrazu hry (pro grafický editor).</summary>
    public RegionF OverlayAsRegion()
    {
        var g = GameRectPx();
        if (Settings.OverlayWidth <= 0 || g.W <= 0 || g.H <= 0) return new RegionF { X = 0.15, Y = 0.7, W = 0.7, H = 0.14 };
        return new RegionF
        {
            X = (Settings.OverlayLeft - g.L) / (double)g.W, Y = (Settings.OverlayTop - g.T) / (double)g.H,
            W = Settings.OverlayWidth / (double)g.W, H = Settings.OverlayHeight / (double)g.H
        }.Clamp();
    }

    /// <summary>Zjistí obraz hry (černé pruhy) z celého snímku; vrací true, když se oblast znatelně změnila.</summary>
    public bool DetectGameArea(Frame full)
    {
        var det = Settings.AutoGameArea ? (GameAreaDetector.Detect(full) ?? RegionF.Full) : RegionF.Full;
        var o = Settings.GameArea;
        bool changed = Math.Abs(det.X - o.X) > 0.01 || Math.Abs(det.Y - o.Y) > 0.01 || Math.Abs(det.W - o.W) > 0.01 || Math.Abs(det.H - o.H) > 0.01;
        if (changed)
        {
            Settings.GameArea = det;
            AppLog.Info($"Obraz hry: {(det.W >= 0.999 && det.H >= 0.999 ? "celý snímek" : $"x {det.X:0.000}, y {det.Y:0.000}, šířka {det.W:0.000}, výška {det.H:0.000} (černé pruhy ořezány)")}");
        }
        return changed;
    }

    int _gaTick;
    async Task RefreshGameAreaAsync(bool force = false)
    {
        if (!Settings.AutoGameArea || ActiveSource == null) return;
        if (!force && ++_gaTick % 10 != 0) return;
        var full = await ActiveSource.GrabAsync(RegionF.Full);
        if (full != null && DetectGameArea(full)) { Pipeline.ApplySettings(); PlaceOverlayFromSettings(false); }
    }

    public PlacementPreset CurrentPreset => Settings.OverlayCoverOriginal ? PlacementPreset.Cover : Settings.OverlayAutoAbove ? PlacementPreset.Above : PlacementPreset.Custom;

    public void ApplyPlacement(PlacementResult r)
    {
        Settings.Region = r.Region;
        Settings.OverlayCoverOriginal = r.Preset == PlacementPreset.Cover;
        Settings.OverlayAutoAbove = r.Preset == PlacementPreset.Above;
        var g = GameRectPx();
        Settings.OverlayLeft = g.L + (int)(r.Overlay.X * g.W); Settings.OverlayTop = g.T + (int)(r.Overlay.Y * g.H);
        Settings.OverlayWidth = Math.Max(50, (int)(r.Overlay.W * g.W)); Settings.OverlayHeight = Math.Max(30, (int)(r.Overlay.H * g.H));
        Overlay.ApplySettings(Settings);
        PlaceOverlayFromSettings(false);
        Pipeline.ApplySettings();
        Settings.Save();
    }

    public void PlaceScreenOverlay() => ScreenOverlay.PlaceOver(TargetRect());

    public void SetOverlayVisible(bool show)
    {
        Settings.OverlayVisible = show;
        var v = show ? Visibility.Visible : Visibility.Hidden;
        Overlay.Visibility = v; ScreenOverlay.Visibility = v;
        if (show) { Overlay.ReassertTopmost(); ScreenOverlay.ReassertTopmost(); }
    }

    public void PlaceOverlayFromSettings(bool forceDefault)
    {
        if (Settings.OverlayCoverOriginal && !Overlay.EditMode && !forceDefault)
        {
            // přímo přes originální titulek: okno vystředěné na jeho polohu, box je aspoň tak velký jako originál (+ rezerva)
            // bez skutečně změřeného originálu (např. ukázka) box nic nevynucuje a jen obepne text
            var t = TargetRect(); var r = _lastTextBox ?? Settings.EffectiveRegion;
            double wpx = r.W * t.Width, hpx = r.H * t.Height;
            int cx = t.Left + (int)((r.X + r.W / 2) * t.Width), cy = t.Top + (int)((r.Y + r.H / 2) * t.Height);
            double minW = _lastTextBox != null ? Math.Min(wpx + 32, t.Width * 0.7) : 0, minH = _lastTextBox != null ? Math.Min(hpx + 16, t.Height * 0.15) : 0;
            int w = (int)Math.Min(t.Width * 0.9, Math.Max(minW * 1.35, t.Width * 0.5));   // okno je průhledné, box uvnitř jen obepíná text
            w = (int)Math.Min(w, 2.0 * Math.Min(cx - t.Left, t.Right - cx));
            w = Math.Max(w, (int)Math.Min(minW, t.Width));
            int h = (int)Math.Min(t.Height * 0.4, Math.Max(minH * 2.2, t.Height * 0.2));
            int y = Math.Clamp(cy - h / 2, t.Top, Math.Max(t.Top, t.Bottom - h));
            w = Math.Min(w, t.Width);
            Settings.OverlayLeft = Math.Clamp(cx - w / 2, t.Left, Math.Max(t.Left, t.Right - w)); Settings.OverlayTop = y; Settings.OverlayWidth = w; Settings.OverlayHeight = h;
            Overlay.SetCover(true, minW, minH);
        }
        else if (Settings.OverlayAutoAbove && !Overlay.EditMode && !forceDefault)
        {
            Overlay.SetCover(false, 0, 0);
            // těsně NAD skutečným originálním titulkem (poloha světlého textu), vystředěno na něj;
            // než se text poprvé najde, použije se vybraná oblast
            var t = TargetRect(); var r = _lastTextBox ?? Settings.EffectiveRegion;
            int cx = t.Left + (int)((r.X + r.W / 2) * t.Width), top = t.Top + (int)(r.Y * t.Height);
            int w = (int)Math.Min(t.Width * 0.8, 2.0 * Math.Min(cx - t.Left, t.Right - cx));
            w = Math.Max(w, Math.Min(300, t.Width));
            int h = Math.Min((int)(t.Height * 0.22), Math.Max(60, top - 6 - t.Top));
            Settings.OverlayLeft = Math.Clamp(cx - w / 2, t.Left, Math.Max(t.Left, t.Right - w)); Settings.OverlayTop = Math.Max(t.Top, top - 6 - h);
            Settings.OverlayWidth = w; Settings.OverlayHeight = h;
        }
        else if (forceDefault || Settings.OverlayWidth <= 0 || Settings.OverlayHeight <= 0)
        {
            var d = Overlay.DefaultPlacement(TargetRect());
            Settings.OverlayLeft = d.X; Settings.OverlayTop = d.Y; Settings.OverlayWidth = d.W; Settings.OverlayHeight = d.H;
        }
        Overlay.PlacePx(Settings.OverlayLeft, Settings.OverlayTop, Settings.OverlayWidth, Settings.OverlayHeight);
    }

    public void SetSettings(AppSettings s)
    {
        s.Normalize();
        Settings = s;
        _tess = TesseractOcr.TryCreate(s.TesseractPath);
        if ((int)_http.Timeout.TotalSeconds != s.HttpTimeoutSec) { _http = TranslatorFactory.NewHttp(s.HttpTimeoutSec); }
        Service.Translator = TranslatorFactory.Create(s, _http, Protector);
        Service.RetryCount = s.RetryCount;
        Service.LangTag = Languages.PairTag(s.SourceLang, s.TargetLang);
        Service.Glossary = Glossary.Parse(s.Glossary);
        Overlay.ApplySettings(s); ScreenOverlay.ApplySettings(s);
        PlaceOverlayFromSettings(false); PlaceScreenOverlay();
        Pipeline.ApplySettings(); ScreenPipeline.ApplySettings();
        ApplyWeb();
        s.Save();
    }

    public async Task<bool> StartTranslationAsync()
    {
        if (_starting) return false;
        _starting = true;
        try
        {
            if (!OcrAvailable) { LastStartError = WindowsOcrEngine.MissingLanguageHelp; AppLog.Error(LastStartError); return false; }
            var src = SourceFactory.Create(Settings);
            PlaceScreenOverlay();
            if (Settings.Scope == TranslationScope.FullScreen)
            {
                Overlay.ClearAll();
                await ScreenPipeline.StartAsync(src);
            }
            else
            {
                ScreenOverlay.Clear();
                await Pipeline.StartAsync(src);
            }
            TranslationEnabled = true; LastStartError = null;
            _ = Task.Run(async () => { try { await Task.Delay(800); await RefreshGameAreaAsync(true); } catch (Exception e) { AppLog.Warn("Zjištění obrazu hry selhalo: " + e.Message); } });
            Pipeline.OcrPaused = false; ScreenPipeline.OcrPaused = false;
            return true;
        }
        catch (Exception e)
        {
            LastStartError = e.Message;
            AppLog.Error($"Start překladu selhal: {e.Message}");
            TranslationEnabled = false;
            try { await Pipeline.StopAsync(); await ScreenPipeline.StopAsync(); } catch { }
            return false;
        }
        finally { _starting = false; }
    }

    public async Task StopTranslationAsync()
    {
        TranslationEnabled = false;
        await Pipeline.StopAsync();
        await ScreenPipeline.StopAsync();
        Overlay.ClearAll(); ScreenOverlay.Clear();
    }

    async Task WatchdogAsync()
    {
        try
        {
            Overlay.HoldLive = OcrPaused;
            if (Overlay.Visibility == Visibility.Visible && !Overlay.EditMode) Overlay.ReassertTopmost();
            if (ScreenOverlay.Visibility == Visibility.Visible) ScreenOverlay.ReassertTopmost();
            SampleMetrics();
            if (++_wdTicks % 120 == 0) { var c = Cache; _ = Task.Run(() => c.Save(_cachePath)); }
            if (!TranslationEnabled || _starting) return;
            await RefreshGameAreaAsync();
            var asrc = ActiveSource;
            var err = asrc?.LastError;
            bool stalled = Settings.Source != SourceKind.ImageFile && asrc != null && asrc.Fps == 0 && err != null;
            if ((err != null && err.StartsWith("Zdrojové")) || stalled)
            {
                if ((DateTime.UtcNow - _lastRestart).TotalSeconds < 5) return;
                _lastRestart = DateTime.UtcNow;
                AppLog.Warn($"Zdroj hlásí problém ({err}) – zkouším ho znovu připojit.");
                await Pipeline.StopAsync(); await ScreenPipeline.StopAsync();
                await StartTranslationAsync();
            }
        }
        catch (Exception e) { AppLog.Error($"Watchdog: {e.Message}"); }
    }

    public ICaptureSource? ActiveSource => Settings.Scope == TranslationScope.FullScreen ? ScreenPipeline.Source : Pipeline.Source;
    public PipelineStats ActiveStats => Settings.Scope == TranslationScope.FullScreen ? ScreenPipeline.Stats : Pipeline.Stats;
    public bool ActiveRunning => Settings.Scope == TranslationScope.FullScreen ? ScreenPipeline.Running : Pipeline.Running;
    public bool OcrPaused
    {
        get => Settings.Scope == TranslationScope.FullScreen ? ScreenPipeline.OcrPaused : Pipeline.OcrPaused;
        set { Pipeline.OcrPaused = value; ScreenPipeline.OcrPaused = value; }
    }

    void SampleMetrics()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            var now = DateTime.UtcNow; var cpu = p.TotalProcessorTime;
            double dt = (now - _lastCpuAt).TotalSeconds;
            if (dt >= 1) { CpuPercent = (cpu - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100; _lastCpu = cpu; _lastCpuAt = now; }
            RamMb = p.WorkingSet64 / 1048576.0;
        }
        catch { }
    }

    // ---------- pomocné akce pro UI
    public async Task<Frame?> GrabOnceAsync(RegionF region)
    {
        if (ActiveRunning && ActiveSource != null)
            return await ActiveSource.GrabAsync(region);
        await using var src = SourceFactory.Create(Settings);
        await src.StartAsync();
        for (int i = 0; i < 20; i++)
        {
            var f = await src.GrabAsync(region);
            if (f != null) return f;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>OCR celého snímku s polohou řádků (pro diagnostiku režimu „vše na obrazovce“).</summary>
    public async Task<(IReadOnlyList<OcrLine> Lines, int Ms)> OcrLinesOnceAsync(Frame full)
    {
        if (_ocr == null) throw new InvalidOperationException(WindowsOcrEngine.MissingLanguageHelp);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = await _ocr.RecognizeLinesAsync(full);
        return (lines, (int)sw.ElapsedMilliseconds);
    }

    public async Task<(string Text, string Method, int Ms)> OcrOnceAsync(Frame crop)
    {
        if (_ocr == null) throw new InvalidOperationException(WindowsOcrEngine.MissingLanguageHelp);
        var o = new OcrOptions { Mode = Settings.OcrMode, WhiteThreshold = Settings.WhiteThreshold, Upscale = Settings.Upscale, MaxDimension = _ocr.MaxDimension, MinLetters = Settings.MinLetters };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await OcrStrategy.RunAsync(crop, o, _ocr.RecognizeAsync, Settings.UseTesseract && _tess != null ? _tess.RecognizeAsync : null);
        return (r.Text, r.Method, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>Pro diagnostiku: spustí Tesseract na výřezu (izolovaný světlý text) a vrátí text a čas; null = Tesseract není k dispozici.</summary>
    public async Task<(string Text, int Ms)?> TesseractOnceAsync(Frame crop)
    {
        if (_tess == null) return null;
        var o = Settings;
        var scaled = ImagePrep.ScaleBilinear(crop, Math.Max(1, o.Upscale));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = await _tess.RecognizeAsync(ImagePrep.IsolateLight(scaled, o.WhiteThreshold));
        return (TextTools.Normalize(t), (int)sw.ElapsedMilliseconds);
    }

    public async Task<List<string>> OcrAccuracyOnTestImagesAsync(string dir)
    {
        var lines = new List<string>();
        if (_ocr == null) { lines.Add(WindowsOcrEngine.MissingLanguageHelp); return lines; }
        if (!Directory.Exists(dir)) { lines.Add("Složka s testovacími obrázky nenalezena: " + dir); return lines; }
        var files = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToList();
        if (files.Count == 0) { lines.Add("Ve složce nejsou žádné .png."); return lines; }
        double[] sum = new double[3]; int n = 0;
        lines.Add("Test přesnosti OCR (CER = chybovost znaků, nižší je lepší). Oblast: aktuální nastavená.");
        lines.Add($"{"obrázek",-26} {"Auto",8} {"Izol.",8} {"Orig.",8}   čas Auto");
        foreach (var f in files)
        {
            var txt = Path.ChangeExtension(f, ".txt");
            if (!File.Exists(txt)) continue;
            var expected = File.ReadAllText(txt).Trim();
            var full = await BitmapUtil.LoadFileAsync(f);
            var crop = FrameCrop.Crop(full, Settings.EffectiveRegion);
            var cers = new double[3]; int ms = 0;
            var modes = new[] { OcrMode.Auto, OcrMode.WhiteIsolated, OcrMode.Original };
            for (int i = 0; i < 3; i++)
            {
                var o = new OcrOptions { Mode = modes[i], WhiteThreshold = Settings.WhiteThreshold, Upscale = Settings.Upscale, MaxDimension = _ocr.MaxDimension, MinLetters = Settings.MinLetters };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await OcrStrategy.RunAsync(crop, o, _ocr.RecognizeAsync);
                if (i == 0) ms = (int)sw.ElapsedMilliseconds;
                cers[i] = Math.Min(1.0, TextTools.Cer(expected, r.Text));
                sum[i] += cers[i];
            }
            n++;
            lines.Add($"{Path.GetFileNameWithoutExtension(f),-26} {cers[0],8:P1} {cers[1],8:P1} {cers[2],8:P1}   {ms} ms");
        }
        if (n > 0) lines.Add($"{"PRŮMĚR",-26} {sum[0] / n,8:P1} {sum[1] / n,8:P1} {sum[2] / n,8:P1}");
        lines.Add("Pozn.: jde o syntetické obrázky (DejaVu Sans, vymyšlené věty). Skutečné titulky ze hry se mohou chovat jinak.");
        return lines;
    }

    public ITranslator BuildTranslatorForTest(AppSettings s) => TranslatorFactory.Create(s, _http, Protector);

    public void SaveCache() => Cache.Save(_cachePath);

    public async Task ShutdownAsync()
    {
        _watchdog?.Stop();
        try { await Pipeline.StopAsync(); await ScreenPipeline.StopAsync(); } catch { }
        SaveCache();
        Web.Dispose();
        Settings.Save();
    }
}
