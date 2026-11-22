namespace Overlingo.App.Win;

public sealed record MonitorInfo(int Index, IntPtr Handle, Native.RECT Rect, bool Primary)
{
    public override string ToString() => $"Monitor {Index + 1}: {Rect.Width}x{Rect.Height} @ {Rect.Left},{Rect.Top}{(Primary ? Overlingo.Core.Loc.T(" (hlavní)") : "")}";
}

public static class MonitorEnumerator
{
    public static List<MonitorInfo> List()
    {
        var res = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(h, ref mi);
            res.Add(new MonitorInfo(res.Count, h, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return res.OrderByDescending(m => m.Primary).Select((m, i) => m with { Index = i }).ToList();
    }
}
