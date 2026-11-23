namespace Overlingo.Core;

/// <summary>Převod výstupu Tesseractu (config „tsv“) na řádky s polohou, aby šly projít stejným výběrem dialogu jako řádky z Windows OCR.</summary>
public static class TesseractTsv
{
    public static List<OcrLine> ParseLines(string tsv, int width, int height)
    {
        var groups = new Dictionary<(int, int, int), List<(int L, int T, int W, int H, string Text)>>();
        var order = new List<(int, int, int)>();
        foreach (var raw in tsv.Split('\n'))
        {
            var p = raw.TrimEnd('\r').Split('\t');
            if (p.Length < 12 || p[0] != "5") continue;                       // jen slova
            if (!int.TryParse(p[2], out var blk) || !int.TryParse(p[3], out var par) || !int.TryParse(p[4], out var ln)) continue;
            if (!int.TryParse(p[6], out var l) || !int.TryParse(p[7], out var t) || !int.TryParse(p[8], out var w) || !int.TryParse(p[9], out var h)) continue;
            var text = string.Join("\t", p.Skip(11)).Trim();
            if (text.Length == 0 || w <= 0 || h <= 0) continue;
            var key = (blk, par, ln);
            if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new(); order.Add(key); }
            list.Add((l, t, w, h, text));
        }
        var res = new List<OcrLine>();
        if (width <= 0 || height <= 0) return res;
        foreach (var k in order)
        {
            var ws = groups[k].OrderBy(x => x.L).ToList();
            int x1 = ws.Min(x => x.L), y1 = ws.Min(x => x.T), x2 = ws.Max(x => x.L + x.W), y2 = ws.Max(x => x.T + x.H);
            res.Add(new OcrLine(string.Join(" ", ws.Select(x => x.Text)), x1 / (double)width, y1 / (double)height, (x2 - x1) / (double)width, (y2 - y1) / (double)height));
        }
        return res;
    }
}
