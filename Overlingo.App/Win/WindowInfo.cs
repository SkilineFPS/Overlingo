using System.Diagnostics;
using System.Text;

namespace Overlingo.App.Win;

public enum WindowMode { Minimized, Windowed, Maximized, BorderlessFullscreen }

public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public Native.RECT Rect { get; init; }
    public Native.RECT MonitorRect { get; init; }
    public long Style { get; init; }
    public long ExStyle { get; init; }
    public bool Minimized { get; init; }
    public bool Cloaked { get; init; }
    public uint Dpi { get; init; }
    public uint DisplayAffinity { get; init; }

    public bool HasCaption => (Style & Native.WS_CAPTION) == Native.WS_CAPTION;
    public bool IsTopmost => (ExStyle & Native.WS_EX_TOPMOST) != 0;
    public WindowMode Mode => Classify(Minimized, Style, Rect, MonitorRect);

    public static bool Covers(Native.RECT w, Native.RECT m) =>
        w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;

    public static WindowMode Classify(bool minimized, long style, Native.RECT rect, Native.RECT monitor)
    {
        if (minimized) return WindowMode.Minimized;
        bool caption = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
        if (Covers(rect, monitor) && !caption) return WindowMode.BorderlessFullscreen;
        if ((style & Native.WS_MAXIMIZE) != 0) return WindowMode.Maximized;
        return WindowMode.Windowed;
    }

    public IEnumerable<string> DescribeLines()
    {
        yield return $"Titulek: {Title}";
        yield return $"Proces: {ProcessName} (PID {Pid})";
        yield return $"Třída okna: {ClassName}";
        yield return $"HWND: 0x{Handle.ToInt64():X}";
        yield return $"Pozice/velikost okna: {Rect}";
        yield return $"Monitor: {MonitorRect}  DPI okna: {Dpi}";
        yield return $"Režim (odhad z geometrie): {Mode}" +
            (Mode == WindowMode.BorderlessFullscreen ? " – okno pokrývá celý monitor bez rámu (borderless NEBO exkluzivní fullscreen; rozliší až stav oznámení Windows)" : "");
        yield return $"Styl 0x{Style:X8} (titulek: {HasCaption}), ExStyle 0x{ExStyle:X8} (TOPMOST: {IsTopmost})";
        yield return $"Cloaked (DWM): {Cloaked}";
        yield return $"Display affinity okna: 0x{DisplayAffinity:X} {(DisplayAffinity == Native.WDA_EXCLUDEFROMCAPTURE ? "(okno se vylučuje ze záchytu → WGC vrátí černou!)" : "")}";
    }

    public override string ToString() => $"{(string.IsNullOrWhiteSpace(Title) ? "(bez titulku)" : Title)}  [{ProcessName}]  <{ClassName}>  0x{Handle.ToInt64():X}";
}

public static class WindowEnumerator
{
    public static WindowInfo? Describe(IntPtr h)
    {
        try
        {
            var sb = new StringBuilder(512);
            Native.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            sb.Clear();
            Native.GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            Native.GetWindowThreadProcessId(h, out var pid);
            Native.GetWindowRect(h, out var rect);
            var hmon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(hmon, ref mi);
            int cloaked = 0;
            Native.DwmGetWindowAttribute(h, 14, out cloaked, sizeof(int));
            Native.GetWindowDisplayAffinity(h, out var aff);
            uint dpi = 0;
            try { dpi = Native.GetDpiForWindow(h); } catch { }
            return new WindowInfo
            {
                Handle = h, Title = title, ClassName = cls, Pid = pid, ProcessName = ProcessNameOf(pid),
                Rect = rect, MonitorRect = mi.rcMonitor,
                Style = Native.GetLong(h, Native.GWL_STYLE), ExStyle = Native.GetLong(h, Native.GWL_EXSTYLE),
                Minimized = Native.IsIconic(h), Cloaked = cloaked != 0, Dpi = dpi, DisplayAffinity = aff
            };
        }
        catch { return null; }
    }

    public static List<WindowInfo> Enumerate(bool onlyTitled = true)
    {
        var list = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var w = Describe(h);
            if (w == null) return true;
            if (onlyTitled && string.IsNullOrWhiteSpace(w.Title)) return true;
            if (w.Cloaked) return true;
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IEnumerable<WindowInfo> FindCandidates(IEnumerable<WindowInfo> all, string needle) =>
        all.Where(w => w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || w.ProcessName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || w.ClassName.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>True, pokud je okno <paramref name="candidate"/> v Z-pořadí NAD oknem <paramref name="reference"/>.</summary>
    public static bool IsAbove(IntPtr candidate, IntPtr reference)
    {
        var h = Native.GetWindow(reference, Native.GW_HWNDPREV);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 20000)
        {
            if (h == candidate) return true;
            h = Native.GetWindow(h, Native.GW_HWNDPREV);
        }
        return false;
    }

    static string ProcessNameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "?"; }
    }
}
