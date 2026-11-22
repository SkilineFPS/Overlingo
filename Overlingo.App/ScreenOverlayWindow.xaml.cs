using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Průhledné okno přes celý zdroj: české řádky se kreslí přesně přes původní anglický text. Vždy click-through.</summary>
public partial class ScreenOverlayWindow : Window
{
    AppSettings _s = new();
    IReadOnlyList<ScreenLine> _lines = Array.Empty<ScreenLine>();
    public IntPtr Hwnd { get; private set; }

    public ScreenOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            Native.SetLong(Hwnd, Native.GWL_EXSTYLE,
                Native.GetLong(Hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
            ApplyFlags();
        };
        SizeChanged += (_, _) => Render();
    }

    public void ApplySettings(AppSettings s) { _s = s; ApplyFlags(); Render(); }

    void ApplyFlags()
    {
        if (Hwnd == IntPtr.Zero) return;
        bool exclude = _s.ExcludeOverlayFromCapture || _s.Source == SourceKind.Monitor;
        Native.SetWindowDisplayAffinity(Hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    public void PlaceOver(Native.RECT r) =>
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    public void ReassertTopmost() =>
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    public void SetLines(IReadOnlyList<ScreenLine> lines) { _lines = lines; Render(); }
    public void Clear() => SetLines(Array.Empty<ScreenLine>());

    void Render()
    {
        Cv.Children.Clear();
        double W = Cv.ActualWidth > 0 ? Cv.ActualWidth : ActualWidth, H = Cv.ActualHeight > 0 ? Cv.ActualHeight : ActualHeight;
        if (W <= 0 || H <= 0) return;
        var fam = new FontFamily(_s.FontFamily);
        Brush fg; try { fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_s.TextColor)); } catch { fg = Brushes.White; }
        byte alpha = 255;   // musí úplně zakrýt původní text (neprůhledná černá)
        var bg = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        double ppd = 1.0; try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        foreach (var l in _lines)
        {
            double left = l.X * W, top = l.Y * H, w = l.W * W, h = l.H * H;
            // zvětšit krycí pole o rezervu, aby po stranách nevykukovaly okraje písmen původního textu
            double padX = Math.Max(6, h * 0.35), padY = Math.Max(3, h * 0.2);
            left -= padX; top -= padY; w += 2 * padX; h += 2 * padY;
            double fs = Math.Clamp(h * _s.ScreenFontScale, 11, 48);
            // překlad se vejde do boxu zmenšením písma (a zalomením), box se nezvětšuje daleko za originál
            double maxTextW = Math.Max(w * 1.15, 140) - 8;
            double maxTextH = Math.Max(h * 1.7, 22) - 2;
            fs = TextFit.Fit(l.Czech, fam, FontWeights.Normal, fs, Math.Max(9, fs * 0.6), maxTextW, maxTextH, ppd);
            var tb = new TextBlock { Text = l.Czech, FontFamily = fam, FontSize = fs, Foreground = fg, TextWrapping = TextWrapping.Wrap, MaxWidth = maxTextW };
            var b = new Border { Background = bg, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), MinWidth = w, MinHeight = h, Child = tb };
            Canvas.SetLeft(b, Math.Max(0, Math.Min(left, W - 40))); Canvas.SetTop(b, Math.Max(0, Math.Min(top, H - h)));
            Cv.Children.Add(b);
        }
    }
}
