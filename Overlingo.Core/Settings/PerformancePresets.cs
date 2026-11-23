namespace Overlingo.Core;

public enum PerformancePreset { Eco, Balanced, Fast, Custom }

/// <summary>Tři předvolby výkonu místo desítek čísel: nastaví interval čtení, zvětšení, spekulativní překlad a adaptivní interval najednou.</summary>
public static class PerformancePresets
{
    static (int Interval, int Upscale, bool Speculative, bool Adaptive) Values(PerformancePreset p) => p switch
    {
        PerformancePreset.Eco => (500, 2, false, true),
        PerformancePreset.Fast => (120, 2, true, false),
        _ => (200, 2, true, true),
    };

    public static void Apply(AppSettings s, PerformancePreset p)
    {
        if (p == PerformancePreset.Custom) return;
        var v = Values(p);
        s.IntervalMs = v.Interval; s.Upscale = v.Upscale; s.SpeculativeTranslate = v.Speculative; s.AdaptiveInterval = v.Adaptive;
        s.Normalize();
    }

    /// <summary>Vrátí výkon a OCR na doporučené hodnoty (zdroj, jazyky, překladač, klíče, vzhled a zkratky se nemění).</summary>
    public static void ResetTuning(AppSettings s)
    {
        var d = new AppSettings();
        s.WhiteThreshold = d.WhiteThreshold; s.StableReads = d.StableReads; s.ChangeThreshold = d.ChangeThreshold; s.MinLetters = d.MinLetters;
        s.SkipWhenNoLightText = d.SkipWhenNoLightText; s.UseTesseract = d.UseTesseract; s.StripJunkTokens = d.StripJunkTokens;
        s.MaxSubtitleChars = d.MaxSubtitleChars; s.IgnoreSideLines = d.IgnoreSideLines; s.AutoGameArea = d.AutoGameArea; s.OcrMode = d.OcrMode;
        s.CacheMax = d.CacheMax; s.HttpTimeoutSec = d.HttpTimeoutSec; s.RetryCount = d.RetryCount;
        Apply(s, PerformancePreset.Balanced);
    }

    /// <summary>Která předvolba odpovídá nastavení; „Custom“, když hodnoty nesedí na žádnou.</summary>
    public static PerformancePreset Detect(AppSettings s)
    {
        foreach (var p in new[] { PerformancePreset.Eco, PerformancePreset.Balanced, PerformancePreset.Fast })
        {
            var v = Values(p);
            if (s.IntervalMs == v.Interval && s.Upscale == v.Upscale && s.SpeculativeTranslate == v.Speculative && s.AdaptiveInterval == v.Adaptive) return p;
        }
        return PerformancePreset.Custom;
    }
}
