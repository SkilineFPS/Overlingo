using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Overlingo.App.Capture;
using Overlingo.App.Win;
using Overlingo.Core;
using Microsoft.Win32;

namespace Overlingo.App;

public partial class MainWindow : Window
{
    readonly AppController _c = new();
    readonly DispatcherTimer _dash = new() { Interval = TimeSpan.FromMilliseconds(500) };
    HotkeyManager? _hot;
    bool _ui;          // UI je načtené (potlačení událostí během inicializace)
    bool _closing;

    AppSettings S => _c.Settings;

    void ApplyAdvancedUi()
    {
        var vis = S.ShowAdvancedUi ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in new[] { TabOcr, TabAdvanced, TabDiag, TabLogs, TabTest }) t.Visibility = vis;
        if (Tabs.SelectedItem is TabItem cur && cur.Visibility != Visibility.Visible) Tabs.SelectedItem = TabHome;
    }

    void OnAdvancedUiToggled(object sender, RoutedEventArgs e)
    {
        S.ShowAdvancedUi = CbAdvancedUi.IsChecked == true;
        ApplyAdvancedUi();
        try { S.Save(); } catch { }
    }

    void SetCheck(TextBlock tb, bool ok, string text, bool warnIfNot = true)
    {
        tb.Text = (ok ? "✔  " : "○  ") + text;
        tb.Foreground = Res(ok ? "Good" : warnIfNot ? "Warn" : "Muted");
    }

    void UpdateQuickSetup()
    {
        SetCheck(ChkOcr, _c.OcrAvailable, _c.OcrAvailable ? Loc.T("Rozpoznávání textu (OCR) je připravené") : Loc.T("Chybí jazykový balíček Windows OCR – viz Úvodní nastavení"));
        bool srcOk = S.Source switch
        {
            SourceKind.Window => !string.IsNullOrWhiteSpace(S.WindowMatch),
            SourceKind.Program => !string.IsNullOrWhiteSpace(S.ProgramName),
            _ => true
        };
        SetCheck(ChkSource, srcOk, srcOk ? Loc.T("Zdroj obrazu: ") + Loc.T(_c.SourceDescription) : Loc.T("Zvolte, odkud se čte obraz (průvodce nastavením)"));
        SetCheck(ChkTranslator, true, Loc.T("Překladač: ") + _c.Service.Translator.Name);
    }

    void OnUiLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ui || CbUiLang.SelectedItem is not UiLanguage ul || ul.Code == Loc.Current) return;
        Loc.Set(ul.Code);
        UiLocalizer.ApplyAll();
        SetStatus(Loc.T("Jazyk rozhraní změněn. Zbylé texty se obnoví po restartu aplikace."));
    }

    public MainWindow()
    {
        InitializeComponent();
        CbOcrMode.ItemsSource = new[] { Loc.T("Auto (doporučeno)"), Loc.T("Izolace bílého textu"), Loc.T("Originální obraz") };
        CbTranslator.ItemsSource = new[] { Loc.T("Google Translate (neoficiální, bez klíče) – výchozí"), Loc.T("Google Cloud Translation (oficiální API, klíč)"), Loc.T("DeepL (API klíč Free / Pro)"), Loc.T("Gemini (AI, API klíč)"), Loc.T("AI – OpenAI-kompatibilní / Ollama / LM Studio") };
        LoadUi();
        _ui = true;
        OnSourceKindChanged(this, new RoutedEventArgs());
        OnTranslatorChanged(this, null!);

        foreach (var l in AppLog.Snapshot()) LogBox.AppendText(l + Environment.NewLine);
        AppLog.LineAdded += l => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(l + Environment.NewLine);
            if (LogBox.Text.Length > 300_000) LogBox.Text = LogBox.Text[^150_000..];
            LogBox.ScrollToEnd();
        });

        _dash.Tick += (_, _) => UpdateDashboard();
        _dash.Start();
        Loaded += (_, _) => Startup();
        SourceInitialized += (_, _) =>
        {   // tmavý titulek okna (Windows 10 20H1+ / 11)
            try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); } catch { }
        };
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    Brush Res(string key) => (Brush)FindResource(key);

    void Startup()
    {
        _c.InitOverlay();
        SetupHost.Content = SetupPanel.Build(_c);
        ApplyHotkeys();
        AppLog.Info($"Aplikace spuštěna. OCR: {_c.OcrInfo}");
        if (!_c.OcrAvailable) SetStatus("Chybí anglický OCR balíček Windows – viz Přehled.");
        if (Environment.GetCommandLineArgs().Contains("--minimized")) WindowState = WindowState.Minimized;
        if (!S.FirstRunDone) { RunWizard(); }
        else if (S.StartTranslationOnLaunch) _ = StartOnLaunchAsync();
        _progTimer.Tick += async (_, _) => await CheckProgramAsync();
        _progTimer.Start();
    }

    void RunWizard()
    {
        var w = new WizardWindow(S, _c) { Owner = this.IsVisible ? this : null };
        bool ok = w.ShowDialog() == true;
        S.FirstRunDone = true;
        _c.SetSettings(S); LoadUi();
        if (ok) { SetStatus("Základní nastavení hotovo."); if (w.OpenEditorAfter) OnPlacementEditor(this, new RoutedEventArgs()); }
    }
    void OnRunWizard(object s, RoutedEventArgs e) => RunWizard();

    readonly DispatcherTimer _progTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _manualStop, _progBusy;

    async Task StartOnLaunchAsync()
    {
        bool ok = await _c.StartTranslationAsync();
        SetStatus(ok ? "Překlad spuštěn automaticky." : "Automatický start selhal: " + _c.LastStartError);
    }

    /// <summary>Zdroj „Program“: když se hra spustí, překlad se zapne; po ručním zastavení se to znovu nestane, dokud hra neskončí.</summary>
    async Task CheckProgramAsync()
    {
        if (_progBusy || !S.AutoStartWhenProgramRuns || S.Source != SourceKind.Program) return;
        _progBusy = true;
        try
        {
            bool running = SourceFactory.FindProgramWindow(S.ProgramName) != null;
            if (!running) { _manualStop = false; return; }
            if (!_c.TranslationEnabled && !_manualStop)
            {
                bool ok = await _c.StartTranslationAsync();
                SetStatus(ok ? $"Program „{S.ProgramName}“ běží – překlad spuštěn." : "Automatický start selhal: " + _c.LastStartError);
            }
        }
        finally { _progBusy = false; }
    }

    void SetStatus(string s) => StatusBar.Text = Loc.T(s);

    // ================= zkratky =================
    void ApplyHotkeys()
    {
        _hot?.Dispose();
        _hot = new HotkeyManager(this);
        _hot.Register(1, S.HkToggle, "překlad zap/vyp", () => Dispatcher.BeginInvoke(new Action(async () => await ToggleTranslationAsync())));
        _hot.Register(2, S.HkOverlay, "overlay zobrazit/skrýt", () => Dispatcher.BeginInvoke(new Action(ToggleOverlay)));
        _hot.Register(3, S.HkPause, "pauza OCR", () => Dispatcher.BeginInvoke(new Action(TogglePause)));
        _hot.Register(4, S.HkRegion, "výběr oblasti", () => Dispatcher.BeginInvoke(new Action(async () => await SelectRegionAsync())));
        _hot.Register(5, S.HkSettings, "otevřít nastavení", () => Dispatcher.BeginInvoke(new Action(ShowSettings)));
        _hot.Register(6, S.HkScope, "přepnout režim", () => Dispatcher.BeginInvoke(new Action(async () => await ToggleScopeAsync())));
        _hot.Register(8, S.HkCorrect, "špatný překlad", () => Dispatcher.BeginInvoke(new Action(ShowCorrection)));
        _hot.Register(7, S.HkHistory, "historie titulků", () => Dispatcher.BeginInvoke(new Action(ShowHistory)));
        var sb = new StringBuilder();
        if (_hot.Failed.Count == 0 && _hot.Fallback.Count == 0) sb.Append("Všechny zkratky jsou zaregistrovány.");
        if (_hot.Fallback.Count > 0) sb.Append("Obsazené zkratky nahrazeny: " + string.Join(", ", _hot.Fallback) + ". ");
        if (_hot.Failed.Count > 0) sb.Append("NEPODAŘILO SE zaregistrovat: " + string.Join(", ", _hot.Failed) + " – zvolte jinou kombinaci.");
        HotkeyInfo.Text = sb.ToString();
    }

    void OnHotkeyKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var tb = (TextBox)sender;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Back) { tb.Text = (string)tb.Tag; return; }
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.None) return;
        uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        uint mods = 0; var km = System.Windows.Input.Keyboard.Modifiers;
        if ((km & System.Windows.Input.ModifierKeys.Control) != 0) mods |= HotkeyParser.Ctrl;
        if ((km & System.Windows.Input.ModifierKeys.Alt) != 0) mods |= HotkeyParser.Alt;
        if ((km & System.Windows.Input.ModifierKeys.Shift) != 0) mods |= HotkeyParser.Shift;
        var text = HotkeyParser.Format(mods, vk);
        if (text == "") { SetStatus("Tuto klávesu pro zkratku použít nelze."); return; }
        tb.Text = text;
    }

    // ================= režim překladu =================
    async void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ui) return;
        var scope = RbScopeFull.IsChecked == true ? TranslationScope.FullScreen : TranslationScope.DialogOnly;
        if (scope == S.Scope) return;
        S.Scope = scope; S.Save();
        await ApplyScopeAsync();
    }

    async Task ToggleScopeAsync()
    {
        S.Scope = S.Scope == TranslationScope.DialogOnly ? TranslationScope.FullScreen : TranslationScope.DialogOnly;
        _ui = false; RbScopeDialog.IsChecked = S.Scope == TranslationScope.DialogOnly; RbScopeFull.IsChecked = S.Scope == TranslationScope.FullScreen; _ui = true;
        S.Save();
        await ApplyScopeAsync();
    }

    async Task ApplyScopeAsync()
    {
        string name = S.Scope == TranslationScope.FullScreen ? "vše na obrazovce" : "jen konverzace";
        if (_c.TranslationEnabled)
        {
            await _c.StopTranslationAsync();
            bool ok = await _c.StartTranslationAsync();
            SetStatus(ok ? $"Režim: {name}." : "Režim přepnut, ale start selhal: " + _c.LastStartError);
        }
        else SetStatus($"Režim: {name} (po spuštění překladu).");
    }

    // ================= diagnostika =================
    async void OnRunDiagnostics(object sender, RoutedEventArgs e)
    {
        BtnDiag.IsEnabled = false; DiagBox.Text = "Probíhá diagnostika…";
        try { CollectUi(); var lines = await DiagnosticsRunner.RunAsync(_c, _hot, p => Dispatcher.BeginInvoke(() => SetStatus("Diagnostika: " + p))); DiagBox.Text = string.Join(Environment.NewLine, lines); SetStatus("Diagnostika dokončena."); }
        catch (Exception ex) { DiagBox.Text = "Diagnostika selhala: " + ex; AppLog.Error("Diagnostika: " + ex); }
        finally { BtnDiag.IsEnabled = true; }
    }

    void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagBox.Text)) { SetStatus("Nejdřív spusťte diagnostiku."); return; }
        try
        {
            var dir = Path.Combine(AppSettings.DefaultDir, "diagnostika"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"diagnostika_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, DiagBox.Text, Encoding.UTF8);
            SetStatus("Report uložen: " + path);
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
        catch (Exception ex) { SetStatus("Uložení selhalo: " + ex.Message); }
    }

    // ================= načtení / sběr nastavení =================
    void LoadUi()
    {
        var s = S;
        RbWindow.IsChecked = s.Source == SourceKind.Window || s.Source == SourceKind.ImageFile; RbMonitor.IsChecked = s.Source == SourceKind.Monitor;
        RbElgato.IsChecked = s.Source == SourceKind.Elgato; RbProgram.IsChecked = s.Source == SourceKind.Program;
        RefreshSourceLists(); _ = RefreshCardsAsync(s.CaptureDevice);
        CbWindow.Text = s.WindowMatch; CbProgram.Text = s.ProgramName;
        CbMonitor.ItemsSource = MonitorEnumerator.List();
        var mons = (List<MonitorInfo>)CbMonitor.ItemsSource;
        CbMonitor.SelectedIndex = mons.Count == 0 ? -1 : Math.Clamp(s.MonitorIndex, 0, mons.Count - 1);
        UpdateRegionText();
        TbInterval.Text = s.IntervalMs.ToString(); CbOcrMode.SelectedIndex = (int)s.OcrMode;
        TbThreshold.Text = s.WhiteThreshold.ToString(); TbUpscale.Text = s.Upscale.ToString();
        TbStable.Text = s.StableReads.ToString(); TbChange.Text = s.ChangeThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        TbMinLetters.Text = s.MinLetters.ToString(); CbSkipNoText.IsChecked = s.SkipWhenNoLightText;
        CbTesseract.IsChecked = s.UseTesseract; TbTessPath.Text = s.TesseractPath; TessInfo.Text = _c.TesseractInfo;
        OcrEngineInfo.Text = "OCR engine: " + _c.OcrInfo;
        CbTranslator.SelectedIndex = (int)s.Translator;
        CbContext.IsChecked = s.UseContext; TbCtxLines.Text = s.ContextLines.ToString();
        UpdateKeyStates();
        TbFont.Text = s.FontFamily; TbFontSize.Text = s.FontSize.ToString(CultureInfo.InvariantCulture);
        TbColor.Text = s.TextColor; TbEnColor.Text = s.EnglishColor; SlOpacity.Value = s.BackgroundOpacity;
        TbMaxLines.Text = s.MaxLines.ToString(); TbSeconds.Text = s.DisplaySeconds.ToString(CultureInfo.InvariantCulture);
        CbAutoAbove.IsChecked = s.OverlayAutoAbove; CbCover.IsChecked = s.OverlayCoverOriginal; CbShowEn.IsChecked = s.ShowEnglish; CbClickThrough.IsChecked = s.ClickThrough; CbExclude.IsChecked = s.ExcludeOverlayFromCapture;
        RbScopeDialog.IsChecked = s.Scope == TranslationScope.DialogOnly; RbScopeFull.IsChecked = s.Scope == TranslationScope.FullScreen;
        CbAdaptive.IsChecked = s.AdaptiveInterval; CbStripJunk.IsChecked = s.StripJunkTokens; CbVerbose.IsChecked = s.VerboseLog;
        TbCacheMax.Text = s.CacheMax.ToString(); TbHttpTimeout.Text = s.HttpTimeoutSec.ToString(); TbRetry.Text = s.RetryCount.ToString();
        TbScreenScale.Text = s.ScreenFontScale.ToString(CultureInfo.InvariantCulture); TbScreenInterval.Text = s.ScreenMinIntervalMs.ToString();
        TbIgnore.Text = s.IgnoreContains; TbMaxChars.Text = s.MaxSubtitleChars.ToString(); CbGameArea.IsChecked = s.AutoGameArea;
        HkToggle.Text = s.HkToggle; HkOverlay.Text = s.HkOverlay; HkPause.Text = s.HkPause; HkRegion.Text = s.HkRegion; HkSettings.Text = s.HkSettings; HkScope.Text = s.HkScope; HkHistory.Text = s.HkHistory; HkCorrect.Text = s.HkCorrect;
        CbSrcLang.ItemsSource = Languages.All; CbTgtLang.ItemsSource = Languages.All;
        CbSrcLang.SelectedItem = Languages.Find(s.SourceLang); CbTgtLang.SelectedItem = Languages.Find(s.TargetLang);
        TbGlossary.Text = s.Glossary;
        TbGeminiModel.Text = s.GeminiModel; TbAiUrl.Text = s.AiBaseUrl; TbAiModel.Text = s.AiModel; TbAiContext.Text = s.AiGameContext;
        var langs = Loc.Available(); CbUiLang.ItemsSource = langs; CbUiLang.SelectedItem = langs.FirstOrDefault(l => l.Code == Loc.Current);
        CbAdvancedUi.IsChecked = s.ShowAdvancedUi; ApplyAdvancedUi();
        CbAutostart.IsChecked = Autostart.IsEnabled(); CbStartTranslate.IsChecked = s.StartTranslationOnLaunch; CbAutoProgram.IsChecked = s.AutoStartWhenProgramRuns;
        CbWeb.IsChecked = s.WebEnabled; TbWebPort.Text = s.WebPort.ToString();
        WebUrlText.Text = s.WebEnabled ? $"Adresa pro OBS: http://127.0.0.1:{s.WebPort}/" : "";
        RefreshProfiles();
    }

    static bool TryD(string t, out double v) => double.TryParse((t ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    static int I(TextBox tb, int cur, List<string> bad, string name)
    {
        if (int.TryParse(tb.Text.Trim(), out var v)) return v;
        bad.Add(name); return cur;
    }
    static double D(TextBox tb, double cur, List<string> bad, string name)
    {
        if (TryD(tb.Text, out var v)) return v;
        bad.Add(name); return cur;
    }

    /// <summary>Přečte hodnoty z UI do nastavení. Vrací seznam polí s neplatnou hodnotou (ta zůstanou beze změny).</summary>
    List<string> CollectUi()
    {
        var bad = new List<string>(); var s = S;
        s.Source = RbMonitor.IsChecked == true ? SourceKind.Monitor : RbElgato.IsChecked == true ? SourceKind.Elgato : RbProgram.IsChecked == true ? SourceKind.Program : SourceKind.Window;
        s.WindowMatch = (CbWindow.SelectedItem as WindowChoice)?.Match ?? CbWindow.Text.Trim();
        s.ProgramName = CbProgram.Text.Trim();
        s.CaptureDevice = (CbCapture.SelectedItem as CardChoice)?.Name ?? s.CaptureDevice;
        if (CbMonitor.SelectedIndex >= 0) s.MonitorIndex = CbMonitor.SelectedIndex;
        s.IntervalMs = I(TbInterval, s.IntervalMs, bad, "Interval");
        s.OcrMode = (OcrMode)Math.Max(0, CbOcrMode.SelectedIndex);
        s.WhiteThreshold = I(TbThreshold, s.WhiteThreshold, bad, "Práh světlosti");
        s.Upscale = I(TbUpscale, s.Upscale, bad, "Zvětšení");
        s.StableReads = I(TbStable, s.StableReads, bad, "Shodná čtení");
        s.ChangeThreshold = D(TbChange, s.ChangeThreshold, bad, "Práh změny");
        s.MinLetters = I(TbMinLetters, s.MinLetters, bad, "Min. písmen");
        s.SkipWhenNoLightText = CbSkipNoText.IsChecked == true;
        s.UseTesseract = CbTesseract.IsChecked == true; s.TesseractPath = TbTessPath.Text.Trim();
        s.Translator = (TranslatorKind)Math.Max(0, CbTranslator.SelectedIndex);
        s.UseContext = CbContext.IsChecked == true;
        s.ContextLines = I(TbCtxLines, s.ContextLines, bad, "Počet vět kontextu");
        if (PbGoogle.Password.Length > 0) { s.GoogleCloudKeyProtected = _c.Protector.Protect(PbGoogle.Password.Trim()); PbGoogle.Clear(); }
        if (PbDeepL.Password.Length > 0) { s.DeepLKeyProtected = _c.Protector.Protect(PbDeepL.Password.Trim()); PbDeepL.Clear(); }
        s.FontFamily = TbFont.Text.Trim();
        s.FontSize = D(TbFontSize, s.FontSize, bad, "Velikost písma");
        s.TextColor = TbColor.Text.Trim(); s.EnglishColor = TbEnColor.Text.Trim();
        s.BackgroundOpacity = SlOpacity.Value;
        s.MaxLines = I(TbMaxLines, s.MaxLines, bad, "Max. titulků");
        s.DisplaySeconds = D(TbSeconds, s.DisplaySeconds, bad, "Doba zobrazení");
        s.OverlayAutoAbove = CbAutoAbove.IsChecked == true; s.OverlayCoverOriginal = CbCover.IsChecked == true; s.ShowEnglish = CbShowEn.IsChecked == true; s.ClickThrough = CbClickThrough.IsChecked == true; s.ExcludeOverlayFromCapture = CbExclude.IsChecked == true;
        s.Scope = RbScopeFull.IsChecked == true ? TranslationScope.FullScreen : TranslationScope.DialogOnly;
        s.AdaptiveInterval = CbAdaptive.IsChecked == true; s.StripJunkTokens = CbStripJunk.IsChecked == true; s.VerboseLog = CbVerbose.IsChecked == true;
        s.CacheMax = I(TbCacheMax, s.CacheMax, bad, "Velikost cache"); s.HttpTimeoutSec = I(TbHttpTimeout, s.HttpTimeoutSec, bad, "Časový limit");
        s.RetryCount = I(TbRetry, s.RetryCount, bad, "Opakování");
        s.ScreenFontScale = D(TbScreenScale, s.ScreenFontScale, bad, "Velikost písma (obrazovka)"); s.ScreenMinIntervalMs = I(TbScreenInterval, s.ScreenMinIntervalMs, bad, "Interval (obrazovka)");
        s.IgnoreContains = TbIgnore.Text.Trim(); s.MaxSubtitleChars = I(TbMaxChars, s.MaxSubtitleChars, bad, "Max. délka titulku"); s.AutoGameArea = CbGameArea.IsChecked == true;
        s.HkToggle = HkToggle.Text; s.HkOverlay = HkOverlay.Text; s.HkPause = HkPause.Text; s.HkRegion = HkRegion.Text; s.HkSettings = HkSettings.Text; s.HkScope = HkScope.Text; s.HkHistory = HkHistory.Text; s.HkCorrect = HkCorrect.Text;
        s.SourceLang = (CbSrcLang.SelectedItem as Lang)?.Code ?? s.SourceLang; s.TargetLang = (CbTgtLang.SelectedItem as Lang)?.Code ?? s.TargetLang;
        s.Glossary = TbGlossary.Text;
        s.GeminiModel = TbGeminiModel.Text.Trim(); s.AiBaseUrl = TbAiUrl.Text.Trim(); s.AiModel = TbAiModel.Text.Trim(); s.AiGameContext = TbAiContext.Text.Trim();
        if (PbGemini.Password.Length > 0) { s.GeminiKeyProtected = _c.Protector.Protect(PbGemini.Password.Trim()); PbGemini.Clear(); }
        if (PbAi.Password.Length > 0) { s.AiKeyProtected = _c.Protector.Protect(PbAi.Password.Trim()); PbAi.Clear(); }
        if (CbUiLang.SelectedItem is UiLanguage ul) s.UiLang = ul.Code;
        s.StartTranslationOnLaunch = CbStartTranslate.IsChecked == true; s.AutoStartWhenProgramRuns = CbAutoProgram.IsChecked == true; s.AutoStartWithWindows = CbAutostart.IsChecked == true;
        try { if (Autostart.IsEnabled() != s.AutoStartWithWindows) Autostart.Set(s.AutoStartWithWindows); }
        catch (Exception ex) { bad.Add("Autostart s Windows (" + ex.Message + ")"); }
        s.WebEnabled = CbWeb.IsChecked == true; s.WebPort = I(TbWebPort, s.WebPort, bad, "Port OBS serveru");
        foreach (var (tb, name) in new[] { (TbColor, "Barva textu"), (TbEnColor, "Barva angl. textu") })
        {
            try { System.Windows.Media.ColorConverter.ConvertFromString(tb.Text.Trim()); }
            catch { bad.Add(name + " (použijte např. #FFFFFF)"); if (tb == TbColor) s.TextColor = "#FFFFFF"; else s.EnglishColor = "#B8B8B8"; }
        }
        return bad;
    }

    (SourceKind, string, int, string) SourceKey() => (S.Source, S.WindowMatch, S.MonitorIndex, S.ProgramName);

    async void OnSave(object sender, RoutedEventArgs e)
    {
        var before = (SourceKey(), S.Scope);
        var bad = CollectUi();
        _c.SetSettings(S);
        LoadUi();
        ApplyHotkeys();
        if (_c.TranslationEnabled && !before.Equals((SourceKey(), S.Scope)))
        {
            await _c.StopTranslationAsync();
            await _c.StartTranslationAsync();
        }
        SetStatus(bad.Count == 0 ? "Nastavení uloženo a použito." : "Uloženo, ale tyto hodnoty byly neplatné a zůstaly původní: " + string.Join(", ", bad));
    }

    // ================= přehled / akce =================
    async Task ToggleTranslationAsync()
    {
        if (_c.TranslationEnabled)
        {
            _manualStop = true;
            await _c.StopTranslationAsync();
            SetStatus("Překlad vypnut.");
        }
        else
        {
            CollectUi(); _c.SetSettings(S);
            SetStatus("Spouštím překlad…");
            bool ok = await _c.StartTranslationAsync();
            SetStatus(ok ? "Překlad běží." : "Překlad se nepodařilo spustit: " + _c.LastStartError);
        }
        UpdateDashboard();
    }

    async void OnToggleTranslation(object s, RoutedEventArgs e) => await ToggleTranslationAsync();

    void TogglePause()
    {
        _c.OcrPaused = !_c.OcrPaused;
        SetStatus(_c.OcrPaused ? "OCR pozastaveno (F10)." : "OCR obnoveno.");
        UpdateDashboard();
    }
    void OnPauseOcr(object s, RoutedEventArgs e) => TogglePause();

    void ToggleOverlay()
    {
        bool show = _c.Overlay.Visibility != Visibility.Visible;
        _c.SetOverlayVisible(show);
        SetStatus(show ? "Overlay zobrazen." : "Overlay skryt (F9).");
    }
    void OnToggleOverlay(object s, RoutedEventArgs e) => ToggleOverlay();

    void OnOverlaySample(object s, RoutedEventArgs e)
    {
        CollectUi(); _c.Overlay.ApplySettings(S);
        if (_c.Overlay.Visibility != Visibility.Visible) { _c.Overlay.Visibility = Visibility.Visible; S.OverlayVisible = true; }
        _c.Overlay.ShowSample();
    }

    void ShowSettings()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    void UpdateDashboard()
    {
        UpdateQuickSetup();
        var st = _c.ActiveStats; var ts = _c.Service.Stats;
        BtnToggle.Content = Loc.T((_c.TranslationEnabled ? "■  Zastavit překlad (" : "▶  Spustit překlad (") + S.HkToggle + ")");
        BtnToggle.Style = (Style)FindResource(_c.TranslationEnabled ? "DangerButton" : "AccentButton");
        BtnPause.Content = Loc.T((_c.OcrPaused ? "Obnovit OCR (" : "Pauza OCR (") + S.HkPause + ")");
        BtnPause.IsEnabled = _c.TranslationEnabled;
        {
            bool on = _c.TranslationEnabled, paused = _c.OcrPaused;
            TileState.Text = Loc.T(!on ? "Zastaveno" : paused ? "Pozastaveno" : "Překládám");
            StateDot.Fill = Res(!on ? "Muted" : paused ? "Warn" : "Good");
            TileSub.Text = Loc.T(!on
                ? $"Stiskněte Spustit ({S.HkToggle}). Režim: {(S.Scope == TranslationScope.FullScreen ? "vše na obrazovce" : "jen konverzace")}."
                : $"{_c.SourceDescription}  •  {(S.Scope == TranslationScope.FullScreen ? "vše na obrazovce" : "jen konverzace")}  •  {_c.Service.Translator.Name}");
            TileCpu.Text = $"{_c.CpuPercent:0.0} %";
            TileOcr.Text = st.LastOcrMs > 0 ? $"{st.LastOcrMs} ms" : "–";
            TileTr.Text = ts.LastMs > 0 ? $"{ts.LastMs} ms" : "–";
            TileFps.Text = on && st.SourceFps > 0 ? st.SourceFps.ToString("0") : "–";
            LastEn.Text = string.IsNullOrEmpty(st.LastEnglish) ? "–" : st.LastEnglish;
            LastCs.Text = string.IsNullOrEmpty(st.LastCzech) ? "–" : st.LastCzech;
        }
        var w = new StringBuilder();
        if (!_c.OcrAvailable) w.AppendLine("CHYBA: " + _c.OcrInfo);
        if (!_c.TranslationEnabled && _c.LastStartError != null) w.AppendLine("Poslední pokus o start selhal: " + _c.LastStartError);
        if (_c.TranslationEnabled && st.SourceError != null) w.AppendLine("Problém se zdrojem: " + st.SourceError);
        if (_c.TranslationEnabled && ts.LastError != null && ts.LastErrorUtc is { } t && (DateTime.UtcNow - t).TotalSeconds < 60) w.AppendLine("Chyba překladu: " + ts.LastError);
        DashWarn.Text = Loc.T(w.ToString().TrimEnd());

        var sb = new StringBuilder();
        void L(string k, string v) => sb.AppendLine($"{Loc.T(k),-24}{Loc.T(v)}");
        L("Překlad:", _c.TranslationEnabled ? "ZAPNUTO" : "vypnuto");
        L("Režim:", S.Scope == TranslationScope.FullScreen ? "vše na obrazovce" : "jen konverzace (dole uprostřed)");
        L("OCR:", !_c.TranslationEnabled ? "-" : _c.OcrPaused ? "POZASTAVENO" : "běží");
        L("CPU / paměť aplikace:", $"{_c.CpuPercent:0.0} % / {_c.RamMb:0} MB");
        L("Zdroj:", _c.SourceDescription);
        L("FPS zdroje:", st.SourceFps > 0 ? st.SourceFps.ToString("0.0") : "-");
        L("Oblast titulků:", S.Region.ToString());
        L("Interval:", $"{S.IntervalMs} ms (poslední cyklus {st.LastTickMs} ms)");
        L("Cykly / OCR / skip:", $"{st.Ticks} / {st.OcrRuns} / {st.Skipped}");
        L("Čas OCR:", $"{st.LastOcrMs} ms (průměr {st.AvgOcrMs:0} ms, metoda: {(st.LastOcrMethod == "" ? "-" : st.LastOcrMethod)})");
        L("Překladač:", _c.Service.Translator.Name);
        L("Čas překladu:", $"{ts.LastMs} ms");
        L("Potlačené duplicity:", st.Suppressed.ToString());
        L("Požadavky/cache:", $"{ts.Requests} / {ts.CacheHits} zásahů (úspěšnost {(ts.Requests + ts.CacheHits == 0 ? 0 : 100.0 * ts.CacheHits / (ts.Requests + ts.CacheHits)):0} %, cache {_c.Cache.Count} vět)");
        L("Odesláno znaků:", ts.CharsSent.ToString("N0"));
        L("Chyb celkem:", $"{st.Errors + 0} (překlad: {ts.Errors})");
        L("Posl. OCR text:", st.LastOcrText);
        L("Posl. titulek EN:", st.LastEnglish);
        L("Posl. překlad CS:", st.LastCzech);
        L("Overlay:", $"{(_c.Overlay.Visibility == Visibility.Visible ? "zobrazen" : "skryt")}, vyloučen ze záchytu: {(_c.Overlay.ExcludedFromCaptureNow ? "ANO" : "NE")}, click-through: {(S.ClickThrough && !_c.Overlay.EditMode ? "ANO" : "NE")}");
        DashText.Text = sb.ToString();
    }

    // ================= zdroj / oblast =================
    void OnSourceKindChanged(object sender, RoutedEventArgs e)
    {
        if (!_ui) return;
        CbWindow.IsEnabled = RbWindow.IsChecked == true;
        CbProgram.IsEnabled = RbProgram.IsChecked == true;
        CbCapture.IsEnabled = RbElgato.IsChecked == true;
        CbMonitor.IsEnabled = RbMonitor.IsChecked == true;
    }

    sealed record CardChoice(string Name, string Label) { public override string ToString() => Label; }

    async Task RefreshCardsAsync(string keep)
    {
        var devs = await Capture.ElgatoMfSource.ListDevicesAsync();
        var items = new List<CardChoice> { new("", devs.Count == 0 ? "Automaticky (karta nenalezena)" : Loc.T("Automaticky (doporučeno)")) };
        items.AddRange(devs.Select(d => new CardChoice(d, d + (Capture.ElgatoMfSource.LooksLikeCaptureCard(d) ? "" : Loc.T("  (nejspíš kamera)")))));
        CbCapture.ItemsSource = items;
        CbCapture.SelectedItem = items.FirstOrDefault(i => i.Name == keep) ?? items[0];
    }
    async void OnRefreshCards(object sender, RoutedEventArgs e)
    {
        await RefreshCardsAsync((CbCapture.SelectedItem as CardChoice)?.Name ?? "");
        SetStatus(CbCapture.Items.Count > 1 ? $"Nalezeno zařízení: {CbCapture.Items.Count - 1}." : "Žádné záznamové zařízení nenalezeno – je připojené a nepoužívá ho jiný program ve výhradním režimu?");
    }

    sealed record WindowChoice(string Label, string Match);

    void RefreshSourceLists()
    {
        var keepW = CbWindow.Text; var keepP = CbProgram.Text;
        CbWindow.ItemsSource = SourceFactory.ListWindows().Select(w => new WindowChoice($"{w.Title}  [{w.ProcessName}]", w.Title)).ToList();
        CbProgram.ItemsSource = SourceFactory.ListPrograms();
        CbWindow.Text = keepW; CbProgram.Text = keepP;
    }

    void OnRefreshSources(object sender, RoutedEventArgs e)
    {
        RefreshSourceLists();
        var match = (CbWindow.SelectedItem as WindowChoice)?.Match ?? CbWindow.Text.Trim();
        var w = SourceFactory.FindWindow(match);
        WindowFound.Text = w == null
            ? "Seznam obnoven. Vybrané okno se nenašlo (je spuštěné a není minimalizované?)."
            : $"Nalezeno: {w.Title}  [{w.ProcessName}]  {w.Rect}  režim: {w.Mode}";
    }

    void UpdateRegionText() => RegionText.Text = S.Region.ToString();

    async void OnPlacementEditor(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectUi(); _c.SetSettings(S);
            SetStatus("Pořizuji snímek pro editor…");
            var full = await _c.GrabOnceAsync(RegionF.Full);
            string? note = null;
            if (full == null)
            {   // zdroj neběží: pro nastavení umístění stačí ukázkový snímek (poměry obrazu jsou stejné jako ve hře)
                var sample = Path.Combine(AppContext.BaseDirectory, "Assets", "sample-placement.jpg");
                if (!File.Exists(sample)) { SetStatus("Nepodařilo se získat snímek ze zdroje – zkontrolujte zdroj obrazu (hra musí běžet)."); return; }
                full = await BitmapUtil.LoadFileAsync(sample);
                note = "ukázkový snímek (zdroj neběží)";
                SetStatus("Zdroj neběží – používám ukázkový snímek, umístění platí i pro hru.");
            }
            if (_c.DetectGameArea(full)) { /* Settings.GameArea aktualizováno */ }
            if (S.AutoGameArea && (S.GameArea.W < 0.999 || S.GameArea.H < 0.999)) { full = FrameCrop.Crop(full, S.GameArea); note = (note == null ? "" : note + ", ") + "jen obraz hry (bez černých pruhů)"; }
            var dlg = new PlacementEditorWindow(full, S.Region, _c.OverlayAsRegion(), _c.CurrentPreset, note);
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _c.ApplyPlacement(dlg.Result); LoadUi(); UpdateRegionText();
                _c.Overlay.ShowSample();
                SetStatus("Umístění uloženo.");
            }
        }
        catch (Exception ex) { SetStatus("Editor selhal: " + ex.Message); AppLog.Error("Editor umístění: " + ex); }
    }

    async void OnSelectRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync();

    async Task SelectRegionAsync()
    {
        try
        {
            CollectUi();
            SetStatus("Pořizuji snímek pro výběr oblasti…");
            var full = await _c.GrabOnceAsync(RegionF.Full);
            if (full == null) { SetStatus("Nepodařilo se získat snímek ze zdroje – zkontrolujte zdroj obrazu."); return; }
            _c.DetectGameArea(full);
            if (S.AutoGameArea && (S.GameArea.W < 0.999 || S.GameArea.H < 0.999)) full = FrameCrop.Crop(full, S.GameArea);
            var dlg = new RegionSelectorWindow(full, S.Region);
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                S.Region = dlg.Result; _c.PlaceOverlayFromSettings(false); S.Save(); _c.Pipeline.ApplySettings(); UpdateRegionText();
                AppLog.Info("Nová oblast titulků: " + S.Region);
                SetStatus("Oblast titulků nastavena: " + S.Region);
            }
            else SetStatus("Výběr oblasti zrušen.");
        }
        catch (Exception ex) { AppLog.Error("Výběr oblasti: " + ex.Message); SetStatus("Výběr oblasti selhal: " + ex.Message); }
    }

    void OnRegionDefault(object s, RoutedEventArgs e) { S.Region = RegionF.DefaultSubtitleBand; _c.PlaceOverlayFromSettings(false); S.Save(); _c.Pipeline.ApplySettings(); UpdateRegionText(); }
    void OnRegionFull(object s, RoutedEventArgs e) { S.Region = RegionF.Full; _c.PlaceOverlayFromSettings(false); S.Save(); _c.Pipeline.ApplySettings(); UpdateRegionText(); }

    // ================= OCR =================
    async void OnOcrNow(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectUi();
            var crop = await _c.GrabOnceAsync(S.EffectiveRegion);
            if (crop == null) { OcrResult.Text = "Ze zdroje nepřišel žádný snímek."; return; }
            OcrPreview.Source = RegionSelectorWindow.ToBitmapSource(crop);
            var r = await _c.OcrOnceAsync(crop);
            OcrResult.Text = $"[{r.Method}, {r.Ms} ms]  {(r.Text.Length == 0 ? "(nic nerozpoznáno)" : r.Text)}";
        }
        catch (Exception ex) { OcrResult.Text = "Chyba: " + ex.Message; AppLog.Error("Test OCR: " + ex.Message); }
    }

    // ================= překlad =================
    void OnTranslatorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ui || GoogleWarn == null) return;
        GoogleWarn.Visibility = CbTranslator.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateKeyStates()
    {
        GoogleKeyState.Text = string.IsNullOrEmpty(S.GoogleCloudKeyProtected) ? "Klíč není uložen." : "Klíč je uložen (šifrovaně). Nový zadejte jen při změně.";
        GeminiKeyState.Text = string.IsNullOrEmpty(S.GeminiKeyProtected) ? "Klíč není uložen." : "Klíč je uložen (šifrovaně). Nový zadejte jen při změně.";
        AiKeyState.Text = string.IsNullOrEmpty(S.AiKeyProtected) ? "Klíč není uložen (pro lokální servery není potřeba)." : "Klíč je uložen (šifrovaně).";
        DeepLKeyState.Text = string.IsNullOrEmpty(S.DeepLKeyProtected) ? "Klíč není uložen." : "Klíč je uložen (šifrovaně). Nový zadejte jen při změně.";
    }
    void OnClearGoogleKey(object s, RoutedEventArgs e) { S.GoogleCloudKeyProtected = ""; PbGoogle.Clear(); UpdateKeyStates(); S.Save(); _c.SetSettings(S); }
    void OnClearGeminiKey(object s, RoutedEventArgs e) { S.GeminiKeyProtected = ""; PbGemini.Clear(); UpdateKeyStates(); S.Save(); _c.SetSettings(S); }
    void OnClearDeepLKey(object s, RoutedEventArgs e) { S.DeepLKeyProtected = ""; PbDeepL.Clear(); UpdateKeyStates(); S.Save(); _c.SetSettings(S); }
    // ================= historie, export, profily, OBS =================
    /// <summary>Oprava posledního zobrazeného překladu: uloží se a příště se použije místo překladače.</summary>
    void ShowCorrection()
    {
        var last = _c.History.Snapshot().LastOrDefault();
        if (last == null) { SetStatus("Zatím není co opravovat – nejdřív se musí zobrazit nějaký překlad."); return; }
        var w = new CorrectionWindow(last.Original, last.Translation);
        if (w.ShowDialog() == true && w.Translation.Length > 0)
        {
            _c.Corrections.Set(last.Original, _c.Service.LangTag, w.Translation);
            _c.Overlay.AddSubtitle(last.Original, w.Translation, true, false);
            AppLog.Info($"Uložena oprava překladu: „{last.Original}“ → „{w.Translation}“ (celkem oprav: {_c.Corrections.Count})");
            SetStatus($"Oprava uložena (celkem {_c.Corrections.Count}). Příště se použije vaše verze.");
        }
    }

    HistoryWindow? _hist;
    void ShowHistory()
    {
        if (_hist == null || !_hist.IsLoaded) { _hist = new HistoryWindow(_c.History); }
        _hist.Show(); _hist.Activate();
    }
    void OnShowHistory(object s, RoutedEventArgs e) => ShowHistory();

    void OnExportZip(object s, RoutedEventArgs e)
    {
        try
        {
            CollectUi();
            var zip = _c.ExportDiagnosticsZip();
            SetStatus("Diagnostika exportována (bez API klíčů): " + zip);
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zip + "\"");
        }
        catch (Exception ex) { SetStatus("Export selhal: " + ex.Message); }
    }

    void OnCopyWebUrl(object s, RoutedEventArgs e)
    {
        var url = $"http://127.0.0.1:{S.WebPort}/";
        try { Clipboard.SetText(url); SetStatus("Adresa zkopírována: " + url + (S.WebEnabled ? "" : " (server zapnete zaškrtnutím a uložením)")); }
        catch (Exception ex) { SetStatus("Kopírování selhalo: " + ex.Message); }
    }

    readonly ProfileStore _profiles = new();
    void RefreshProfiles() { var t = CbProfile.Text; CbProfile.ItemsSource = _profiles.List(); CbProfile.Text = t; }
    void OnProfileSave(object s, RoutedEventArgs e)
    {
        var n = CbProfile.Text.Trim();
        if (n.Length == 0) { SetStatus("Zadejte název profilu."); return; }
        try { CollectUi(); _profiles.Save(n, S); RefreshProfiles(); SetStatus($"Profil „{n}“ uložen."); }
        catch (Exception ex) { SetStatus("Uložení profilu selhalo: " + ex.Message); }
    }
    void OnProfileLoad(object s, RoutedEventArgs e)
    {
        var n = CbProfile.Text.Trim();
        try
        {
            var p = _profiles.Load(n, S);
            _c.SetSettings(p); LoadUi();
            SetStatus($"Profil „{n}“ načten.");
        }
        catch (Exception ex) { SetStatus("Načtení profilu selhalo: " + ex.Message); }
    }
    void OnProfileDelete(object s, RoutedEventArgs e)
    {
        try { _profiles.Delete(CbProfile.Text.Trim()); RefreshProfiles(); SetStatus("Profil smazán."); }
        catch (Exception ex) { SetStatus("Smazání selhalo: " + ex.Message); }
    }

    void OnClearCache(object s, RoutedEventArgs e) { _c.Cache.Clear(); SetStatus("Cache překladů vymazána."); }

    async void OnTestTranslator(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectUi(); UpdateKeyStates();
            TranslatorTestResult.Text = "Testuji…";
            var t = _c.BuildTranslatorForTest(S);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var r = await t.TestAsync(cts.Token);
            TranslatorTestResult.Text = (r.Ok ? "✔ " : "✘ ") + r.Message;
            AppLog.Info($"Test spojení {t.Name}: {(r.Ok ? "OK" : "SELHÁNÍ")} – {r.Message}");
        }
        catch (Exception ex) { TranslatorTestResult.Text = "✘ " + ex.Message; }
    }

    async void OnManualTranslate(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectUi();
            var t = _c.BuildTranslatorForTest(S);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cs = await t.TranslateAsync(TbManual.Text.Trim(), null, cts.Token);
            ManualResult.Text = $"{cs}\n[{t.Name}, {sw.ElapsedMilliseconds} ms]";
            _c.Overlay.ClearAll(); _c.Overlay.ApplySettings(S); _c.Overlay.AddSubtitle(TbManual.Text.Trim(), cs);
        }
        catch (TranslationException ex) { ManualResult.Text = $"✘ {ex.Message} ({ex.Kind})"; }
        catch (Exception ex) { ManualResult.Text = "✘ " + ex.Message; }
    }

    // ================= overlay =================
    void OnEditOverlay(object sender, RoutedEventArgs e)
    {
        var o = _c.Overlay;
        if (!o.EditMode)
        {
            CollectUi(); S.OverlayAutoAbove = false; CbAutoAbove.IsChecked = false; o.ApplySettings(S);
            o.Visibility = Visibility.Visible; S.OverlayVisible = true;
            o.SetEditMode(true); o.ShowSample();
            BtnEditOverlay.Content = "Ukončit úpravy overlaye a uložit polohu";
            OverlayInfo.Text = "Režim úprav: overlay lze přetahovat a zvětšovat. Je dočasně klikatelný.";
        }
        else
        {
            o.SetEditMode(false); S.Save();
            BtnEditOverlay.Content = "Upravit polohu a velikost overlaye myší";
            OverlayInfo.Text = $"Poloha uložena: {S.OverlayLeft},{S.OverlayTop}  {S.OverlayWidth}×{S.OverlayHeight} px.";
        }
    }

    async void OnPlaceOverlayByMouse(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectUi();
            var full = await _c.GrabOnceAsync(RegionF.Full);
            if (full == null) { SetStatus("Nepodařilo se získat snímek zdroje – nelze zobrazit podklad pro umístění."); return; }
            var t = _c.TargetRect();
            RegionF? cur = null;
            if (S.OverlayWidth > 0 && t.Width > 0 && t.Height > 0)
                cur = new RegionF { X = (S.OverlayLeft - t.Left) / (double)t.Width, Y = (S.OverlayTop - t.Top) / (double)t.Height, W = S.OverlayWidth / (double)t.Width, H = S.OverlayHeight / (double)t.Height }.Clamp();
            var dlg = new RegionSelectorWindow(full, cur, "Nakreslete obdélník, kam se má zobrazovat PŘELOŽENÝ text (overlay).   Enter = potvrdit   Esc = zrušit");
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                var r = dlg.Result;
                S.OverlayLeft = t.Left + (int)Math.Round(r.X * t.Width); S.OverlayTop = t.Top + (int)Math.Round(r.Y * t.Height);
                S.OverlayWidth = Math.Max(240, (int)Math.Round(r.W * t.Width)); S.OverlayHeight = Math.Max(80, (int)Math.Round(r.H * t.Height));
                S.OverlayAutoAbove = false; CbAutoAbove.IsChecked = false;
                _c.PlaceOverlayFromSettings(false); S.Save();
                _c.Overlay.ShowSample();
                OverlayInfo.Text = $"Overlay umístěn: {S.OverlayLeft},{S.OverlayTop}  {S.OverlayWidth}×{S.OverlayHeight} px.";
            }
        }
        catch (Exception ex) { AppLog.Error("Umístění overlaye: " + ex.Message); SetStatus("Umístění selhalo: " + ex.Message); }
    }

    void OnOverlayDefaultPlace(object s, RoutedEventArgs e)
    {
        S.OverlayAutoAbove = true; CbAutoAbove.IsChecked = true;
        _c.PlaceOverlayFromSettings(false); S.Save();
        OverlayInfo.Text = $"Výchozí umístění: {S.OverlayLeft},{S.OverlayTop}  {S.OverlayWidth}×{S.OverlayHeight} px.";
    }

    // ================= logy =================
    void OnClearLog(object s, RoutedEventArgs e) { AppLog.Clear(); LogBox.Clear(); }
    void OnOpenLogDir(object s, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppLog.LogDir); System.Diagnostics.Process.Start("explorer.exe", AppLog.LogDir); }
        catch (Exception ex) { SetStatus("Složku nelze otevřít: " + ex.Message); }
    }
    void OnLogFileToggle(object s, RoutedEventArgs e) => AppLog.FileLogging = CbLogFile.IsChecked == true;

    // ================= test =================
    async void OnOcrAccuracy(object sender, RoutedEventArgs e)
    {
        TestLog.Text = "Probíhá měření…";
        try
        {
            CollectUi();
            var lines = await _c.OcrAccuracyOnTestImagesAsync(Path.Combine(AppContext.BaseDirectory, "testimages"));
            TestLog.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { TestLog.Text = "Chyba: " + ex.Message; AppLog.Error("Test přesnosti: " + ex); }
    }

    // ================= zavření =================
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true; _closing = true;
        try { CollectUi(); } catch { }
        _dash.Stop(); _hot?.Dispose();
        await _c.ShutdownAsync();
        _c.Overlay.Close(); _c.ScreenOverlay.Close();
        Application.Current.Shutdown();
    }
}
