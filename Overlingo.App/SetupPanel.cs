using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overlingo.App.Capture;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Úvodní nastavení: co si nainstalovat (se stavem Nalezeno / Chybí) a kde vytvořit API klíče. Odkazy se otevírají v prohlížeči.</summary>
internal static class SetupPanel
{
    public const string OcrCommand = "Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";

    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
    static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(Loc.T("Odkaz se nepodařilo otevřít: " + e.Message) + "\n" + url, "Overlingo"); }
    }

    static TextBlock Head(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };

    static (TextBlock status, StackPanel row) Row(string title, string note, string? url, string? btn = "Otevřít stránku")
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        // tři pevné sloupce: název | stav | tlačítko – stav tak nikdy nezasahuje do tlačítka
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var status = new TextBlock { Foreground = Muted, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(status, 1);
        if (url != null)
        {
            var b = new Button { Content = btn, Padding = new Thickness(10, 4, 10, 4), ToolTip = url, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => Open(url);
            Grid.SetColumn(b, 2); top.Children.Add(b);
        }
        top.Children.Add(status);
        top.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(top);
        row.Children.Add(new TextBlock { Text = note, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return (status, row);
    }

    static void Set(TextBlock t, bool ok, string text) { t.Text = (ok ? "✔ " : "✖ ") + text; t.Foreground = ok ? Ok : Bad; }

    public static UIElement Build(AppController c)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Program funguje s tím, co už Windows obsahují. Níže je to, co je potřeba doinstalovat, a odkazy pro volitelné funkce. U každé položky vidíte, jestli už ji počítač má.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });

        root.Children.Add(Head("Nutné"));
        var (stNet, rNet) = Row(".NET 10 Desktop Runtime", "Potřebuje ho samotný program (pokud ho spouštíte, už je nainstalovaný).", "https://dotnet.microsoft.com/download/dotnet/10.0");
        Set(stNet, true, "nainstalováno"); root.Children.Add(rNet);

        var (stOcr, rOcr) = Row("Jazykový balíček Windows OCR", "Nastavení → Čas a jazyk → Jazyk a oblast → Přidat jazyk (zapněte funkci „Optical character recognition“). Nebo příkaz v PowerShellu jako správce – tlačítko níže ho zkopíruje. Pro jiný jazyk titulků než angličtinu změňte kód jazyka v příkazu (např. ja-JP).", null);
        Set(stOcr, c.OcrAvailable, c.OcrAvailable ? c.OcrInfo : "chybí");
        var copy = new Button { Content = "Kopírovat příkaz pro PowerShell", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        copy.Click += (_, _) => { try { Clipboard.SetText(OcrCommand); } catch { } };
        var jazyk = new Button { Content = "Otevřít nastavení jazyka", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        jazyk.Click += (_, _) => Open("ms-settings:regionlanguage");
        var wp = new WrapPanel(); wp.Children.Add(copy); wp.Children.Add(jazyk); rOcr.Children.Add(wp);
        root.Children.Add(rOcr);

        root.Children.Add(Head("Volitelné – lepší čtení a záznamová karta"));
        var (stTess, rTess) = Row("Tesseract OCR (druhý OCR engine)", "Pomůže u špatně čitelných titulků. Po instalaci ho program najde sám (nebo zadejte cestu v záložce OCR).", "https://github.com/UB-Mannheim/tesseract/wiki");
        bool tess = c.TesseractInfo.StartsWith("Tesseract nalezen", StringComparison.OrdinalIgnoreCase);
        Set(stTess, tess, tess ? "nalezen" : "nenalezen (volitelné)"); root.Children.Add(rTess);

        var (stCap, rCap) = Row("Záznamová karta", "Pokud hrajete z konzole přes záznamovou kartu, program ji najde automaticky. Zdroj „Záznamová karta“ vybíráte v záložce Zdroj obrazu.", null);
        Set(stCap, false, "hledám…"); stCap.Foreground = Muted; root.Children.Add(rCap);
        _ = FillCardsAsync(stCap);

        root.Children.Add(Head("Překladače a API klíče (volitelné)"));
        root.Children.Add(new TextBlock { Text = "Bez klíče funguje neoficiální Google Translate. Pro stabilnější nebo kvalitnější překlad si můžete vytvořit klíč; vložíte ho v záložce Překlad.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) });
        void Key(string title, string note, string url, string? existing)
        {
            var (st, r) = Row(title, note, url, "Vytvořit klíč");
            if (existing != null) Set(st, existing.Length > 0, existing.Length > 0 ? "klíč uložen" : "klíč není uložen");
            root.Children.Add(r);
        }
        var s = c.Settings;
        Key("Gemini (AI překlad)", "Klíč vytvoříte v Google AI Studio tlačítkem Create API key. Text titulků se posílá Googlu a využití může být zpoplatněno.", "https://aistudio.google.com/apikey", s.GeminiKeyProtected);
        Key("DeepL", "Zvolte plán DeepL API (Free: 500 000 znaků měsíčně). Předplatné „DeepL Translator“ klíč k API nedává. Klíč Free končí na :fx.", "https://www.deepl.com/pro-api", s.DeepLKeyProtected);
        Key("Google Cloud Translation", "Vyžaduje Google Cloud projekt, zapnuté Cloud Translation API a zapnuté účtování. Klíč se vytváří v Credentials.", "https://docs.cloud.google.com/translate/docs/setup", s.GoogleCloudKeyProtected);
        Key("OpenAI (volitelně)", "Pro AI překlad přes OpenAI-kompatibilní server: adresa https://api.openai.com/v1.", "https://platform.openai.com/api-keys", null);
        Key("OpenRouter (volitelně)", "Jedna adresa pro mnoho modelů: https://openrouter.ai/api/v1.", "https://openrouter.ai/keys", null);

        root.Children.Add(Head("Lokální AI (bez internetu a bez poplatků)"));
        var (_, rOl) = Row("Ollama", "Po instalaci stáhněte menší model a v záložce Překlad zvolte AI přes OpenAI-kompatibilní server (adresa http://localhost:11434/v1).", "https://ollama.com/download/windows", "Stáhnout");
        var (_, rLm) = Row("LM Studio", "Grafická aplikace pro lokální modely; zapněte její lokální server (adresa http://localhost:1234/v1).", "https://lmstudio.ai", "Stáhnout");
        root.Children.Add(rOl); root.Children.Add(rLm);
        return root;
    }

    static async Task FillCardsAsync(TextBlock st)
    {
        var devs = await ElgatoMfSource.ListDevicesAsync();
        var cards = devs.Where(ElgatoMfSource.LooksLikeCaptureCard).ToList();
        Set(st, cards.Count > 0, cards.Count > 0 ? "nalezena: " + string.Join(", ", cards) : "nenalezena (potřebujete jen při zdroji „Záznamová karta“)");
        if (cards.Count == 0) st.Foreground = Muted;
    }
}
