namespace Overlingo.Core;

/// <summary>Jazyk překladu. Code = kód pro Google (BCP-47 styl), DeepL = cílový kód DeepL (null = DeepL tento jazyk nepodporuje).</summary>
public sealed record Lang(string Code, string Name, string? DeepL)
{
    public override string ToString() => Name;
    /// <summary>Základní kód zdrojového jazyka pro DeepL (např. „PT“, „ZH“).</summary>
    public string DeepLSource => (DeepL ?? Code).Split('-')[0].ToUpperInvariant();
}

public static class Languages
{
    public static readonly IReadOnlyList<Lang> All = new List<Lang>
    {
        new("cs", "Čeština (Czech)", "CS"), new("en", "English", "EN-GB"), new("es", "Español (Spanish)", "ES"),
        new("fr", "Français (French)", "FR"), new("de", "Deutsch (German)", "DE"), new("it", "Italiano (Italian)", "IT"),
        new("pt", "Português (Portuguese)", "PT-BR"), new("ru", "Русский (Russian)", "RU"), new("pl", "Polski (Polish)", "PL"),
        new("uk", "Українська (Ukrainian)", "UK"), new("tr", "Türkçe (Turkish)", "TR"), new("nl", "Nederlands (Dutch)", "NL"),
        new("sv", "Svenska (Swedish)", "SV"), new("da", "Dansk (Danish)", "DA"), new("fi", "Suomi (Finnish)", "FI"),
        new("no", "Norsk (Norwegian)", "NB"), new("hu", "Magyar (Hungarian)", "HU"), new("ro", "Română (Romanian)", "RO"),
        new("bg", "Български (Bulgarian)", "BG"), new("el", "Ελληνικά (Greek)", "EL"), new("sk", "Slovenčina (Slovak)", "SK"),
        new("sl", "Slovenščina (Slovenian)", "SL"), new("lt", "Lietuvių (Lithuanian)", "LT"), new("lv", "Latviešu (Latvian)", "LV"),
        new("et", "Eesti (Estonian)", "ET"), new("id", "Bahasa Indonesia", "ID"), new("ja", "日本語 (Japanese)", "JA"),
        new("ko", "한국어 (Korean)", "KO"), new("zh-CN", "中文简体 (Chinese Simplified)", "ZH-HANS"), new("zh-TW", "中文繁體 (Chinese Traditional)", "ZH-HANT"),
        new("ar", "العربية (Arabic)", "AR"), new("hi", "हिन्दी (Hindi)", null), new("he", "עברית (Hebrew)", null),
        new("th", "ไทย (Thai)", null), new("vi", "Tiếng Việt (Vietnamese)", null),
    };

    public static Lang Find(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Značka páru jazyků pro klíč cache. Pro výchozí EN→CS je prázdná, aby zůstala platná stará cache.</summary>
    public static string PairTag(string src, string tgt) =>
        string.Equals(src, "en", StringComparison.OrdinalIgnoreCase) && string.Equals(tgt, "cs", StringComparison.OrdinalIgnoreCase) ? "" : $"{src}>{tgt}";
}
