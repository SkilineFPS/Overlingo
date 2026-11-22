using System.Text;
using System.Text.RegularExpressions;

namespace Overlingo.Core;

public static class TextTools
{
    static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex WellFormedWord = new(@"^(?:[A-Za-z]+(?:['\-][A-Za-z]+)*|\d+(?:[.,]\d+)?)$", RegexOptions.Compiled);
    static readonly Regex CamelAnomaly = new(@"[a-z][A-Z]", RegexOptions.Compiled);
    static readonly char[] Punct = ".,!?;:\"'()[]{}…—–-_*".ToCharArray();

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');
        return Ws.Replace(s, " ").Trim();
    }

    static readonly Regex JunkColon = new(@"^[A-Za-z]{1,3}[:;][A-Za-z]{1,2}$", RegexOptions.Compiled);

    /// <summary>Odstraní typické OCR smetí (samostatné symboly, „oo:c“); čísla, ceny a normální slova zůstávají.</summary>
    public static string StripJunk(string? s)
    {
        var keep = new List<string>();
        foreach (var tok in Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool hasAlnum = tok.Any(char.IsLetterOrDigit);
            if (!hasAlnum) continue;
            if (JunkColon.IsMatch(tok.Trim(Punct))) continue;
            keep.Add(tok);
        }
        return string.Join(" ", keep);
    }

    /// <summary>
    /// Vypadá text jako panel s čísly / statistikami (FPS, ms, hodnoty) nebo je příliš dlouhý na titulek?
    /// Titulky mívají nanejvýš dva řádky a málo číslic; panel „Input 0.0 ms … 12ø.ø fps“ má číslic hodně.
    /// </summary>
    public static bool LooksLikeUiNoise(string? text, int maxChars = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (maxChars > 0 && text.Length > maxChars) return true;
        int chars = 0, digits = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            chars++;
            if (char.IsDigit(c) || c == 'ø' || c == 'Ø') digits++;
        }
        return chars >= 25 && digits * 100 >= chars * 15;
    }

    /// <summary>Obsahuje text některý z ignorovaných řetězců (oddělených „;“)?</summary>
    public static bool ContainsIgnored(string? text, string? ignoreList)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(ignoreList)) return false;
        foreach (var part in ignoreList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length > 0 && text.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Do jaké míry je kratší text obsažen (s drobnými OCR chybami) v delším: 1 = přesně, 0 = vůbec.</summary>
    public static double ContainmentScore(string? a, string? b)
    {
        var x = Key(a); var y = Key(b);
        var s = x.Length <= y.Length ? x : y; var l = x.Length <= y.Length ? y : x;
        if (s.Length == 0) return 0;
        if (s.Length == l.Length) return Similarity(s, l);
        double best = 0;
        for (int i = 0; i + s.Length <= l.Length; i++)
        {
            // okno stejné délky jako kratší text
            var w = l.Substring(i, s.Length);
            double sc = 1.0 - (double)Levenshtein(s, w) / s.Length;
            if (sc > best) { best = sc; if (best >= 0.999) break; }
        }
        return best;
    }

    public static string Key(string? s) => Normalize(s).ToLowerInvariant();

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Podobnost 0..1 (1 = shodné) bez ohledu na velikost písmen a mezery.</summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Key(a); var y = Key(b);
        int m = Math.Max(x.Length, y.Length);
        if (m == 0) return 1;
        return 1.0 - (double)Levenshtein(x, y) / m;
    }

    /// <summary>Character error rate (0 = přesná shoda).</summary>
    public static double Cer(string expected, string actual)
    {
        var e = Key(expected); var a = Key(actual);
        if (e.Length == 0) return a.Length == 0 ? 0 : 1;
        return (double)Levenshtein(e, a) / e.Length;
    }

    public static int LetterCount(string? s) => s == null ? 0 : s.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    /// <summary>Vypadá text jako titulek (dost písmen, málo smetí)?</summary>
    public static bool IsPlausible(string? s, int minLetters)
    {
        var t = Normalize(s);
        if (t.Length == 0) return false;
        int letters = LetterCount(t);
        int nonSpace = t.Count(c => !char.IsWhiteSpace(c));
        return letters >= minLetters && nonSpace > 0 && (double)letters / nonSpace >= 0.6;
    }

    /// <summary>Hrubé skóre „čitelnosti“ OCR výstupu: správně utvořená slova přičítají, smetí (např. „Theytr&amp;StiIIl“) odečítá.</summary>
    public static double QualityScore(string? s)
    {
        double score = 0;
        foreach (var tok in Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var core = tok.Trim(Punct);
            if (core.Length == 0) continue;
            bool ok = WellFormedWord.IsMatch(core) && !CamelAnomaly.IsMatch(core) && core.Length <= 15;
            score += ok ? core.Length : -core.Length;
        }
        return score;
    }

    /// <summary>Nejlepší souvislý blok OCR řádků vůči očekávanému textu.</summary>
    public static double BestWindowCer(string expected, IReadOnlyList<string> lines, out string best, int maxSpan = 4)
    {
        best = ""; double bestCer = 1.0;
        for (int i = 0; i < lines.Count; i++)
            for (int j = i; j < Math.Min(lines.Count, i + maxSpan); j++)
            {
                var cand = string.Join(" ", lines.Skip(i).Take(j - i + 1));
                double c = Cer(expected, cand);
                if (c < bestCer || best.Length == 0) { bestCer = c; best = cand; }
            }
        return Math.Min(bestCer, 1.0);
    }
}
