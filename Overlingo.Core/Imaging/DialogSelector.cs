namespace Overlingo.Core;

/// <summary>
/// Z řádků OCR vybere blok dialogových titulků a ostatní bloky (úkol mise, nápověda ovládání…) ignoruje.
/// Řádky se seskupí do bloků podle svislé mezery (zalomená věta = jeden blok, úkol pod dialogem = další blok).
/// Vybere se blok nejblíž poslední známé poloze dialogu („zámek“); bez zámku horní blok, nebo výrazně vyšší písmo.
/// Bloky, které se odmítly vedle dialogu, si program pamatuje, takže se ignorují, i když je úkol na obrazovce sám.
/// Navíc se naučí „spodní okraj dialogu“: titulky ve hrách bývají přichycené k pevnému spodnímu okraji (jednořádkové i zalomené),
/// kdežto úkoly mise a nápovědy ovládání (PHOTO…) leží jinde. Po naučení se ignoruje každý blok, který nekončí u tohoto okraje.
/// </summary>
public sealed class DialogSelector
{
    readonly Func<DateTime> _now;
    public DialogSelector(Func<DateTime>? now = null) { _now = now ?? (() => DateTime.UtcNow); }

    /// <summary>Mezera mezi řádky větší než tento násobek výšky řádku začíná nový blok.</summary>
    public double GapFactor { get; set; } = 0.7;
    /// <summary>Jak dlouho se pamatuje „vedlejší“ blok bez nového potvrzení (s).</summary>
    public double ZoneMemorySec { get; set; } = 180;

    double _lockY = double.NaN, _lockH;
    readonly List<(double Y, double H, DateTime At, string Text)> _zones = new();

    public int Ignored { get; private set; }

    /// <summary>Naučený spodní okraj dialogu jako zlomek snímku (NaN = ještě neznámý).</summary>
    public double Anchor { get; private set; } = double.NaN;
    readonly List<(int Bucket, string Text)> _recent = new();
    string _frameKey = "";
    public const int LearnDistinct = 3;

    static string Norm(string t) => string.Join(" ", t.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    static bool DialogLike(string t)
    {
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 && t.Count(char.IsLetter) >= 12 && t.Length <= 220;
    }

    static HashSet<string> Tok(string t) =>
        new(System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}]+").Where(w => w.Length > 0));

    static bool Similar(string a, string b)
    {
        var x = Tok(a); var y = Tok(b);
        if (x.Count == 0 || y.Count == 0) return a == b;
        int inter = x.Count(y.Contains);
        return inter / (double)(x.Count + y.Count - inter) >= 0.5;
    }

    /// <summary>
    /// Učí se spodní okraj dialogu z textů, které vypadají jako věta a leží ve spodní části obrazu. Stejný (i chvějivě přečtený) text se počítá jen jednou.
    /// Když se v obraze objeví dvě místa se stejnou jistotou (dialog a pod ním úkol mise), vyhrává horní – úkol mise leží vždy pod titulky.
    /// </summary>
    void Learn(Block b)
    {
        if (b.Bottom < 0.6) return;
        var text = TextOf(b);
        if (!DialogLike(text)) return;
        if (_recent.Any(r => r.Text == text || Similar(r.Text, text))) return;
        int bucket = (int)Math.Round(b.Bottom / 0.01);
        _recent.Add((bucket, text));
        while (_recent.Count > 24) _recent.RemoveAt(0);
        // kandidát = nejvyšší místo, které má dost různých textů (ne nutně nejčetnější)
        var cands = _recent.GroupBy(r => r.Bucket).Select(g => (Bucket: g.Key, Count: g.Count())).Where(g => g.Count >= LearnDistinct).OrderBy(g => g.Bucket).ToList();
        if (cands.Count == 0) return;
        var best = cands[0];
        if (!double.IsNaN(Anchor))
        {
            int curBucket = (int)Math.Round(Anchor / 0.01);
            if (Math.Abs(best.Bucket - curBucket) <= 1) return;
            // naučený okraj je „lepivý“ směrem dolů: nižší místo (úkol) ho nepřepíše, jen výrazně převažující
            int cur = _recent.Count(r => Math.Abs(r.Bucket - curBucket) <= 1);
            int nw = _recent.Count(r => Math.Abs(r.Bucket - best.Bucket) <= 1);
            if (best.Bucket > curBucket && nw < Math.Max(LearnDistinct, cur + 2)) return;
        }
        var near = _recent.Where(r => Math.Abs(r.Bucket - best.Bucket) <= 1).Select(r => r.Bucket).ToList();
        Anchor = near.Average() * 0.01;
    }

    // různé dialogové texty, které nesedí na naučený okraj a nejsou ve známé zóně; při několika po sobě se okraj zahodí (hra jinde titulkuje)
    readonly List<(string Text, DateTime At)> _misses = new();
    public const int MissesToDropAnchor = 4;

    void NoteMiss(Block b, DateTime now)
    {
        var text = TextOf(b);
        if (!DialogLike(text)) return;
        _misses.RemoveAll(m => (now - m.At).TotalSeconds > 90);
        if (_misses.Any(m => m.Text == text || Similar(m.Text, text))) return;
        _misses.Add((text, now));
    }

    public sealed record Block(IReadOnlyList<OcrLine> Lines)
    {
        public double Top => Lines.Min(l => l.Y);
        public double Bottom => Lines.Max(l => l.Y + l.H);
        public double CenterY => (Top + Bottom) / 2;
        public double LineH => Lines.Average(l => l.H);
    }

    public void Reset() { _lockY = double.NaN; _zones.Clear(); Ignored = 0; Anchor = double.NaN; _recent.Clear(); _misses.Clear(); }

    public static List<Block> Group(IReadOnlyList<OcrLine> lines, double gapFactor)
    {
        var sorted = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Y).ToList();
        var blocks = new List<Block>(); var cur = new List<OcrLine>();
        foreach (var l in sorted)
        {
            if (cur.Count > 0)
            {
                var prev = cur[^1];
                double gap = l.Y - (prev.Y + prev.H);
                if (gap > gapFactor * Math.Max(prev.H, l.H)) { blocks.Add(new Block(cur)); cur = new List<OcrLine>(); }
            }
            cur.Add(l);
        }
        if (cur.Count > 0) blocks.Add(new Block(cur));
        return blocks;
    }

    List<Block> Fit(IReadOnlyList<Block> blocks) =>
        blocks.Where(b => Math.Abs(b.Bottom - Anchor) <= Math.Max(0.02, b.LineH * 0.8)).OrderBy(b => Math.Abs(b.Bottom - Anchor)).ToList();

    static string TextOf(Block b) => Norm(string.Join(" ", b.Lines.OrderBy(l => l.Y).Select(l => l.Text)));

    /// <summary>Zóna platí jen pro stejný text na stejném místě: nový dialog na místě, kde dřív visel úkol, se nikdy neignoruje.</summary>
    bool ZoneMatches((double Y, double H, DateTime At, string Text) z, Block b) =>
        Math.Abs(b.CenterY - z.Y) <= Math.Max(b.LineH, z.H) * 0.9 && (z.Text.Length == 0 || Similar(z.Text, TextOf(b)));

    void RefreshZone(Block b, DateTime now)
    {
        for (int i = 0; i < _zones.Count; i++)
            if (ZoneMatches(_zones[i], b)) _zones[i] = (_zones[i].Y, _zones[i].H, now, TextOf(b));
    }

    bool InZone(Block b, DateTime now)
    {
        foreach (var z in _zones)
            if (now - z.At <= TimeSpan.FromSeconds(ZoneMemorySec) && ZoneMatches(z, b)) return true;
        return false;
    }

    /// <summary>Vrátí řádky dialogu (prázdný seznam, když v záběru není nic než vedlejší bloky).
    /// frameKey: rozměry/oblast snímku – při změně se naučená poloha zapomene.</summary>
    public IReadOnlyList<OcrLine> Select(IReadOnlyList<OcrLine> lines, string frameKey = "")
    {
        if (frameKey != _frameKey) { Reset(); _frameKey = frameKey; }
        var now = _now();
        _zones.RemoveAll(z => now - z.At > TimeSpan.FromSeconds(ZoneMemorySec));
        var blocks = Group(lines, GapFactor);
        if (blocks.Count == 0) return Array.Empty<OcrLine>();

        // už známe, kde dialog končí: vezme se blok u tohoto okraje, ostatní (i osamocené) se ignorují
        if (!double.IsNaN(Anchor))
        {
            var fit = Fit(blocks);
            if (fit.Count == 0)
            {
                foreach (var b in blocks) { Ignored++; Learn(b); if (!InZone(b, now)) NoteMiss(b, now); }
                fit = Fit(blocks);   // učení mohlo okraj posunout přímo na dialog
                if (fit.Count == 0)
                {
                    if (_misses.Count < MissesToDropAnchor) return Array.Empty<OcrLine>();
                    // několik různých dialogů po sobě nesedí na naučený okraj → okraj je zastaralý (jiná scéna / hra), zahodí se
                    Anchor = double.NaN; _recent.Clear(); _misses.Clear();
                }
            }
            if (fit.Count > 0 && !double.IsNaN(Anchor))
            {
                _misses.Clear();
                foreach (var b in blocks.Where(b => !fit.Contains(b)))
                {   // odmítnutý blok si zapamatujeme jako zónu, aby osamocený úkol později nebyl brán za dialog
                    _zones.RemoveAll(z => Math.Abs(z.Y - b.CenterY) <= Math.Max(z.H, b.LineH) * 0.9);
                    _zones.Add((b.CenterY, b.LineH, now, TextOf(b))); Ignored++; Learn(b);
                }
                var c = fit[0];
                Learn(c);
                _lockY = c.CenterY; _lockH = c.LineH;
                return c.Lines;
            }
        }

        Block chosen;
        if (blocks.Count == 1)
        {
            if (InZone(blocks[0], now)) { RefreshZone(blocks[0], now); Ignored++; return Array.Empty<OcrLine>(); }   // osamocený úkol mise – nepřekládat; je stále vidět → paměť zóny se prodlouží
            chosen = blocks[0];
        }
        else
        {
            if (!double.IsNaN(_lockY))
                chosen = blocks.OrderBy(b => Math.Abs(b.CenterY - _lockY) / Math.Max(_lockH, b.LineH)).First();
            else
            {
                var tallest = blocks.OrderByDescending(b => b.LineH).First();
                var top = blocks[0];
                chosen = tallest.LineH > top.LineH * 1.25 ? tallest : top;
            }
            foreach (var b in blocks.Where(b => !ReferenceEquals(b, chosen)))
            {
                _zones.RemoveAll(z => Math.Abs(z.Y - b.CenterY) <= Math.Max(z.H, b.LineH) * 0.9);
                _zones.Add((b.CenterY, b.LineH, now, TextOf(b))); Ignored++; Learn(b);
            }
        }
        Learn(chosen);
        // zámek polohy se bere jen z bloku, který vypadá jako dialog – krátký osamocený úkol („Get in the“) by jinak zamkl špatné místo
        if (blocks.Count > 1 || DialogLike(TextOf(chosen))) { _lockY = chosen.CenterY; _lockH = chosen.LineH; }
        return chosen.Lines;
    }
}
