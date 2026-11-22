using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Overlingo.App;

/// <summary>Malé okno „Špatný překlad“: původní věta + upravitelný překlad. Uložená oprava se příště použije místo překladače.</summary>
internal sealed class CorrectionWindow : Window
{
    readonly TextBox _tr = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 54, FontSize = 15, Margin = new Thickness(0, 4, 0, 10) };
    public string Translation => _tr.Text.Trim();

    public CorrectionWindow(string original, string current)
    {
        Title = "Overlingo – oprava překladu"; Width = 560; Height = 320; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)); Foreground = Brushes.White;
        _tr.Text = current;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Původní věta:", Foreground = Brushes.Gray });
        panel.Children.Add(new TextBox { Text = original, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 4, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Správný překlad:", Foreground = Brushes.Gray });
        panel.Children.Add(_tr);
        var ok = new Button { Content = "Uložit opravu", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Zrušit", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) => DialogResult = true;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        panel.Children.Add(bar);
        Content = panel;
        Loaded += (_, _) => { Activate(); _tr.Focus(); _tr.SelectAll(); };
    }
}
