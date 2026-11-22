using System.Windows;
using System.Windows.Interop;
using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Globální klávesové zkratky přes RegisterHotKey (fungují i když je v popředí hra). Ve Fázi 1 pevné F8–F12.</summary>
public sealed class HotkeyManager : IDisposable
{
    readonly IntPtr _hwnd;
    readonly HwndSource _src;
    readonly Dictionary<int, Action> _actions = new();
    readonly List<string> _failed = new();
    readonly List<string> _fallback = new();
    public IReadOnlyList<string> Failed => _failed;
    public IReadOnlyList<string> Fallback => _fallback;

    public HotkeyManager(Window owner)
    {
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _src = HwndSource.FromHwnd(_hwnd);
        _src.AddHook(Hook);
    }

    /// <summary>Registruje zkratku zapsanou textem („F8“, „Ctrl+Shift+F8“). Obsazenou holou klávesu zkusí ještě s Ctrl.</summary>
    public void Register(int id, string combo, string name, Action action)
    {
        _actions[id] = action;
        if (!HotkeyParser.TryParse(combo, out var mods, out var vk))
        { _failed.Add($"{name} (neplatný zápis „{combo}“)"); return; }
        if (Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) return;
        if (mods == 0 && Native.RegisterHotKey(_hwnd, id, Native.MOD_CONTROL | Native.MOD_NOREPEAT, vk))
        {
            _fallback.Add($"{name}: Ctrl+{combo}");
            AppLog.Warn($"Zkratka {combo} ({name}) je obsazená – zaregistrována jako Ctrl+{combo}.");
            return;
        }
        _failed.Add($"{name} ({combo})");
        AppLog.Warn($"Zkratku {combo} ({name}) se nepodařilo zaregistrovat (používá ji jiná aplikace). Změňte ji v záložce Zkratky.");
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception e) { AppLog.Error($"Zkratka: {e.Message}"); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _src.RemoveHook(Hook);
    }
}
