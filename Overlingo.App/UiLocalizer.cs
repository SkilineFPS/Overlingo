using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>
/// Překládá texty ve WPF stromu podle <see cref="Loc"/>. Pamatuje si původní české texty,
/// takže jde jazyk kdykoli přepnout zpět. Texty nastavené později (stavy) se přeloží při dalším průchodu.
/// </summary>
public static class UiLocalizer
{
    sealed class Slot { public string Original = ""; public string Shown = ""; }
    static readonly ConditionalWeakTable<object, Dictionary<string, Slot>> Memory = new();
    static readonly List<WeakReference<Window>> Windows = new();
    static DispatcherTimer? _timer;

    public static void Init()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w) { Windows.Add(new WeakReference<Window>(w)); Apply(w); }
        }));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => ApplyAll();
        _timer.Start();
    }

    public static void ApplyAll()
    {
        Windows.RemoveAll(r => !r.TryGetTarget(out var w) || !w.IsLoaded);
        foreach (var r in Windows.ToArray()) if (r.TryGetTarget(out var w)) Apply(w);
    }

    public static void Apply(DependencyObject root)
    {
        try { Walk(root, 0); } catch (Exception ex) { AppLog.Warn($"Lokalizace okna selhala: {ex.Message}"); }
    }

    static void Walk(DependencyObject o, int depth)
    {
        if (depth > 60) return;
        switch (o)
        {
            case Window w: Set(w, "title", w.Title, v => w.Title = v); break;
            case TextBlock tb: Set(tb, "text", tb.Text, v => tb.Text = v); break;
            case TextBox tb2 when tb2.IsReadOnly && tb2.Name != "LogBox": Set(tb2, "text", tb2.Text, v => tb2.Text = v); break;
            case Run r: Set(r, "run", r.Text, v => r.Text = v); break;
            case HeaderedContentControl hc when hc.Header is string h: Set(hc, "header", h, v => hc.Header = v); break;
        }
        if (o is ContentControl cc && cc.Content is string c) Set(cc, "content", c, v => cc.Content = v);
        if (o is FrameworkElement fe && fe.ToolTip is string tip) Set(fe, "tip", tip, v => fe.ToolTip = v);

        foreach (var child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) Walk(d, depth + 1);
    }

    static void Set(object owner, string prop, string current, Action<string> write)
    {
        if (string.IsNullOrEmpty(current)) return;
        var slots = Memory.GetOrCreateValue(owner);
        if (!slots.TryGetValue(prop, out var slot)) { slot = new Slot { Original = current, Shown = current }; slots[prop] = slot; }
        else if (current != slot.Shown) { slot.Original = current; slot.Shown = current; } // text byl mezitím změněn kódem
        var want = Loc.T(slot.Original);
        if (want != current) { slot.Shown = want; write(want); }
    }
}
