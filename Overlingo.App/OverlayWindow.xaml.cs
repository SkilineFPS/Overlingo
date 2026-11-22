using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App;

public partial class OverlayWindow : Window
{
    sealed class Entry { public string En = "", Cs = ""; public DateTime Expiry, MinUntil; public bool IsError, Live; }

    readonly List<Entry> _entries = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    AppSettings _s = new();
    bool _edit, _dragging;
    Native.POINT _dragStart, _dragOrigin;
    DateTime _lastErrorShown = DateTime.MinValue;
    DateTime _lastVisible = DateTime.UtcNow;
    /// <summary>Čtení OCR je pozastavené – překlad se nemá ukončovat kvůli „nevidím titulek“.</summary>
    public bool HoldLive { get; set; }
    /// <summary>Pipeline právě viděla původní titulek v oblasti.</summary>
    public void MarkVisible() => _lastVisible = DateTime.UtcNow;

    public IntPtr Hwnd { get; private set; }
    public bool EditMode => _edit;
    /// <summary>Uživatel přesunul/zvětšil overlay v režimu úprav (fyzické pixely).</summary>
    public event Action<int, int, int, int>? GeometryChanged;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            Native.SetLong(Hwnd, Native.GWL_EXSTYLE, Native.GetLong(Hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            ApplyWindowFlags();
        };
        _timer.Tick += (_, _) => Expire();
        _timer.Start();
        MouseLeftButtonDown += (_, _) =>
        {
            if (!_edit || Hwnd == IntPtr.Zero) return;
            Native.GetCursorPos(out _dragStart); Native.GetWindowRect(Hwnd, out var r);
            _dragOrigin = new Native.POINT { X = r.Left, Y = r.Top }; _dragging = true; CaptureMouse();
        };
        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            Native.GetCursorPos(out var c);
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, _dragOrigin.X + c.X - _dragStart.X, _dragOrigin.Y + c.Y - _dragStart.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
        MouseLeftButtonUp += (_, _) => { if (_dragging) { _dragging = false; ReleaseMouseCapture(); RaiseGeometry(); } };
        LocationChanged += (_, _) => RaiseGeometry();
        SizeChanged += (_, _) => RaiseGeometry();
        Closed += (_, _) => _timer.Stop();
    }

    void OnGripDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width = Math.Max(240, Width + e.HorizontalChange);
        Height = Math.Max(80, Height + e.VerticalChange);
    }

    void RaiseGeometry()
    {
        if (!_edit || Hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(Hwnd, out var r);
        GeometryChanged?.Invoke(r.Left, r.Top, r.Width, r.Height);
    }

    // ---------- nastavení vzhledu a vlastností okna
    public void ApplySettings(AppSettings s)
    {
        _s = s;
        Box.Background = new SolidColorBrush(Color.FromArgb(s.OverlayCoverOriginal ? (byte)255 : (byte)Math.Round(s.BackgroundOpacity * 255), 0, 0, 0));
        SetCover(s.OverlayCoverOriginal && !_edit, _coverMinW, _coverMinH);
        Render();
        ApplyWindowFlags();
    }

    double _coverMinW, _coverMinH;
    bool _preCover;
    DateTime _preCoverAt;
    /// <summary>Přečten nový originální titulek, překlad ještě nedorazil: starý překlad zmizí a prázdný černý box (velikosti nového originálu)
    /// zakrývá originál, dokud nepřijde překlad (nejdéle 1,5 s).</summary>
    public void BeginCover()
    {
        _entries.RemoveAll(e => !e.IsError);
        _preCover = true; _preCoverAt = DateTime.UtcNow;
        Render();
    }
    public void EndCover() { if (!_preCover) return; _preCover = false; Render(); }
    /// <summary>Režim „přes originál“: černý neprůhledný box aspoň tak velký jako původní titulek (rozměry v pixelech), vystředěný v okně.</summary>
    public void SetCover(bool on, double minWpx, double minHpx)
    {
        _coverMinW = minWpx; _coverMinH = minHpx;
        double k = 1.0; try { k = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        Box.VerticalAlignment = on ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Lines.VerticalAlignment = on ? VerticalAlignment.Center : VerticalAlignment.Top;
        Box.MinWidth = on ? minWpx / k : 0; Box.MinHeight = on ? minHpx / k : 0;
        if (_entries.Count > 0 || _preCover) Render();   // box se změnil → přepočítat zalomení a velikost písma
    }

    void ApplyWindowFlags()
    {
        if (Hwnd == IntPtr.Zero) return;
        long ex = Native.GetLong(Hwnd, Native.GWL_EXSTYLE);
        bool ct = _s.ClickThrough && !_edit;
        ex = ct ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetLong(Hwnd, Native.GWL_EXSTYLE, ex);
        // Při záchytu celého monitoru by si aplikace přečetla vlastní překlad → tam je vyloučení nutné vždy.
        bool exclude = _s.ExcludeOverlayFromCapture || _s.Source == SourceKind.Monitor;
        if (_s.Source == SourceKind.Monitor && !_s.ExcludeOverlayFromCapture)
            AppLog.Warn("Zdroj je celý monitor – overlay je proto vynuceně vyloučen ze záchytu (v OBS nebude vidět). Pro stream použijte zdroj Okno nebo Elgato.");
        bool ok = Native.SetWindowDisplayAffinity(Hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
        if (!ok && exclude)
            AppLog.Warn("Overlay se nepodařilo vyloučit ze záchytu (SetWindowDisplayAffinity) – hrozí zpětné čtení vlastního překladu.");
    }

    public void SetEditMode(bool on)
    {
        _edit = on;
        EditFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SetCover(_s.OverlayCoverOriginal && !on, _coverMinW, _coverMinH);
        ApplyWindowFlags();
        Render();
    }

    public void PlacePx(int x, int y, int w, int h) =>
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    /// <summary>Výchozí umístění: spodní část cílové oblasti (okno zdroje / monitor).</summary>
    public (int X, int Y, int W, int H) DefaultPlacement(Native.RECT target)
    {
        int w = (int)(target.Width * 0.7), h = (int)(target.Height * 0.2);
        return (target.Left + (target.Width - w) / 2, target.Top + (int)(target.Height * 0.68), w, h);
    }

    public void ReassertTopmost() =>
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    public bool ExcludedFromCaptureNow
    {
        get { if (Hwnd == IntPtr.Zero) return false; Native.GetWindowDisplayAffinity(Hwnd, out var a); return a == Native.WDA_EXCLUDEFROMCAPTURE; }
    }

    // ---------- obsah
    public static double AutoSeconds(string cs) => Math.Clamp(3.0 + cs.Length * 0.07, 4, 14);

    /// <summary>
    /// Překlad žije, dokud je vidět původní titulek (Live); po jeho zmizení zůstane ještě chvilku, nejméně však
    /// po minimální dobu na přečtení. Nový titulek ukončí předchozí, jakmile uplyne jeho minimální doba.
    /// Pevná doba z nastavení (DisplaySeconds &gt; 0) má přednost.
    /// </summary>
    public void AddSubtitle(string en, string cs, bool replaceLast = false, bool sourceGone = false)
    {
        var now = DateTime.UtcNow;
        _lastVisible = now; _preCover = false;
        _entries.RemoveAll(e => e.IsError);
        if (replaceLast && _entries.Count > 0) _entries.RemoveAt(_entries.Count - 1);
        else foreach (var e in _entries.Where(e => e.Live))
        { e.Live = false; e.Expiry = e.MinUntil > now ? e.MinUntil : now; }
        var n = new Entry { En = en, Cs = cs };
        if (_s.DisplaySeconds > 0) { n.MinUntil = n.Expiry = now.AddSeconds(_s.DisplaySeconds); }
        else if (sourceGone)
        {   // původní titulek zmizel už během překladu → ukáže se jen na dobu čtení a skončí
            n.MinUntil = now.AddSeconds(MinReadSeconds(cs)); n.Expiry = n.MinUntil.AddSeconds(0.5);
        }
        else { n.Live = true; n.MinUntil = now.AddSeconds(MinReadSeconds(cs)); n.Expiry = now.AddSeconds(14); }
        _entries.Add(n);
        while (_entries.Count > _s.MaxLines) _entries.RemoveAt(0);
        Render();
    }

    public static double MinReadSeconds(string cs) => Math.Clamp(2.0 + cs.Length * 0.045, 2.5, 7);

    /// <summary>Původní titulek zmizel ze hry: překlad skončí po krátké dohrávce, nejdřív však po minimální době čtení.</summary>
    public void OnSourceCleared()
    {
        if (_s.DisplaySeconds > 0) return;
        var now = DateTime.UtcNow;
        foreach (var e in _entries.Where(e => e.Live))
        { e.Live = false; var tail = now.AddSeconds(0.7); e.Expiry = e.MinUntil > tail ? e.MinUntil : tail; }
    }

    public void ShowError(string message)
    {
        if ((DateTime.UtcNow - _lastErrorShown).TotalSeconds < 5) return;
        _lastErrorShown = DateTime.UtcNow;
        _entries.RemoveAll(e => e.IsError);
        _entries.Add(new Entry { Cs = "⚠ " + message, IsError = true, Expiry = DateTime.UtcNow.AddSeconds(5) });
        Render();
    }

    public void ClearAll() { _entries.Clear(); Render(); }

    public void ShowSample()
    {
        AddSubtitle("Hey, keep your voice down. They're still watching the street.", "Hej, ztiš hlas. Pořád sledují ulici.");
    }

    void Expire()
    {
        var now = DateTime.UtcNow;
        // pojistka: když původní titulek nebyl 2,5 s vidět, překlad nesmí viset dál (zmeškaná událost „zmizel“)
        if (!HoldLive && _entries.Any(e => e.Live) && (now - _lastVisible).TotalSeconds > 2.5) OnSourceCleared();
        bool changed = _entries.RemoveAll(e => e.Expiry <= now) > 0;
        if (_preCover && (now - _preCoverAt).TotalSeconds > 1.5) { _preCover = false; changed = true; }   // pojistka: překlad nedorazil (šum / titulek zmizel)
        if (changed) Render();
    }

    void Render()
    {
        Lines.Children.Clear();
        bool covering = _s.OverlayCoverOriginal && !_edit;
        if (_entries.Count == 0)
        {
            Box.Visibility = _preCover && covering ? Visibility.Visible : Visibility.Collapsed;   // prázdný černý box přes originál
            return;
        }
        var fam = new FontFamily(_s.FontFamily);
        var shown = _s.OverlayCoverOriginal && !_edit ? _entries.Skip(Math.Max(0, _entries.Count - 1)).ToList() : _entries;
        var csBrush = MakeBrush(_s.TextColor, Colors.White);
        var enBrush = MakeBrush(_s.EnglishColor, Colors.LightGray);
        foreach (var e in shown)
        {
            if (_s.ShowEnglish && !(_s.OverlayCoverOriginal && !_edit) && !e.IsError && e.En.Length > 0)
                Lines.Children.Add(new TextBlock { Text = e.En, FontFamily = fam, FontSize = Math.Max(10, _s.FontSize * 0.7), Foreground = enBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            double fs = _s.FontSize, maxW = double.PositiveInfinity;
            if (covering && !e.IsError && _coverMinW > 0)
            {   // box má velikost originálu: překlad se do něj vejde zmenšením písma / zalomením, box se nezvětšuje do šířky
                double k = 1.0, ppd = 1.0; try { var d = VisualTreeHelper.GetDpi(this); k = d.DpiScaleX; ppd = d.PixelsPerDip; } catch { }
                double boxW = _coverMinW / k, boxH = _coverMinH / k;
                maxW = Math.Max(140, boxW * 1.12 - (Box.Padding.Left + Box.Padding.Right));
                double maxH = Math.Max(boxH * 1.5, _s.FontSize * 1.3) - (Box.Padding.Top + Box.Padding.Bottom);
                fs = TextFit.Fit(e.Cs, fam, FontWeights.SemiBold, _s.FontSize, Math.Max(10, _s.FontSize * 0.6), maxW, maxH, ppd);
            }
            Lines.Children.Add(new TextBlock
            {
                Text = e.Cs, FontFamily = fam, FontSize = fs, FontWeight = FontWeights.SemiBold, MaxWidth = maxW,
                Foreground = e.IsError ? Brushes.OrangeRed : csBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, ReferenceEquals(e, shown[^1]) ? 0 : 6)
            });
        }
        Box.Visibility = Visibility.Visible;
    }

    static Brush MakeBrush(string hex, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(fallback); }
    }
}
