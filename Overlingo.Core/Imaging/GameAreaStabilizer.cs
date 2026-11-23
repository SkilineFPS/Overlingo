namespace Overlingo.Core;

/// <summary>Uklidní kolísání detekce černých pruhů: malé odchylky (tmavé scény) ignoruje a skutečnou změnu přijme až po několika shodných měřeních po sobě.</summary>
public sealed class GameAreaStabilizer
{
    readonly double _tol;      // odchylka, kterou považujeme za šum
    readonly int _confirm;     // kolik shodných měření po sobě je třeba pro změnu
    RegionF _cur = RegionF.Full;
    RegionF? _cand; int _candN;

    public GameAreaStabilizer(double tolerance = 0.03, int confirmations = 2) { _tol = tolerance; _confirm = Math.Max(1, confirmations); }

    public RegionF Current => _cur;
    public void Reset(RegionF? start = null) { _cur = start ?? RegionF.Full; _cand = null; _candN = 0; }

    static bool Near(RegionF a, RegionF b, double t) =>
        Math.Abs(a.X - b.X) <= t && Math.Abs(a.Y - b.Y) <= t && Math.Abs(a.W - b.W) <= t && Math.Abs(a.H - b.H) <= t;

    /// <summary>Zpracuje nové měření; vrací true, když se přijatá oblast změnila.</summary>
    public bool Update(RegionF measured)
    {
        if (Near(measured, _cur, _tol)) { _cand = null; _candN = 0; return false; }
        if (_cand is RegionF c && Near(measured, c, _tol)) _candN++;
        else { _cand = measured; _candN = 1; }
        if (_candN < _confirm) return false;
        _cur = _cand!; _cand = null; _candN = 0;
        return true;
    }
}
