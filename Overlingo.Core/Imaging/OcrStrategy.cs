namespace Overlingo.Core;

public sealed class OcrOptions
{
    public OcrMode Mode { get; set; } = OcrMode.Auto;
    public int WhiteThreshold { get; set; } = 175;
    public int Upscale { get; set; } = 2;
    public int MaxDimension { get; set; } = 2600;
    public int MinLetters { get; set; } = 3;
}

public sealed record OcrOutcome(string Text, string Method, int OcrCalls);

public static class OcrStrategy
{
    /// <summary>Provede OCR podle zvoleného režimu. Auto: izolovaný bílý text, a když je výsledek slabý, porovná s originálem.</summary>
    public static async Task<OcrOutcome> RunAsync(Frame crop, OcrOptions o, Func<Frame, Task<string>> ocr, Func<Frame, Task<string>>? second = null)
    {
        int scale = Math.Max(1, o.Upscale);
        while (scale > 1 && Math.Max(crop.Width, crop.Height) * scale + 24 > o.MaxDimension) scale--;
        var scaled = ImagePrep.ScaleBilinear(crop, scale);

        var isoFrame = ImagePrep.IsolateLight(scaled, o.WhiteThreshold);
        async Task<string> Iso() => TextTools.Normalize(await ocr(isoFrame));
        async Task<string> Orig() => TextTools.Normalize(await ocr(scaled));
        bool Good(string t) => TextTools.IsPlausible(t, o.MinLetters) && TextTools.QualityScore(t) >= 0.8 * TextTools.LetterCount(t);

        switch (o.Mode)
        {
            case OcrMode.WhiteIsolated: return new(await Iso(), "isolated", 1);
            case OcrMode.Original: return new(await Orig(), "original", 1);
        }

        var iso = await Iso();
        if (Good(iso)) return new(iso, "isolated", 1);
        var orig = await Orig();
        double qi = TextTools.QualityScore(iso), qo = TextTools.QualityScore(orig);
        var best = qo > qi ? new OcrOutcome(orig, "original", 2) : new OcrOutcome(iso, "isolated", 2);
        // Druhý engine (Tesseract) jen když je výsledek neprázdný, ale slabý – zbytečně nezdržuje čtení.
        if (second != null && !string.IsNullOrWhiteSpace(best.Text) && !Good(best.Text))
        {
            try
            {
                var t = TextTools.Normalize(await second(isoFrame));
                if (TextTools.QualityScore(t) > TextTools.QualityScore(best.Text)) return new(t, "tesseract", 3);
            }
            catch { /* druhý engine je jen pomocný */ }
        }
        return best;
    }
}
