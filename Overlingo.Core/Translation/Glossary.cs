using System.Text;
using System.Text.RegularExpressions;

namespace Overlingo.Core;

/// <summary>
/// Slovník jmen a frází. Řádky: „Vice City“ (nepřekládat) nebo „Vice City = Vice City“ / „jackpot = výhra“ (vynutit překlad).
/// Před překladem se výrazy nahradí neutrálními značkami, po překladu se vrátí – překladač je tak nezmění.
/// Prázdné řádky a řádky začínající „#“ se ignorují.
/// </summary>
public sealed class Glossary
{
    sealed record Entry(string Term, string Repl, Regex Rx);
    readonly List<Entry> _entries = new();
    public bool IsEmpty => _entries.Count == 0;
    public int Count => _entries.Count;
    /// <summary>Krátký otisk obsahu – při změně slovníku se starý překlad v cache nepoužije.</summary>
    public string Version { get; private set; } = "";

    public static Glossary Parse(string? text)
    {
        var g = new Glossary();
        var sb = new StringBuilder();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string term = line, repl = line;
            int eq = line.IndexOf('=');
            if (eq > 0) { term = line[..eq].Trim(); repl = line[(eq + 1)..].Trim(); }
            if (term.Length < 2 || repl.Length == 0) continue;
            g._entries.Add(new Entry(term, repl, new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
            sb.Append(term).Append('=').Append(repl).Append('\n');
        }
        // nejdelší výrazy první (aby „Vice City“ přebilo „City“)
        g._entries.Sort((a, b) => b.Term.Length.CompareTo(a.Term.Length));
        g.Version = g._entries.Count == 0 ? "" : ((uint)sb.ToString().GetHashCode()).ToString("x");
        return g;
    }

    static string Token(int i)
    {
        // jen písmena (překladače je nechávají být): „Qzx“ + písmena indexu + „xq“
        var sb = new StringBuilder("Qzx");
        do { sb.Append((char)('a' + i % 26)); i /= 26; } while (i > 0);
        return sb.Append("xq").ToString();
    }

    public sealed record Protected(string Text, IReadOnlyList<(string Token, string Repl)> Map);

    public Protected Protect(string text)
    {
        var map = new List<(string, string)>();
        string cur = text;
        foreach (var e in _entries)
        {
            if (!e.Rx.IsMatch(cur)) continue;
            var tok = Token(map.Count);
            cur = e.Rx.Replace(cur, tok);
            map.Add((tok, e.Repl));
        }
        return new Protected(cur, map);
    }

    /// <summary>Vrátí text se zpětně doplněnými výrazy; null, když překladač některou značku zničil (volající pak přeloží bez slovníku).</summary>
    public static string? Restore(string translated, Protected p)
    {
        string cur = translated;
        foreach (var (tok, repl) in p.Map)
        {
            var rx = new Regex(Regex.Escape(tok), RegexOptions.IgnoreCase);
            if (!rx.IsMatch(cur)) return null;
            cur = rx.Replace(cur, repl.Replace("$", "$$"));
        }
        return cur;
    }
}
