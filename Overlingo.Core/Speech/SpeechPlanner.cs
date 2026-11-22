namespace Overlingo.Core;

public sealed record SpeechItem(string Text, double Rate, DateTime Enqueued);

/// <summary>
/// Fronta vět k přečtení nahlas. Titulky ve hře trvají často 1–2 s a češtinu lze říct jen asi 12–15 znaky za sekundu, takže hlas by
/// zaostával. Proto: fronta je krátká (starší věty se zahazují), věta, která čeká příliš dlouho, se nečte, a při zpoždění se mluví rychleji.
/// </summary>
public sealed class SpeechPlanner
{
    readonly List<(string Text, DateTime At)> _q = new();
    readonly object _lk = new();
    public int MaxQueue { get; set; } = 2;
    public double MaxAgeSec { get; set; } = 6;
    public double BaseRate { get; set; } = 1.0;
    public double MaxRate { get; set; } = 1.5;
    public int Dropped { get; private set; }

    public int Pending { get { lock (_lk) return _q.Count; } }

    /// <summary>Přidá větu. Při replace = true (nahrazuje předchozí verzi téhož titulku) se přepíše poslední ještě nepřečtená věta,
    /// a pokud už žádná nečeká (byla už vyslovena), nová verze se nečte, aby se titulek neopakoval.</summary>
    public void Enqueue(string text, bool replace, DateTime now)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        lock (_lk)
        {
            if (replace)
            {
                if (_q.Count == 0) return;
                _q[^1] = (text, now);
                return;
            }
            _q.Add((text, now));
            while (_q.Count > Math.Max(1, MaxQueue)) { _q.RemoveAt(0); Dropped++; }   // nejstarší věta už je neaktuální
        }
    }

    /// <summary>Další věta k přečtení, nebo false. backlog = kolik dalších vět (i už nasyntetizovaných) čeká za touto.</summary>
    public bool TryDequeue(DateTime now, int extraBacklog, out SpeechItem item)
    {
        lock (_lk)
        {
            while (_q.Count > 0 && (now - _q[0].At).TotalSeconds > MaxAgeSec) { _q.RemoveAt(0); Dropped++; }
            if (_q.Count == 0) { item = null!; return false; }
            var (text, at) = _q[0]; _q.RemoveAt(0);
            double age = (now - at).TotalSeconds;
            int waiting = _q.Count + Math.Max(0, extraBacklog);
            double rate = BaseRate * (1 + 0.2 * waiting + (age > 2 ? 0.1 : 0));
            item = new SpeechItem(text, Math.Clamp(rate, BaseRate, Math.Max(BaseRate, MaxRate)), at);
            return true;
        }
    }

    public void Clear() { lock (_lk) _q.Clear(); }
}
