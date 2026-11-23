using System.Text.Json;

namespace Overlingo.Core;

/// <summary>
/// Nastavení zapamatované zvlášť pro každou hru (podle názvu programu): oblast titulků, vzhled a poloha overlaye, režim a výkon.
/// Překladač, klíče, jazyky a zkratky jsou společné pro všechny hry a profil je nemění.
/// </summary>
public sealed class GameProfiles
{
    readonly string _dir;
    public GameProfiles(string? dir = null) { _dir = dir ?? Path.Combine(AppSettings.DefaultDir, "games"); }

    public sealed class Data
    {
        public RegionF Region { get; set; } = RegionF.DefaultSubtitleBand;
        public bool AutoGameArea { get; set; } = true;
        public TranslationScope Scope { get; set; }
        public int IntervalMs { get; set; } = 200;
        public int Upscale { get; set; } = 2;
        public bool SpeculativeTranslate { get; set; } = true;
        public bool AdaptiveInterval { get; set; } = true;
        public bool OverlayAutoAbove { get; set; }
        public bool OverlayCoverOriginal { get; set; } = true;
        public bool ShowEnglish { get; set; }
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 28;
        public string TextColor { get; set; } = "#FFFFFF";
        public string EnglishColor { get; set; } = "#B8B8B8";
        public double BackgroundOpacity { get; set; } = 0.55;
        public int MaxLines { get; set; } = 3;
        public int OverlayLeft { get; set; } = -1;
        public int OverlayTop { get; set; } = -1;
        public int OverlayWidth { get; set; } = -1;
        public int OverlayHeight { get; set; } = -1;
    }

    static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().ToLowerInvariant();
    }
    string PathOf(string game) => Path.Combine(_dir, Safe(game) + ".json");

    public bool Exists(string game) => !string.IsNullOrWhiteSpace(Safe(game)) && File.Exists(PathOf(game));

    public static Data Capture(AppSettings s) => new()
    {
        Region = s.Region, AutoGameArea = s.AutoGameArea, Scope = s.Scope, IntervalMs = s.IntervalMs, Upscale = s.Upscale,
        SpeculativeTranslate = s.SpeculativeTranslate, AdaptiveInterval = s.AdaptiveInterval, OverlayAutoAbove = s.OverlayAutoAbove,
        OverlayCoverOriginal = s.OverlayCoverOriginal, ShowEnglish = s.ShowEnglish, FontFamily = s.FontFamily, FontSize = s.FontSize,
        TextColor = s.TextColor, EnglishColor = s.EnglishColor, BackgroundOpacity = s.BackgroundOpacity, MaxLines = s.MaxLines,
        OverlayLeft = s.OverlayLeft, OverlayTop = s.OverlayTop, OverlayWidth = s.OverlayWidth, OverlayHeight = s.OverlayHeight,
    };

    public static void ApplyTo(Data d, AppSettings s)
    {
        s.Region = d.Region; s.AutoGameArea = d.AutoGameArea; s.Scope = d.Scope; s.IntervalMs = d.IntervalMs; s.Upscale = d.Upscale;
        s.SpeculativeTranslate = d.SpeculativeTranslate; s.AdaptiveInterval = d.AdaptiveInterval; s.OverlayAutoAbove = d.OverlayAutoAbove;
        s.OverlayCoverOriginal = d.OverlayCoverOriginal; s.ShowEnglish = d.ShowEnglish; s.FontFamily = d.FontFamily; s.FontSize = d.FontSize;
        s.TextColor = d.TextColor; s.EnglishColor = d.EnglishColor; s.BackgroundOpacity = d.BackgroundOpacity; s.MaxLines = d.MaxLines;
        s.OverlayLeft = d.OverlayLeft; s.OverlayTop = d.OverlayTop; s.OverlayWidth = d.OverlayWidth; s.OverlayHeight = d.OverlayHeight;
        s.Normalize();
    }

    public void Save(string game, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(Safe(game))) return;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf(game), JsonSerializer.Serialize(Capture(s), new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Použije uložený profil hry na nastavení. Vrací false, když profil neexistuje nebo je poškozený.</summary>
    public bool TryApply(string game, AppSettings s)
    {
        try
        {
            if (!Exists(game)) return false;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(PathOf(game)));
            if (d == null) return false;
            ApplyTo(d, s); return true;
        }
        catch { return false; }
    }
}
