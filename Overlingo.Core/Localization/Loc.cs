using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlingo.Core;

/// <summary>Popis jazykového souboru rozhraní (Languages/xx.json).</summary>
public sealed record UiLanguage(string Code, string Name, bool MachineTranslated, string File)
{
    /// <summary>Text v nabídce: jen název jazyka (poznámka o strojovém překladu se ukazuje zvlášť pod nabídkou).</summary>
    public string Label => Name;
    public override string ToString() => Name;
}

/// <summary>
/// Jednoduchá lokalizace rozhraní. Zdrojový jazyk textů v kódu je čeština; jazykový soubor
/// mapuje české originály na překlad. Chybějící text se zobrazí česky (nikdy nespadne).
/// Komunita může přidat jazyk jen vložením souboru Languages/xx.json (viz Languages/README.md).
/// </summary>
public static class Loc
{
    static Dictionary<string, string> _map = new();
    /// <summary>Vzory s {0},{1}… místo proměnných částí (stavové věty s hodnotami).</summary>
    static List<(Regex Re, string Target)> _patterns = new();
    public static string Current { get; private set; } = "cs";
    public static bool IsSource => Current == "cs";

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>Vrátí překlad českého textu (nebo původní text, když překlad chybí).</summary>
    public static string T(string cs)
    {
        if (IsSource || string.IsNullOrEmpty(cs)) return cs;
        if (_map.TryGetValue(cs, out var t) && t.Length > 0) return t;
        if (cs.Contains('\n'))
        {
            var whole = TranslateLine(cs, onlyIfMatched: true);
            if (whole != null) return whole;
            return string.Join("\n", cs.Split('\n').Select(l => TranslateLine(l)!));
        }
        return TranslateLine(cs)!;
    }

    static string? TranslateLine(string line, bool onlyIfMatched = false)
    {
        if (line.Length == 0) return onlyIfMatched ? null : line;
        var mk = Regex.Match(line, @"^([✔✖○ℹ•]\s+)(.+)$");
        if (mk.Success) { var rest = TranslateLine(mk.Groups[2].Value); return rest == null ? line : mk.Groups[1].Value + rest; }
        var trimmed = line.TrimEnd('\r');
        var cr = line.Length - trimmed.Length > 0 ? "\r" : "";
        if (_map.TryGetValue(trimmed, out var t) && t.Length > 0) return t + cr;
        foreach (var (re, target) in _patterns)
        {
            var m = re.Match(trimmed);
            if (!m.Success) continue;
            var parts = new string[m.Groups.Count - 1];
            for (int i = 1; i < m.Groups.Count; i++) parts[i - 1] = TranslatePart(m.Groups[i].Value);
            return Regex.Replace(target, @"\{(\d)\}", x => int.Parse(x.Groups[1].Value) < parts.Length ? parts[int.Parse(x.Groups[1].Value)] : x.Value) + cr;
        }
        return onlyIfMatched ? null : line;
    }

    // hodnota vložená do vzoru se přeloží také (např. „režim: vše na obrazovce“), ale jen přesnou shodou
    static string TranslatePart(string v)
    {
        if (_map.TryGetValue(v, out var t) && t.Length > 0) return t;
        if (v.Contains(", ")) return string.Join(", ", v.Split(", ").Select(x => _map.TryGetValue(x, out var y) && y.Length > 0 ? y : x));
        return v;
    }

    static List<(Regex, string)> BuildPatterns(Dictionary<string, string> map)
    {
        var list = new List<(Regex, string)>();
        foreach (var (k, v) in map)
        {
            if (!Regex.IsMatch(k, @"\{\d\}")) continue;
            var sb = new StringBuilder("^"); int last = 0;
            foreach (Match m in Regex.Matches(k, @"\{\d\}"))
            {
                sb.Append(Regex.Escape(k[last..m.Index])).Append("(.*?)"); last = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(k[last..])).Append('$');
            list.Add((new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), v));
        }
        // delší (konkrétnější) vzory první
        return list.OrderByDescending(p => p.Item1.ToString().Length).ToList();
    }

    public static string Format(string cs, params object[] args)
    {
        try { return string.Format(T(cs), args); } catch { return string.Format(cs, args); }
    }

    public static List<UiLanguage> Available(string? dir = null)
    {
        var list = new List<UiLanguage> { new("cs", "Čeština", false, "") };
        dir ??= Dir;
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(f));
                var r = d.RootElement;
                var code = r.GetProperty("code").GetString() ?? "";
                if (code.Length == 0 || code == "cs") continue;
                var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
                var mt = r.TryGetProperty("machineTranslated", out var m) && m.ValueKind == JsonValueKind.True;
                list.Add(new UiLanguage(code, name, mt, f));
            }
            catch (Exception ex) { AppLog.Warn($"Jazykový soubor {Path.GetFileName(f)} nelze načíst: {ex.Message}"); }
        }
        return list;
    }

    /// <summary>Vybere jazyk: uložená volba, jinak angličtina (výchozí jazyk aplikace). Jazyk Windows se záměrně nepoužívá.</summary>
    public static string Resolve(string saved, string systemTwoLetter, IEnumerable<UiLanguage> available)
    {
        var codes = available.Select(a => a.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(saved) && codes.Contains(saved)) return saved;
        return codes.Contains("en") ? "en" : "cs";
    }

    static void LoadStrings(string file, Dictionary<string, string> map)
    {
        using var d = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var p in d.RootElement.GetProperty("strings").EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() ?? "").Length > 0) map[p.Name] = p.Value.GetString()!;
    }

    public static bool Set(string code, string? dir = null)
    {
        if (code == "cs") { _map = new(); _patterns = new(); Current = "cs"; return true; }
        var lang = Available(dir).FirstOrDefault(a => a.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (lang == null) { _map = new(); _patterns = new(); Current = "cs"; return false; }
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(lang.File));
            var map = new Dictionary<string, string>();
            // základ je angličtina: text, který v jazyku chybí, se zobrazí anglicky (ne česky), takže i částečný překlad je použitelný
            var en = Available(dir).FirstOrDefault(a => a.Code == "en");
            if (en != null && lang.Code != "en") LoadStrings(en.File, map);
            LoadStrings(lang.File, map);
            _map = map; _patterns = BuildPatterns(map); Current = lang.Code; return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Jazyk {code} nelze načíst: {ex.Message}");
            _map = new(); _patterns = new(); Current = "cs"; return false;
        }
    }
}
