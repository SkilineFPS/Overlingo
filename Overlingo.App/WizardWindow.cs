using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overlingo.App.Capture;
using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Průvodce prvním spuštěním: zdroj obrazu → jazyky a překladač → hotovo (volitelně rovnou editor umístění).</summary>
internal sealed class WizardWindow : Window
{
    readonly AppSettings _s;
    readonly List<UIElement> _pages = new();
    int _page;
    readonly Button _back = new() { Content = "← Zpět", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _next = new() { Content = "Další →", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
    readonly ContentControl _host = new() { Margin = new Thickness(24) };
    readonly TextBlock _step = new() { Foreground = Brushes.Gray, Margin = new Thickness(24, 16, 24, 0) };

    readonly RadioButton _rWin = new() { Content = "Okno (vyberu ze seznamu)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    readonly RadioButton _rProg = new() { Content = "Program (podle názvu, připojí se i po restartu hry)", Margin = new Thickness(0, 4, 0, 0) };
    readonly RadioButton _rMon = new() { Content = "Celá obrazovka", Margin = new Thickness(0, 4, 0, 0) };
    readonly RadioButton _rCap = new() { Content = "Záznamová karta (Elgato)", Margin = new Thickness(0, 4, 0, 0) };
    readonly ComboBox _cbWin = new() { IsEditable = true, Margin = new Thickness(20, 4, 0, 4), DisplayMemberPath = "Label" };
    readonly ComboBox _cbProg = new() { IsEditable = true, Margin = new Thickness(20, 4, 0, 4) };
    readonly ComboBox _cbMon = new() { Margin = new Thickness(20, 4, 0, 4) };
    readonly ComboBox _cbCap = new() { Margin = new Thickness(20, 4, 0, 4) };
    readonly ComboBox _cbSrc = new() { DisplayMemberPath = "Name", Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _cbTgt = new() { DisplayMemberPath = "Name", Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _cbTr = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly CheckBox _openEditor = new() { Content = "Hned potom otevřít grafický editor umístění titulků", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    public bool OpenEditorAfter => _openEditor.IsChecked == true;

    sealed record WinChoice(string Label, string Match);

    public WizardWindow(AppSettings s, AppController c)
    {
        _s = s;
        foreach (var rb in new[] { _rWin, _rProg, _rMon, _rCap }) rb.Foreground = Brushes.White;
        _openEditor.Foreground = Brushes.White;
        Title = "Overlingo – první nastavení"; Width = 720; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)); Foreground = Brushes.White;
        _cbWin.ItemsSource = SourceFactory.ListWindows().Select(w => new WinChoice($"{w.Title}  [{w.ProcessName}]", w.Title)).ToList();
        _cbProg.ItemsSource = SourceFactory.ListPrograms();
        var mons = MonitorEnumerator.List(); _cbMon.ItemsSource = mons; _cbMon.SelectedIndex = mons.Count > 0 ? 0 : -1;
        _cbWin.Text = s.WindowMatch; _cbProg.Text = s.ProgramName;
        _cbCap.ItemsSource = new[] { "Hledám záznamové karty…" }; _cbCap.SelectedIndex = 0; _ = LoadCardsAsync();
        _cbSrc.ItemsSource = Languages.All; _cbTgt.ItemsSource = Languages.All;
        _cbSrc.SelectedItem = Languages.Find(s.SourceLang); _cbTgt.SelectedItem = Languages.Find(s.TargetLang);
        _cbTr.ItemsSource = new[] { Loc.T("Google Translate (bez klíče, výchozí)"), Loc.T("Google Cloud Translation (klíč)"), Loc.T("DeepL (klíč)"), Loc.T("Gemini (AI, klíč)"), Loc.T("AI – Ollama / LM Studio / OpenAI") };
        _cbTr.SelectedIndex = (int)s.Translator;

        TextBlock H(string t) => new() { Text = t, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        TextBlock P(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 0, 0, 8) };

        var p1 = new StackPanel();
        p1.Children.Add(H("Vítejte v Overlingo")); p1.Children.Add(P("Odkud má program číst obraz s titulky?"));
        foreach (var e in new UIElement[] { _rWin, _cbWin, _rProg, _cbProg, _rMon, _cbMon, _rCap, _cbCap }) p1.Children.Add(e);
        var p2 = new StackPanel();
        p2.Children.Add(H("Jazyky a překladač"));
        p2.Children.Add(P("Jazyk titulků ve hře:")); p2.Children.Add(_cbSrc);
        p2.Children.Add(P("Přeložit do:")); p2.Children.Add(_cbTgt);
        p2.Children.Add(P("Překladač (API klíče doplníte později v záložce Překlad):")); p2.Children.Add(_cbTr);
        var p3 = new StackPanel();
        p3.Children.Add(H("Skoro hotovo"));
        p3.Children.Add(P("Teď určíte, kde se ve hře zobrazují původní titulky a kde se má ukázat překlad. Hra musí mezitím běžet, aby program získal snímek."));
        p3.Children.Add(P("Překlad pak spustíte klávesou F8 (zastavení také F8). Všechno půjde kdykoli změnit v nastavení."));
        p3.Children.Add(_openEditor);
        var p0 = new DockPanel();
        var h0 = H("Co si nainstalovat"); DockPanel.SetDock(h0, Dock.Top); p0.Children.Add(h0);
        p0.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = SetupPanel.Build(c) });
        _pages.AddRange(new UIElement[] { p0, p1, p2, p3 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 24, 20) };
        buttons.Children.Add(_back); buttons.Children.Add(_next);
        var dock = new DockPanel(); DockPanel.SetDock(_step, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(_step); dock.Children.Add(buttons); dock.Children.Add(_host);
        Content = dock;
        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += (_, _) => { if (_page == _pages.Count - 1) Finish(); else Show(_page + 1); };
        Show(0);
    }

    sealed record Card(string Name, string Label) { public override string ToString() => Label; }
    async Task LoadCardsAsync()
    {
        var devs = await ElgatoMfSource.ListDevicesAsync();
        var items = new List<Card> { new("", devs.Count == 0 ? Loc.T("Automaticky (žádná karta nenalezena)") : Loc.T("Automaticky (doporučeno)")) };
        items.AddRange(devs.Select(d => new Card(d, d + (ElgatoMfSource.LooksLikeCaptureCard(d) ? "" : Loc.T("  (nejspíš kamera)")))));
        _cbCap.ItemsSource = items; _cbCap.SelectedItem = items.FirstOrDefault(i => i.Name == _s.CaptureDevice) ?? items[0];
        if (devs.Any(ElgatoMfSource.LooksLikeCaptureCard) && _rWin.IsChecked == true && string.IsNullOrWhiteSpace(_cbWin.Text)) _rCap.IsChecked = true;
    }

    void Show(int i)
    {
        _page = Math.Clamp(i, 0, _pages.Count - 1);
        _host.Content = _pages[_page];
        _step.Text = $"Krok {_page + 1} z {_pages.Count}";
        _back.IsEnabled = _page > 0;
        _next.Content = _page == _pages.Count - 1 ? "Dokončit" : "Další →";
    }

    void Finish()
    {
        _s.Source = _rProg.IsChecked == true ? SourceKind.Program : _rMon.IsChecked == true ? SourceKind.Monitor : _rCap.IsChecked == true ? SourceKind.Elgato : SourceKind.Window;
        _s.WindowMatch = (_cbWin.SelectedItem as WinChoice)?.Match ?? _cbWin.Text.Trim();
        _s.ProgramName = _cbProg.Text.Trim();
        _s.CaptureDevice = (_cbCap.SelectedItem as Card)?.Name ?? "";
        if (_cbMon.SelectedIndex >= 0) _s.MonitorIndex = _cbMon.SelectedIndex;
        _s.SourceLang = (_cbSrc.SelectedItem as Lang)?.Code ?? _s.SourceLang;
        _s.TargetLang = (_cbTgt.SelectedItem as Lang)?.Code ?? _s.TargetLang;
        _s.Translator = (TranslatorKind)Math.Max(0, _cbTr.SelectedIndex);
        DialogResult = true;
    }
}
