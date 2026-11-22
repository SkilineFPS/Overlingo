using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Overlingo.Core;
using Frame = Overlingo.Core.Frame;

namespace Overlingo.App;

public enum PlacementPreset { Custom, Above, BottomCenter, Top, Cover }

public sealed record PlacementResult(RegionF Region, RegionF Overlay, PlacementPreset Preset);

/// <summary>
/// Grafický editor: živý snímek ze zdroje, žlutý obdélník = kde se čtou titulky, modrý = kde se ukáže překlad.
/// Obdélníky se táhnou myší (roh = změna velikosti); tlačítka nahoře nastaví typické rozmístění.
/// Souřadnice jsou relativní (0–1) k obrazu zdroje.
/// </summary>
public sealed class PlacementEditorWindow : Window
{
    readonly Canvas _cv = new() { Background = Brushes.Transparent };
    readonly Image _pic = new() { Stretch = Stretch.Uniform };
    readonly double _aspect;
    readonly Box _read, _over;
    PlacementPreset _preset;
    public PlacementResult? Result { get; private set; }

    sealed class Box
    {
        public Rectangle Body = new(), Grip = new() { Width = 16, Height = 16, Cursor = Cursors.SizeNWSE };
        public TextBlock Label = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false, Margin = new Thickness(4, 2, 0, 0) };
        public RegionF R = new();
    }

    public PlacementEditorWindow(Frame snapshot, RegionF region, RegionF overlay, PlacementPreset current, string? note = null)
    {
        Title = note == null ? "Umístění titulků" : "Umístění titulků – " + note; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = SystemParameters.PrimaryScreenWidth * 0.9; Height = SystemParameters.PrimaryScreenHeight * 0.9;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
        _aspect = (double)snapshot.Width / snapshot.Height;
        _pic.Source = RegionSelectorWindow.ToBitmapSource(snapshot);
        _preset = current;
        _read = MakeBox(Color.FromRgb(0xFF, 0xC1, 0x07), "Čtení titulků (originál)", region, false);
        _over = MakeBox(Color.FromRgb(0x42, 0xA5, 0xF5), "Zde se ukáže překlad", overlay, true);

        var bar = new WrapPanel { Margin = new Thickness(8) };
        void AddBtn(string text, Action a) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) }; b.Click += (_, _) => a(); bar.Children.Add(b); }
        AddBtn("Nad originálem (sleduje text)", () => ApplyPreset(PlacementPreset.Above));
        AddBtn("Dole uprostřed", () => ApplyPreset(PlacementPreset.BottomCenter));
        AddBtn("Nahoře", () => ApplyPreset(PlacementPreset.Top));
        AddBtn("Přes originál (zakrýt černým boxem)", () => ApplyPreset(PlacementPreset.Cover));
        AddBtn("✔ Použít", Confirm);
        AddBtn("Zrušit", () => DialogResult = false);
        bar.Children.Add(new TextBlock { Text = "Táhněte obdélníky myší, roh mění velikost.", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });

        var grid = new Grid(); grid.Children.Add(_pic); grid.Children.Add(_cv);
        var dock = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar); dock.Children.Add(grid);
        Content = dock;
        _cv.SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; else if (e.Key == Key.Enter) Confirm(); };
    }

    Box MakeBox(Color c, string label, RegionF r, bool isOverlay)
    {
        var b = new Box { R = new RegionF { X = r.X, Y = r.Y, W = r.W, H = r.H } };
        b.Body.Stroke = new SolidColorBrush(c); b.Body.StrokeThickness = 2; b.Body.Fill = new SolidColorBrush(Color.FromArgb(0x44, c.R, c.G, c.B)); b.Body.Cursor = Cursors.SizeAll;
        b.Grip.Fill = new SolidColorBrush(c);
        b.Label.Text = label;
        _cv.Children.Add(b.Body); _cv.Children.Add(b.Label); _cv.Children.Add(b.Grip);
        Point start = default; RegionF orig = new(); bool move = false, size = false;
        b.Body.MouseLeftButtonDown += (_, e) => { move = true; start = e.GetPosition(_cv); orig = Copy(b.R); b.Body.CaptureMouse(); };
        b.Grip.MouseLeftButtonDown += (_, e) => { size = true; start = e.GetPosition(_cv); orig = Copy(b.R); b.Grip.CaptureMouse(); e.Handled = true; };
        void Drag(Point p)
        {
            var img = ImageRect(); if (img.Width <= 0) return;
            double dx = (p.X - start.X) / img.Width, dy = (p.Y - start.Y) / img.Height;
            if (move) { b.R.X = Math.Clamp(orig.X + dx, 0, 1 - orig.W); b.R.Y = Math.Clamp(orig.Y + dy, 0, 1 - orig.H); }
            else if (size) { b.R.W = Math.Clamp(orig.W + dx, 0.03, 1 - orig.X); b.R.H = Math.Clamp(orig.H + dy, 0.02, 1 - orig.Y); }
            // Posun rámu s překladem = vlastní pevná poloha. Posun čtené oblasti zachová zvolené rozmístění (nad / přes originál) a přepočítá ho.
            if (isOverlay) { _preset = PlacementPreset.Custom; Layout(); }
            else if (_preset != PlacementPreset.Custom) ApplyPreset(_preset);
            else Layout();
        }
        b.Body.MouseMove += (_, e) => { if (move) Drag(e.GetPosition(_cv)); };
        b.Grip.MouseMove += (_, e) => { if (size) Drag(e.GetPosition(_cv)); };
        b.Body.MouseLeftButtonUp += (_, _) => { move = false; b.Body.ReleaseMouseCapture(); };
        b.Grip.MouseLeftButtonUp += (_, _) => { size = false; b.Grip.ReleaseMouseCapture(); };
        return b;
    }

    static RegionF Copy(RegionF r) => new() { X = r.X, Y = r.Y, W = r.W, H = r.H };

    Rect ImageRect()
    {
        double w = _cv.ActualWidth, h = _cv.ActualHeight;
        if (w <= 0 || h <= 0) return Rect.Empty;
        double iw = w, ih = w / _aspect;
        if (ih > h) { ih = h; iw = h * _aspect; }
        return new Rect((w - iw) / 2, (h - ih) / 2, iw, ih);
    }

    void Place(Box b)
    {
        var img = ImageRect(); if (img.IsEmpty) return;
        double x = img.X + b.R.X * img.Width, y = img.Y + b.R.Y * img.Height, w = b.R.W * img.Width, h = b.R.H * img.Height;
        Canvas.SetLeft(b.Body, x); Canvas.SetTop(b.Body, y); b.Body.Width = Math.Max(4, w); b.Body.Height = Math.Max(4, h);
        Canvas.SetLeft(b.Label, x); Canvas.SetTop(b.Label, y);
        Canvas.SetLeft(b.Grip, x + w - 16); Canvas.SetTop(b.Grip, y + h - 16);
    }
    void Layout() { Place(_read); Place(_over); }

    void ApplyPreset(PlacementPreset p)
    {
        _preset = p;
        var r = _read.R;
        double cx = r.X + r.W / 2;
        switch (p)
        {
            case PlacementPreset.Above:
            {
                double w = Math.Min(0.8, 2 * Math.Min(cx, 1 - cx)), h = Math.Min(0.18, Math.Max(0.06, r.Y - 0.01));
                _over.R = new RegionF { X = cx - w / 2, Y = Math.Max(0, r.Y - 0.01 - h), W = w, H = h };
                break;
            }
            case PlacementPreset.BottomCenter: _over.R = new RegionF { X = 0.15, Y = 0.82, W = 0.7, H = 0.14 }; break;
            case PlacementPreset.Top: _over.R = new RegionF { X = 0.15, Y = 0.04, W = 0.7, H = 0.14 }; break;
            case PlacementPreset.Cover:
            {
                double w = Math.Min(0.9, r.W * 1.1 + 0.02), h = Math.Min(0.3, r.H * 1.3 + 0.01);
                _over.R = new RegionF { X = Math.Clamp(cx - w / 2, 0, 1 - w), Y = Math.Clamp(r.Y + r.H / 2 - h / 2, 0, 1 - h), W = w, H = h };
                break;
            }
        }
        Layout();
    }

    void Confirm()
    {
        Result = new PlacementResult(Copy(_read.R).Clamp(), Copy(_over.R).Clamp(), _preset);
        DialogResult = true;
    }
}
