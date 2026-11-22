using System.Runtime.InteropServices;
using System.Text;

namespace Overlingo.App.Win;

/// <summary>Win32 P/Invoke deklarace použité diagnostikou.</summary>
public static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"{Left},{Top} {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, WS_MAXIMIZE = 0x01000000L;
    public const long WS_EX_TOPMOST = 0x8L, WS_EX_TRANSPARENT = 0x20L, WS_EX_TOOLWINDOW = 0x80L,
                      WS_EX_LAYERED = 0x80000L, WS_EX_NOACTIVATE = 0x08000000L;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    public const uint GA_ROOT = 2;
    public const int SW_RESTORE = 9;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    public static long GetLong(IntPtr h, int idx) => GetWindowLongPtr(h, idx).ToInt64();
    public static void SetLong(IntPtr h, int idx, long v) => SetWindowLongPtr(h, idx, new IntPtr(v));

    public static string DescribeNotificationState()
    {
        try
        {
            if (SHQueryUserNotificationState(out var s) != 0) return "nelze zjistit";
            return s switch
            {
                1 => "QUNS_NOT_PRESENT (uzamčeno / spořič)",
                2 => "QUNS_BUSY (na popředí je aplikace přes celou obrazovku – okno/borderless; exkluzivní D3D to být nemusí)",
                3 => "QUNS_RUNNING_D3D_FULL_SCREEN (EXKLUZIVNÍ Direct3D fullscreen – overlay nemusí jít zobrazit!)",
                4 => "QUNS_PRESENTATION_MODE",
                5 => "QUNS_ACCEPTS_NOTIFICATIONS (běžný stav, žádný fullscreen)",
                6 => "QUNS_QUIET_TIME",
                7 => "QUNS_APP (UWP fullscreen)",
                _ => $"neznámý stav {s}"
            };
        }
        catch (Exception e) { return "chyba: " + e.Message; }
    }
}
