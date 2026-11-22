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
}
