using OcrLine = Overlingo.Core.OcrLine;
using Overlingo.Core;
using Windows.Media.Ocr;

namespace Overlingo.App.Capture;

internal sealed class WindowsOcrEngine : IOcrEngine, IScreenOcr, IOcrBounds
{
    readonly OcrEngine _engine;
    public string Language { get; }
    public int MaxDimension => (int)OcrEngine.MaxImageDimension;

    public const string MissingLanguageHelp =
        "Windows OCR nemá anglický jazykový balíček. Nastavení → Čas a jazyk → Jazyk a oblast → Přidat jazyk → English (United States) " +
        "(včetně volitelné funkce „Optical character recognition“), nebo v PowerShellu jako správce: " +
        "Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";

    WindowsOcrEngine(OcrEngine e, string lang) { _engine = e; Language = lang; }

    /// <summary>Vytvoří OCR pro jazyk titulků ve hře (kód z Languages); když balíček chybí, zkusí angličtinu.</summary>
    public static WindowsOcrEngine? TryCreate(string sourceLang = "en")
    {
        var tags = new List<string>();
        if (!string.Equals(sourceLang, "en", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(sourceLang switch { "zh-CN" => "zh-Hans-CN", "zh-TW" => "zh-Hant-TW", _ => sourceLang });
            tags.Add(sourceLang.Split('-')[0]);
        }
        tags.Add("en-US"); tags.Add("en");
        foreach (var t in tags)
        {
            try
            {
                var e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(t));
                if (e != null) return new WindowsOcrEngine(e, t);
            }
            catch { }
        }
        return null;
    }

    public async Task<string> RecognizeAsync(Frame frame)
    {
        using var bmp = BitmapUtil.ToSoftwareBitmap(frame);
        var res = await _engine.RecognizeAsync(bmp);
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
        foreach (var l in res.Lines) foreach (var w in l.Words)
        {
            var r = w.BoundingRect;
            x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y); x2 = Math.Max(x2, r.X + r.Width); y2 = Math.Max(y2, r.Y + r.Height);
        }
        LastBounds = x2 > x1 && y2 > y1 && frame.Width > 0 && frame.Height > 0
            ? new RegionF { X = x1 / frame.Width, Y = y1 / frame.Height, W = (x2 - x1) / frame.Width, H = (y2 - y1) / frame.Height } : null;
        return string.Join(" ", res.Lines.Select(l => l.Text));
    }

    public RegionF? LastBounds { get; private set; }

    /// <summary>Řádky s polohou (sjednocení obdélníků slov) jako zlomky snímku – pro režim celé obrazovky.</summary>
    public async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Frame frame)
    {
        using var bmp = BitmapUtil.ToSoftwareBitmap(frame);
        var res = await _engine.RecognizeAsync(bmp);
        var list = new List<OcrLine>();
        foreach (var l in res.Lines)
        {
            double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
            foreach (var w in l.Words)
            {
                var r = w.BoundingRect;
                x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y); x2 = Math.Max(x2, r.X + r.Width); y2 = Math.Max(y2, r.Y + r.Height);
            }
            if (x2 <= x1 || y2 <= y1) continue;
            list.Add(new OcrLine(l.Text, x1 / frame.Width, y1 / frame.Height, (x2 - x1) / frame.Width, (y2 - y1) / frame.Height));
        }
        return list;
    }
}
