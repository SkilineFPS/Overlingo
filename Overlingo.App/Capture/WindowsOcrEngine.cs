using OcrLine = Overlingo.Core.OcrLine;
using Overlingo.Core;
using Windows.Media.Ocr;

namespace Overlingo.App.Capture;

internal sealed class WindowsOcrEngine : IOcrEngine, IScreenOcr, IOcrBounds, IOcrFrameInfo
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

    /// <summary>Vybírá z OCR řádků jen dialog (zapíná se z nastavení).</summary>
    public bool FilterSideLines { get; set; } = true;
    readonly DialogSelector _dialog = new();
    public int IgnoredLines => _dialog.Ignored;
    public void ResetDialogLock() => _dialog.Reset();

    /// <summary>Čtená část celého snímku (zlomky). Dialog se vybírá v souřadnicích celého snímku – ty se při zmizení/objevení černých pruhů nemění, takže naučený okraj zůstává.</summary>
    public RegionF FrameRegion { get; set; } = RegionF.Full;

    public async Task<string> RecognizeAsync(Frame frame) => SelectText(await RecognizeLinesAsync(frame));

    /// <summary>Vybere dialog z řádků (z libovolného OCR enginu, souřadnice = zlomky čteného výřezu) a vrátí jeho text; nastaví LastBounds.</summary>
    public string SelectText(IReadOnlyList<OcrLine> lines)
    {
        if (FilterSideLines)
        {
            var g = FrameRegion;
            var mapped = lines.Select(l => (Orig: l, Game: new OcrLine(l.Text, g.X + l.X * g.W, g.Y + l.Y * g.H, l.W * g.W, l.H * g.H))).ToList();
            var chosen = _dialog.Select(mapped.Select(m => m.Game).ToList(), "");
            var set = new HashSet<OcrLine>(chosen);
            lines = mapped.Where(m => set.Contains(m.Game)).Select(m => m.Orig).ToList();
        }
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
        foreach (var l in lines)
        {
            x1 = Math.Min(x1, l.X); y1 = Math.Min(y1, l.Y); x2 = Math.Max(x2, l.X + l.W); y2 = Math.Max(y2, l.Y + l.H);
        }
        LastBounds = x2 > x1 && y2 > y1 ? new RegionF { X = x1, Y = y1, W = x2 - x1, H = y2 - y1 } : null;
        var text = string.Join(" ", lines.OrderBy(l => l.Y).Select(l => l.Text));
        if (LastBounds != null)
        {
            if (_bounds.Count > 8) _bounds.Clear();
            _bounds[TextTools.Normalize(text)] = LastBounds;
        }
        return text;
    }

    public RegionF? LastBounds { get; private set; }
    readonly Dictionary<string, RegionF> _bounds = new();
    /// <summary>Poloha textu podle toho, co engine vrátil (OcrStrategy může vybrat jiné než poslední čtení).</summary>
    public RegionF? BoundsOf(string normalizedText) => _bounds.TryGetValue(normalizedText, out var r) ? r : LastBounds;

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
