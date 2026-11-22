namespace Overlingo.Core;

// Updated = delší (úplnější) čtení téhož titulku, má nahradit poslední zobrazený
public enum TrackerEvent { None, NewSubtitle, Updated, Cleared }
public readonly record struct TrackerResult(TrackerEvent Event, string? Text);

/// <summary>
/// Z proudu OCR čtení (jedno za tik) vyrobí události „nový titulek“ / „úplnější verze“ / „titulek zmizel“.
/// Titulek vyšle až po několika shodných čteních a potlačí duplicity: drobné OCR odchylky, oříznuté části
/// už zobrazené věty i rychlé opakování téže věty po krátkém zmizení.
/// </summary>
public sealed class SubtitleTracker
{
    sealed class Recent { public string Text = ""; public DateTime At, LastSeen; public bool Cleared; }

    readonly int _stableReads;
    readonly double _same, _dup;
    readonly int _clearAfter, _minLetters;
    readonly Func<DateTime> _now;
    readonly List<Recent> _recent = new();
    string? _cand; int _candCount;
    string? _lastEmitted;
    int _emptyCount;
    /// <summary>Po jak dlouhé době (s) po zmizení se smí stejná věta zobrazit znovu.</summary>
    public double RepeatGuardSeconds { get; set; } = 4;
    public int Suppressed { get; private set; }
    /// <summary>Kdy bylo aktuální čtení poprvé spatřeno (pro měření prodlevy).</summary>
    public DateTime CandidateSince { get; private set; }

    public SubtitleTracker(int stableReads = 2, int minLetters = 3, double sameThreshold = 0.86, double duplicateThreshold = 0.80, int clearAfter = 3, Func<DateTime>? clock = null)
    {
        _stableReads = Math.Max(1, stableReads); _minLetters = minLetters;
        _same = sameThreshold; _dup = duplicateThreshold; _clearAfter = Math.Max(1, clearAfter);
        _now = clock ?? (() => DateTime.UtcNow);
    }

    public string? LastEmitted => _lastEmitted;
    /// <summary>Čeká se na další shodné čtení (stabilita) – pipeline nesmí přeskočit OCR.</summary>
    /// <summary>Titulek je zobrazený, ale poslední čtení byla prázdná – pipeline má kontrolovat často, aby zmizení postřehla rychle.</summary>
    public bool Clearing => _lastEmitted != null && _emptyCount > 0;
    public bool Pending => _cand != null && _candCount < _stableReads;

    public TrackerResult Observe(string? ocrText)
    {
        var text = TextTools.Normalize(ocrText);
        if (!TextTools.IsPlausible(text, _minLetters))
        {
            _cand = null; _candCount = 0; _emptyCount++;
            if (_emptyCount >= _clearAfter && _lastEmitted != null)
            {
                _lastEmitted = null;
                foreach (var r in _recent) { r.Cleared = true; r.LastSeen = _now(); }
                return new TrackerResult(TrackerEvent.Cleared, null);
            }
            return default;
        }

        _emptyCount = 0;
        if (_cand != null && TextTools.Similarity(_cand, text) >= _same) { _candCount++; _cand = text; }
        else { _cand = text; _candCount = 1; CandidateSince = _now(); }

        if (_candCount < _stableReads) return default;
        var cand = _cand!;
        var now = _now();
        _recent.RemoveAll(r => (now - r.LastSeen).TotalSeconds > 30);

        // 1) aktuálně zobrazený titulek: úplnější verze nahradí, shodný / oříznutý se zahodí
        var live = _recent.LastOrDefault(r => !r.Cleared);
        if (live != null)
        {
            double sim = TextTools.Similarity(live.Text, cand);
            if (sim >= _dup) { live.LastSeen = now; return Dup(); }   // trvale viditelný text (HUD) se nevrací jako „nový“
            if (live.Text.Length >= 8 && cand.Length >= live.Text.Length * 1.15 && TextTools.ContainmentScore(live.Text, cand) >= 0.85)
            {
                live.Text = cand; live.At = live.LastSeen = now; _lastEmitted = cand;
                return new TrackerResult(TrackerEvent.Updated, cand);
            }
            if (cand.Length >= 8 && live.Text.Length >= cand.Length * 1.15 && TextTools.ContainmentScore(cand, live.Text) >= 0.85) return Dup();
        }
        // 2) nedávno zmizelý titulek: stejná věta se hned nevrací (blikání), po ochranné době ano
        foreach (var r in _recent.Where(r => r.Cleared && (now - r.At).TotalSeconds < RepeatGuardSeconds))
            if (TextTools.Similarity(r.Text, cand) >= _dup ||
                (cand.Length >= 8 && r.Text.Length >= cand.Length * 1.15 && TextTools.ContainmentScore(cand, r.Text) >= 0.85))
                return Dup();

        _recent.Add(new Recent { Text = cand, At = now, LastSeen = now });
        _lastEmitted = cand;
        return new TrackerResult(TrackerEvent.NewSubtitle, cand);
    }

    TrackerResult Dup() { Suppressed++; return default; }

    public void Reset() { _cand = null; _candCount = 0; _lastEmitted = null; _emptyCount = 0; _recent.Clear(); }
}
