namespace Overlingo.Core;

public sealed record HistoryItem(DateTime Time, string Original, string Translation);

/// <summary>Posledních N titulků (pro okno historie). Vlákna bezpečné.</summary>
public sealed class SubtitleHistory
{
    readonly List<HistoryItem> _items = new(); readonly object _lk = new();
    readonly int _max;
    public event Action? Changed;
    public SubtitleHistory(int max = 500) { _max = max; }

    public void Add(string original, string translation, bool replaceLast = false)
    {
        lock (_lk)
        {
            if (replaceLast && _items.Count > 0) _items.RemoveAt(_items.Count - 1);
            _items.Add(new HistoryItem(DateTime.Now, original, translation));
            while (_items.Count > _max) _items.RemoveAt(0);
        }
        Changed?.Invoke();
    }
    public IReadOnlyList<HistoryItem> Snapshot() { lock (_lk) return _items.ToList(); }
    public void Clear() { lock (_lk) _items.Clear(); Changed?.Invoke(); }

    public string ToText(bool withOriginal = true)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in Snapshot())
        {
            sb.Append('[').Append(i.Time.ToString("HH:mm:ss")).Append("] ").AppendLine(i.Translation);
            if (withOriginal) sb.Append("           ").AppendLine(i.Original);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Export do SRT (pro přehrávače a editory). Časy jsou relativní k prvnímu titulku; každý titulek trvá do dalšího
    /// (nejméně 1,5 s, nejvýš 7 s). S bilingual = true je pod překladem kurzívou originál.
    /// </summary>
    public string ToSrt(bool bilingual = false)
    {
        var items = Snapshot();
        var sb = new System.Text.StringBuilder();
        if (items.Count == 0) return "";
        var t0 = items[0].Time;
        for (int i = 0; i < items.Count; i++)
        {
            var start = items[i].Time - t0;
            var maxEnd = i + 1 < items.Count ? items[i + 1].Time - t0 - TimeSpan.FromMilliseconds(50) : start + TimeSpan.FromSeconds(7);
            var end = start + TimeSpan.FromSeconds(Math.Clamp(1.5 + items[i].Translation.Length * 0.05, 1.5, 7));
            if (end > maxEnd) end = maxEnd;
            if (end < start + TimeSpan.FromMilliseconds(500)) end = start + TimeSpan.FromMilliseconds(500);
            sb.Append(i + 1).Append("\r\n").Append(Stamp(start)).Append(" --> ").Append(Stamp(end)).Append("\r\n");
            sb.Append(items[i].Translation.Replace("\r", "").Replace("\n", " ")).Append("\r\n");
            if (bilingual && items[i].Original.Length > 0) sb.Append("<i>").Append(items[i].Original.Replace("\r", "").Replace("\n", " ")).Append("</i>\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    static string Stamp(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
}
