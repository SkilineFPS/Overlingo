using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlingo.Core;

public enum SourceKind { Window, Monitor, Elgato, ImageFile, Program }
public enum TranslatorKind { GoogleUnofficial, GoogleCloud, DeepL, Gemini, OpenAiCompatible }
public enum OcrMode { Auto, WhiteIsolated, Original }
public enum TranslationScope { DialogOnly, FullScreen }

/// <summary>Oblast titulků jako zlomky celého snímku (0..1), nezávislá na rozlišení zdroje.</summary>
public sealed class RegionF
{
    public double X { get; set; } = 0.15;
    public double Y { get; set; } = 0.85;
    public double W { get; set; } = 0.70;
    public double H { get; set; } = 0.12;

    public static RegionF DefaultSubtitleBand => new() { X = 0.15, Y = 0.85, W = 0.70, H = 0.12 };
    public static RegionF Full => new() { X = 0, Y = 0, W = 1, H = 1 };

    public RegionF Clamp()
    {
        double x = Math.Clamp(X, 0, 0.99), y = Math.Clamp(Y, 0, 0.99);
        double w = Math.Clamp(W, 0.01, 1 - x), h = Math.Clamp(H, 0.01, 1 - y);
        return new RegionF { X = x, Y = y, W = w, H = h };
    }

    public override string ToString() => $"x {X:P1}, y {Y:P1}, šířka {W:P1}, výška {H:P1}";
}

public sealed class AppSettings
{
    // --- zdroj obrazu
    public SourceKind Source { get; set; } = SourceKind.Window;
    /// <summary>Podřetězec titulku/procesu/třídy okna zdroje (prázdné = zatím nevybráno).</summary>
    public string WindowMatch { get; set; } = "";
    public int MonitorIndex { get; set; } = 0;
    public string TestImagePath { get; set; } = "";
    public RegionF Region { get; set; } = RegionF.DefaultSubtitleBand;

    // --- co se překládá
    public TranslationScope Scope { get; set; } = TranslationScope.DialogOnly;
    /// <summary>Režim celé obrazovky: velikost písma vůči výšce původního řádku.</summary>
    public double ScreenFontScale { get; set; } = 0.8;
    /// <summary>Režim celé obrazovky: nejkratší interval OCR (celý snímek je dražší než pás titulků).</summary>
    public int ScreenMinIntervalMs { get; set; } = 700;

    // --- OCR
    public int IntervalMs { get; set; } = 200;
    public OcrMode OcrMode { get; set; } = OcrMode.Auto;
    public int WhiteThreshold { get; set; } = 175;
    public int Upscale { get; set; } = 2;
    public int StableReads { get; set; } = 2;
    public bool SkipWhenNoLightText { get; set; } = true;
    /// <summary>Pomocný druhý OCR engine Tesseract (jen když je nainstalovaný a čtení Windows OCR je slabé).</summary>
    public bool UseTesseract { get; set; } = true;
    /// <summary>Cesta k tesseract.exe; prázdné = automatické hledání.</summary>
    public string TesseractPath { get; set; } = "";
    public double ChangeThreshold { get; set; } = 0.02;
    public int MinLetters { get; set; } = 3;
    /// <summary>Delší čtení (nebo čtení plné čísel) se nepovažuje za titulek, ale za panel / UI. 0 = bez limitu délky.</summary>
    public int MaxSubtitleChars { get; set; } = 200;
    /// <summary>Oblast titulků se počítá uvnitř obrazu hry (bez černých pruhů). Region je pak relativní k obrazu hry.</summary>
    public bool AutoGameArea { get; set; } = true;
    /// <summary>Jazyk rozhraní (kód z Languages/*.json, "cs"); prázdné = podle Windows.</summary>
    public string UiLang { get; set; } = "en";
    /// <summary>Zobrazit i technické záložky (OCR, Zkratky, Pokročilé, Diagnostika, Logy, Test). Výchozí: jednoduché rozhraní.</summary>
    public bool ShowAdvancedUi { get; set; }
    /// <summary>Verze migrace jazyka rozhraní (1 = výchozí angličtina; starší uložené „cs“ bylo jen automatické, ne volba uživatele).</summary>
    public int UiLangVersion { get; set; }
    /// <summary>Zjištěný obraz hry (za běhu, neukládá se).</summary>
    [JsonIgnore] public RegionF GameArea { get; set; } = RegionF.Full;
    /// <summary>Oblast titulků jako zlomky celého snímku (s ohledem na obraz hry).</summary>
    [JsonIgnore]
    public RegionF EffectiveRegion
    {
        get
        {
            var r = (Region ?? RegionF.DefaultSubtitleBand).Clamp();
            if (!AutoGameArea) return r;
            var g = GameArea ?? RegionF.Full;
            return new RegionF { X = g.X + r.X * g.W, Y = g.Y + r.Y * g.H, W = r.W * g.W, H = r.H * g.H }.Clamp();
        }
    }

    // --- pokročilé
    public bool AdaptiveInterval { get; set; } = true;
    public int CacheMax { get; set; } = 2000;
    public int HttpTimeoutSec { get; set; } = 8;
    public int RetryCount { get; set; } = 1;
    /// <summary>Řádky obsahující kterýkoli z těchto textů (oddělených „;“) se ignorují (UI šum, hodiny…).</summary>
    public string IgnoreContains { get; set; } = "";
    public bool StripJunkTokens { get; set; } = true;
    /// <summary>Podrobný log: každé čtení OCR, rozhodnutí trackeru a prodleva od detekce po zobrazení.</summary>
    public bool VerboseLog { get; set; } = true;

    // --- zkratky (formát např. "F8" nebo "Ctrl+Shift+F8")
    public string HkToggle { get; set; } = "F8";
    public string HkOverlay { get; set; } = "F9";
    public string HkPause { get; set; } = "F10";
    public string HkRegion { get; set; } = "F11";
    public string HkSettings { get; set; } = "F12";
    public string HkScope { get; set; } = "F7";

    // --- překlad
    /// <summary>Jazyk titulků ve hře (OCR) a cílový jazyk překladu – kódy z Languages.</summary>
    /// <summary>Název procesu (bez .exe) pro zdroj „Program“ – po restartu hry se okno najde znovu.</summary>
    /// <summary>Jen pro testy a diagnostiku: povolí zdroj ImageFile.</summary>
    [JsonIgnore] public bool AllowImageSource { get; set; }
    /// <summary>Název záznamového zařízení; prázdné = automatická detekce.</summary>
    public string CaptureDevice { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public bool WebEnabled { get; set; }
    public int WebPort { get; set; } = 8787;
    public string HkHistory { get; set; } = "F6";
    public string HkCorrect { get; set; } = "F5";
    public string GeminiKeyProtected { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-3.8-flash";
    public string AiKeyProtected { get; set; } = "";
    public string AiBaseUrl { get; set; } = "http://localhost:11434/v1";
    public string AiModel { get; set; } = "";
    /// <summary>Krátký popis hry/postav pro AI překladače (např. „GTA – kriminální příběh, slang, vulgarismy“).</summary>
    public string AiGameContext { get; set; } = "";
    public bool FirstRunDone { get; set; }
    public bool AutoStartWithWindows { get; set; }
    public bool StartTranslationOnLaunch { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoStartWhenProgramRuns { get; set; }
    public string SourceLang { get; set; } = "en";
    public string TargetLang { get; set; } = "cs";
    /// <summary>Slovník: řádek „Jméno“ (nepřekládat) nebo „výraz = překlad“.</summary>
    public string Glossary { get; set; } = "";
    public TranslatorKind Translator { get; set; } = TranslatorKind.GoogleUnofficial;
    public string GoogleCloudKeyProtected { get; set; } = "";
    public string DeepLKeyProtected { get; set; } = "";
    public bool UseContext { get; set; } = true;
    public int ContextLines { get; set; } = 2;

    // --- overlay
    public bool OverlayVisible { get; set; } = true;
    /// <summary>Overlay titulků se automaticky umístí těsně NAD vybranou oblast titulků (ruční přesun to vypne).</summary>
    public bool OverlayAutoAbove { get; set; } = false;
    /// <summary>Překlad se zobrazí přímo přes původní titulek na neprůhledném černém pozadí (originál není vidět). Vyžaduje automatické umístění.</summary>
    public bool OverlayCoverOriginal { get; set; } = true;
    public bool ShowEnglish { get; set; } = false;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 30;
    public string TextColor { get; set; } = "#FFFFFF";
    public string EnglishColor { get; set; } = "#B8B8B8";
    public double BackgroundOpacity { get; set; } = 0.55;
    public int MaxLines { get; set; } = 3;
    /// <summary>0 = automaticky podle délky věty.</summary>
    public double DisplaySeconds { get; set; } = 0;
    public bool ClickThrough { get; set; } = true;
    public bool ExcludeOverlayFromCapture { get; set; } = false;
    /// <summary>Pozice a velikost overlaye ve fyzických pixelech; -1 = umístit automaticky.</summary>
    public int OverlayLeft { get; set; } = -1;
    public int OverlayTop { get; set; } = -1;
    public int OverlayWidth { get; set; } = -1;
    public int OverlayHeight { get; set; } = -1;

    public void Normalize()
    {
        if (UiLangVersion < 1) { UiLang = "en"; UiLangVersion = 1; }
        IntervalMs = Math.Clamp(IntervalMs, 100, 3000);
        WhiteThreshold = Math.Clamp(WhiteThreshold, 60, 250);
        Upscale = Math.Clamp(Upscale, 1, 3);
        TesseractPath ??= "";
        StableReads = Math.Clamp(StableReads, 1, 5);
        ChangeThreshold = Math.Clamp(ChangeThreshold, 0.002, 0.2);
        MinLetters = Math.Clamp(MinLetters, 1, 30);
        MaxSubtitleChars = MaxSubtitleChars <= 0 ? 0 : Math.Clamp(MaxSubtitleChars, 60, 1000);
        ContextLines = Math.Clamp(ContextLines, 0, 6);
        FontSize = Math.Clamp(FontSize, 10, 120);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        MaxLines = Math.Clamp(MaxLines, 1, 8);
        ScreenFontScale = Math.Clamp(ScreenFontScale, 0.4, 1.5);
        ScreenMinIntervalMs = Math.Clamp(ScreenMinIntervalMs, 200, 5000);
        CacheMax = Math.Clamp(CacheMax, 100, 50000);
        HttpTimeoutSec = Math.Clamp(HttpTimeoutSec, 2, 60);
        RetryCount = Math.Clamp(RetryCount, 0, 3);
        IgnoreContains ??= "";
        foreach (var (n, def) in new[] { ("HkToggle", "F8"), ("HkOverlay", "F9"), ("HkPause", "F10"), ("HkRegion", "F11"), ("HkSettings", "F12"), ("HkScope", "F7"), ("HkHistory", "F6"), ("HkCorrect", "F5") })
        {
            var pi = typeof(AppSettings).GetProperty(n)!;
            if (!HotkeyParser.TryParse((string?)pi.GetValue(this), out _, out _)) pi.SetValue(this, def);
        }
        if (Source == SourceKind.ImageFile && !AllowImageSource) Source = SourceKind.Window; // testovací obrázek už není volba v UI
        if (string.IsNullOrWhiteSpace(SourceLang)) SourceLang = "en";
        if (string.IsNullOrWhiteSpace(TargetLang)) TargetLang = "cs";
        WebPort = Math.Clamp(WebPort, 1024, 65535);
        DisplaySeconds = Math.Clamp(DisplaySeconds, 0, 60);
        Region = (Region ?? RegionF.DefaultSubtitleBand).Clamp();
        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Segoe UI";
        WindowMatch ??= "";
    }

    // --- ukládání
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overlingo");
    /// <summary>Jednorázově zkopíruje nastavení ze starého jména složky (GtaLiveTranslator).</summary>
    public static void MigrateOld(string? oldDir = null, string? newDir = null)
    {
        try
        {
            oldDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GtaLiveTranslator");
            newDir ??= DefaultDir;
            var o = Path.Combine(oldDir, "settings.json"); var n = Path.Combine(newDir, "settings.json");
            if (!File.Exists(o) || File.Exists(n)) return;
            Directory.CreateDirectory(newDir);
            File.Copy(o, n);
            var oc = Path.Combine(oldDir, "translation-cache.json");
            if (File.Exists(oc)) File.Copy(oc, Path.Combine(newDir, "translation-cache.json"), true);
            AppLog.Info("Nastavení převzato ze staré složky GtaLiveTranslator.");
        }
        catch (Exception e) { AppLog.Warn($"Migrace nastavení selhala: {e.Message}"); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AppSettings FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        s.Normalize(); return s;
    }

    public static string DefaultPath => Path.Combine(DefaultDir, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception e) { AppLog.Warn($"Nastavení se nepodařilo načíst, použiji výchozí: {e.Message}"); }
        var d = new AppSettings(); d.Normalize(); return d;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { AppLog.Error($"Nastavení se nepodařilo uložit: {e.Message}"); }
    }
}
