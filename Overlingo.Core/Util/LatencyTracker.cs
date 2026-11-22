namespace Overlingo.Core;

public readonly record struct LatencySnapshot(int Count, double Last, double Avg, double P95);

/// <summary>Průběžné měření prodlev (ms): posledních N hodnot pro každou veličinu (zakrytí, OCR, překlad, celkem). Vlákna bezpečné.</summary>
public sealed class LatencyTracker
{
    readonly Dictionary<string, Queue<double>> _m = new();
    readonly object _lk = new();
    readonly int _max;
    public LatencyTracker(int max = 100) { _max = Math.Max(5, max); }

    public void Record(string metric, double ms)
    {
        if (ms < 0 || double.IsNaN(ms) || ms > 600_000) return;
        lock (_lk)
        {
            if (!_m.TryGetValue(metric, out var q)) _m[metric] = q = new Queue<double>();
            q.Enqueue(ms);
            while (q.Count > _max) q.Dequeue();
        }
    }

    public LatencySnapshot Get(string metric)
    {
        double[] a;
        lock (_lk) { if (!_m.TryGetValue(metric, out var q) || q.Count == 0) return default; a = q.ToArray(); }
        var sorted = a.OrderBy(x => x).ToArray();
        int i95 = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
        return new LatencySnapshot(a.Length, a[^1], a.Average(), sorted[i95]);
    }

    public void Clear() { lock (_lk) _m.Clear(); }

    /// <summary>Jeden řádek pro přehled: „průměr 12 ms, 95 % pod 30 ms, poslední 9 ms (n=40)“, nebo „–“.</summary>
    public string Describe(string metric)
    {
        var s = Get(metric);
        return s.Count == 0 ? "-" : $"{s.Avg:0} ms (95 %: {s.P95:0} ms, poslední {s.Last:0} ms, n={s.Count})";
    }
}
