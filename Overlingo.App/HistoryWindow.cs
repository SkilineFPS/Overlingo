using System.Windows;
using System.Windows.Controls;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Okno s historií titulků (originál + překlad), s kopírováním a vymazáním.</summary>
internal sealed class HistoryWindow : Window
{
    readonly SubtitleHistory _h;
    readonly TextBox _box = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14 };
    readonly CheckBox _orig = new() { Content = "Zobrazit originál", IsChecked = true, Margin = new Thickness(8) };

    public HistoryWindow(SubtitleHistory h)
    {
        _h = h;
        Title = "Overlingo – historie titulků"; Width = 560; Height = 640;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x1D, 0x23));
        _box.Background = Background; _box.Foreground = System.Windows.Media.Brushes.White; _box.BorderThickness = new Thickness(0);
        var copy = new Button { Content = "Kopírovat vše", Margin = new Thickness(8) };
        var clear = new Button { Content = "Vymazat", Margin = new Thickness(0, 8, 8, 8) };
        copy.Click += (_, _) => { try { Clipboard.SetText(_h.ToText(_orig.IsChecked == true)); } catch { } };
        clear.Click += (_, _) => _h.Clear();
        _orig.Checked += (_, _) => Refresh(); _orig.Unchecked += (_, _) => Refresh();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(copy); bar.Children.Add(clear); bar.Children.Add(_orig);
        var dock = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar); dock.Children.Add(_box);
        Content = dock;
        Action on = () => Dispatcher.BeginInvoke(new Action(Refresh));
        _h.Changed += on;
        Closed += (_, _) => _h.Changed -= on;
        Refresh();
    }

    void Refresh()
    {
        _box.Text = _h.ToText(_orig.IsChecked == true);
        _box.ScrollToEnd();
    }
}
