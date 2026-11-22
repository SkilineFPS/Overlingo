using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Overlingo.Core;

namespace Overlingo.App;

public partial class RegionSelectorWindow : Window
{
    readonly double _aspect;
    Point _start;
    bool _drag;
    Rect _sel;
    public RegionF? Result { get; private set; }

    public static BitmapSource ToBitmapSource(Frame f)
    {
        var bs = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null, f.Bgra, f.Width * 4);
        bs.Freeze();
        return bs;
    }

    public RegionSelectorWindow(Frame snapshot, RegionF? current, string? hint = null)
    {
        InitializeComponent();
        if (hint != null) Hint.Text = hint;
        _aspect = (double)snapshot.Width / snapshot.Height;
        Pic.Source = ToBitmapSource(snapshot);
        Width = SystemParameters.PrimaryScreenWidth * 0.9;
        Height = SystemParameters.PrimaryScreenHeight * 0.9;
        Loaded += (_, _) =>
        {
            Focus();
            if (current != null)
            {
                var img = ImageRect();
                _sel = new Rect(img.X + current.X * img.Width, img.Y + current.Y * img.Height, current.W * img.Width, current.H * img.Height);
                ShowSel(_sel);
            }
        };
        Cv.MouseLeftButtonDown += (_, e) => { _drag = true; _start = Clamp(e.GetPosition(Cv)); Cv.CaptureMouse(); };
        Cv.MouseMove += (_, e) =>
        {
            if (!_drag) return;
            var p = Clamp(e.GetPosition(Cv));
            _sel = new Rect(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
            ShowSel(_sel);
        };
        Cv.MouseLeftButtonUp += (_, _) => { _drag = false; Cv.ReleaseMouseCapture(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; }
            else if (e.Key == Key.Enter || e.Key == Key.Return) Confirm();
        };
        Cv.MouseRightButtonUp += (_, _) => Confirm();
    }

    Rect ImageRect()
    {
        double w = Cv.ActualWidth, h = Cv.ActualHeight;
        if (w <= 0 || h <= 0) { w = Width; h = Height; }
        double iw = w, ih = w / _aspect;
        if (ih > h) { ih = h; iw = h * _aspect; }
        return new Rect((w - iw) / 2, (h - ih) / 2, iw, ih);
    }

    Point Clamp(Point p)
    {
        var r = ImageRect();
        return new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));
    }

    void ShowSel(Rect r)
    {
        Sel.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(Sel, r.X); System.Windows.Controls.Canvas.SetTop(Sel, r.Y);
        Sel.Width = Math.Max(1, r.Width); Sel.Height = Math.Max(1, r.Height);
    }

    void Confirm()
    {
        var img = ImageRect();
        if (_sel.Width < 8 || _sel.Height < 8) { MessageBox.Show(this, Loc.T("Oblast je příliš malá. Označte celé místo, kde se objevují titulky."), Loc.T("Výběr oblasti")); return; }
        Result = new RegionF
        {
            X = (_sel.X - img.X) / img.Width, Y = (_sel.Y - img.Y) / img.Height,
            W = _sel.Width / img.Width, H = _sel.Height / img.Height
        }.Clamp();
        DialogResult = true;
    }
}
