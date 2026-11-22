using System.Net;
using System.Text;
using Overlingo.Core;

int pass = 0, fail = 0;
void T(string name, Func<Task> body)
{
    try { body().GetAwaiter().GetResult(); pass++; Console.WriteLine($"  OK    {name}"); }
    catch (Exception e) { fail++; Console.WriteLine($"  FAIL  {name}: {e.Message}"); }
}
void Eq<TV>(TV exp, TV act, string what = "") { if (!EqualityComparer<TV>.Default.Equals(exp, act)) throw new Exception($"{what} očekáváno '{exp}', je '{act}'"); }
void True(bool c, string what) { if (!c) throw new Exception(what); }

AppLog.FileLogging = false;

FakeHandler H(Func<HttpRequestMessage, (HttpStatusCode, string)> f) => new() { Fn = f };

Console.WriteLine("== Text");
T("Normalize/Similarity", async () =>
{
    Eq("a b", TextTools.Normalize("  a\n  b "));
    True(TextTools.Similarity("Hello there", "hello  there") > 0.99, "case/space");
    True(TextTools.Similarity("abc", "xyz") < 0.1, "different");
    await Task.CompletedTask;
});
T("QualityScore penalizuje smetí", async () =>
{
    double good = TextTools.QualityScore("Hey, keep your voice down. They're still watching the street.");
    double bad = TextTools.QualityScore("Hey, eep your voice down. Theytr&StiIIl watching the street.");
    True(good > bad, $"good {good} bad {bad}");
    await Task.CompletedTask;
});
T("IsPlausible", async () =>
{
    True(TextTools.IsPlausible("Let's go now", 3), "věta");
    True(!TextTools.IsPlausible("|| .. ~", 3), "smetí");
    True(!TextTools.IsPlausible("", 3), "prázdné");
    await Task.CompletedTask;
});

Console.WriteLine("== SubtitleTracker");
T("emituje až po stabilních čteních, bez duplicit", async () =>
{
    var t = new SubtitleTracker(2, 3);
    Eq(TrackerEvent.None, t.Observe("Hello there my friend").Event);
    var r = t.Observe("Hello there my friend");
    Eq(TrackerEvent.NewSubtitle, r.Event);
    Eq(TrackerEvent.None, t.Observe("Hello there my friend").Event);
    Eq(TrackerEvent.None, t.Observe("Hello there my friend.").Event, "drobná odchylka OCR");
    await Task.CompletedTask;
});
T("nový titulek po starém, pořadí zachováno", async () =>
{
    var t = new SubtitleTracker(2, 3); var got = new List<string>();
    foreach (var s in new[] { "First line here", "First line here", "Second line is different", "Second line is different" })
    { var r = t.Observe(s); if (r.Event == TrackerEvent.NewSubtitle) got.Add(r.Text!); }
    Eq("First line here|Second line is different", string.Join("|", got));
    await Task.CompletedTask;
});
T("Cleared po prázdných čteních a stejná věta se může vrátit", async () =>
{
    var clock = DateTime.UtcNow;
    var t = new SubtitleTracker(2, 3, clearAfter: 3, clock: () => clock); int n = 0, c = 0;
    void Feed(string? s) { var r = t.Observe(s); if (r.Event == TrackerEvent.NewSubtitle) n++; if (r.Event == TrackerEvent.Cleared) c++; }
    Feed("Same sentence again"); Feed("Same sentence again"); Feed(null); Feed(null); Feed(null);
    clock = clock.AddSeconds(6);
    Feed("Same sentence again"); Feed("Same sentence again");
    Eq(2, n, "emitováno"); Eq(1, c, "cleared");
    await Task.CompletedTask;
});
T("jednorázový šum nevyvolá titulek", async () =>
{
    var t = new SubtitleTracker(2, 3);
    Eq(TrackerEvent.None, t.Observe("Garbage frame once").Event);
    Eq(TrackerEvent.None, t.Observe("Totally other text").Event);
    await Task.CompletedTask;
});

T("Tracker: oříznutý fragment a OCR varianta nevytvoří druhý překlad", async () =>
{
    var t = new SubtitleTracker(2, 3); int news = 0;
    void Feed(string s) { if (t.Observe(s).Event == TrackerEvent.NewSubtitle) news++; }
    Feed("Right. Andy. The one with the clothes. Nice to meet you."); Feed("Right. Andy. The one with the clothes. Nice to meet you.");
    Feed("e one with the clothes. Nice to meet you."); Feed("e one with the clothes. Nice to meet you.");            // oříznutý začátek
    Feed("Right. Andy. The one with the clothes. Nice to meet yo"); Feed("Right. Andy. The one with the clothes. Nice to meet yo."); // varianta
    Eq(1, news, "jen jeden titulek"); True(t.Suppressed >= 2, "potlačeno " + t.Suppressed);
    await Task.CompletedTask;
});
T("Tracker: úplnější čtení nahradí dřívější fragment (Updated)", async () =>
{
    var t = new SubtitleTracker(2, 3); var ev = new List<TrackerEvent>();
    foreach (var s in new[] { "Okay, so I just grabbed some stuff", "Okay, so I just grabbed some stuff",
                              "Okay, so I just grabbed some stuff that should fit her", "Okay, so I just grabbed some stuff that should fit her" })
    { var r = t.Observe(s); if (r.Event != TrackerEvent.None) ev.Add(r.Event); }
    Eq("NewSubtitle|Updated", string.Join("|", ev));
    await Task.CompletedTask;
});
T("Tracker: stejná věta po zmizení se vrátí až po ochranné době", async () =>
{
    var clock = DateTime.UtcNow; var t = new SubtitleTracker(2, 3, clearAfter: 2, clock: () => clock); int n = 0;
    void Feed(string? s) { if (t.Observe(s).Event == TrackerEvent.NewSubtitle) n++; }
    Feed("Watch your step now"); Feed("Watch your step now"); Feed(null); Feed(null);
    clock = clock.AddSeconds(1); Feed("Watch your step now"); Feed("Watch your step now"); Eq(1, n, "blikání se nepočítá");
    clock = clock.AddSeconds(10); Feed(null); Feed(null); Feed("Watch your step now"); Feed("Watch your step now");
    await Task.CompletedTask;
});
T("Pipeline: nová věta se stejným otiskem obrazu se přečte díky heartbeat", async () =>
{
    // dvě různé věty na stejném místě = stejný otisk; bez heartbeat by se druhá nikdy nepřečetla
    async Task<List<string>> Run(int heartbeat)
    {
        var src = new ScriptedSource(); src.NextVariant(0);
        var ocr = new ScriptedOcr(new[] { "First subtitle line", "First subtitle line", "Second one now", "Second one now", "Second one now", "Second one now" });
        var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
        var s = new AppSettings { StableReads = 2 };
        var pl = new TranslationPipeline(ocr, svc, () => s) { HeartbeatMs = heartbeat };
        var q = pl.BeginManual(src);
        for (int i = 0; i < 6; i++) await pl.TickAsync(s, default);
        var l = new List<string>(); while (q.TryRead(out var e)) l.Add(e); return l;
    }
    Eq("First subtitle line|Second one now", string.Join("|", await Run(0)), "s heartbeat");
    Eq("First subtitle line", string.Join("|", await Run(int.MaxValue)), "bez heartbeat (původní chyba)");
});
T("Pipeline: titulek zmizel dřív než dorazil překlad → SourceGone", async () =>
{
    var src = new ScriptedSource(); src.NextVariant(0);
    var ocr = new ScriptedOcr(new[] { "Hello there my friend", "Hello there my friend", "", "", "", "", "", "", "", "" });
    var svc = new TranslationService(new SlowEcho(700), new TranslationCache());
    var s = new AppSettings { StableReads = 2, IntervalMs = 40 };
    var pl = new TranslationPipeline(ocr, svc, () => s) { HeartbeatMs = 0 };
    var tcs = new TaskCompletionSource<SubtitleTranslated>();
    pl.Translated += r => tcs.TrySetResult(r);
    await pl.StartAsync(src);
    var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
    await pl.StopAsync();
    if (done != tcs.Task) throw new Exception("překlad nedorazil");
    True(tcs.Task.Result.SourceGone, "SourceGone");
    True(tcs.Task.Result.Box != null, "Box");
});
T("Pipeline: poloha titulku se bere z OCR, ne ze světlých pixelů", async () =>
{
    var src = new ScriptedSource(); src.NextVariant(0);
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
    var s = new AppSettings { StableReads = 2, IntervalMs = 40, Region = new RegionF { X = 0.1, Y = 0.8, W = 0.8, H = 0.1 } };
    var pl = new TranslationPipeline(new BoundsOcr(), svc, () => s) { HeartbeatMs = 0 };
    var tcs = new TaskCompletionSource<SubtitleTranslated>();
    pl.Translated += r => tcs.TrySetResult(r);
    await pl.StartAsync(src);
    var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
    await pl.StopAsync();
    if (done != tcs.Task) throw new Exception("překlad nedorazil");
    var b = tcs.Task.Result.Box!;
    True(Math.Abs(b.X - (0.1 + 0.3 * 0.8)) < 1e-6 && Math.Abs(b.W - 0.32) < 1e-6, $"X={b.X} W={b.W}");
});
T("Filtr: panel se statistikami není titulek", async () =>
{
    var panel = "Input ø.ø Face detection 4 Beauty 0.2 Not Optical flow ns Residual HDR Present 6.2 ns 0.1 ns 3.0 ns 0.6 ns ø.ø ns 12ø.ø Submit fps 1% low 114.3";
    True(TextTools.LooksLikeUiNoise(panel), "panel");
    True(!TextTools.LooksLikeUiNoise("First trip out since beautiful Juliana came through."), "titulek 1");
    True(!TextTools.LooksLikeUiNoise("You owe me 500 dollars by 10 o'clock, got it?"), "titulek s čísly");
    True(TextTools.LooksLikeUiNoise(new string('a', 150) + " " + new string('b', 100)), "příliš dlouhé");
    True(!TextTools.LooksLikeUiNoise(new string('a', 150) + " " + new string('b', 100), 0), "limit vypnut");
    await Task.CompletedTask;
});
T("GameArea: černé pruhy se najdou, bez pruhů = null", async () =>
{
    Frame Make(int w, int h, int barTop, int barBottom, int barLeft = 0)
    {
        var f = new Frame(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            bool bar = y < barTop || y >= h - barBottom || x < barLeft;
            byte v = (byte)(bar ? 0 : 90 + ((x * 7 + y * 13) % 120));
            int i = (y * w + x) * 4; f.Bgra[i] = v; f.Bgra[i + 1] = v; f.Bgra[i + 2] = v; f.Bgra[i + 3] = 255;
        }
        return f;
    }
    var a = GameAreaDetector.Detect(Make(800, 450, 40, 40))!;
    True(Math.Abs(a.Y - 40.0 / 450) < 0.01 && Math.Abs(a.H - 370.0 / 450) < 0.01 && a.X == 0 && a.W == 1, $"Y={a.Y} H={a.H}");
    var b = GameAreaDetector.Detect(Make(800, 450, 0, 0, 120))!;
    True(Math.Abs(b.X - 0.15) < 0.01 && b.Y == 0, $"X={b.X}");
    True(GameAreaDetector.Detect(Make(800, 450, 0, 0)) == null, "bez pruhů");
    True(GameAreaDetector.Detect(Make(800, 450, 300, 300)) == null, "příliš málo obrazu");
    var s = new AppSettings { AutoGameArea = true, GameArea = a, Region = new RegionF { X = 0, Y = 0.5, W = 1, H = 0.5 } };
    var e = s.EffectiveRegion;
    True(Math.Abs(e.Y - (a.Y + 0.5 * a.H)) < 1e-9 && Math.Abs(e.H - 0.5 * a.H) < 1e-9, "EffectiveRegion");
    s.AutoGameArea = false; Eq(0.5, s.EffectiveRegion.Y, "vypnuto");
    await Task.CompletedTask;
});
T("Opravy překladu mají přednost a přežijí restart", async () =>
{
    var f = Path.Combine(Path.GetTempPath(), "cor" + Guid.NewGuid().ToString("N") + ".json");
    var cs = new CorrectionStore(f);
    cs.Set("Hello  There", "", "Čau, ty tam");
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache()) { HedgeMs = 0, Corrections = cs };
    var r = await svc.TranslateAsync("hello there", CancellationToken.None);
    Eq("Čau, ty tam", r.Text);
    svc.LangTag = "en>de";
    Eq("CS:hello there", (await svc.TranslateAsync("hello there", CancellationToken.None)).Text);
    var again = new CorrectionStore(f);
    True(again.TryGet("HELLO THERE", "", out var v) && v == "Čau, ty tam", "po načtení ze souboru");
    True(again.Remove("hello there", "") && again.Count == 0, "smazání");
});
T("Starý uložený jazyk „cs“ se migruje na výchozí angličtinu, nová volba zůstane", async () =>
{
    var old = AppSettings.FromJson("{\"UiLang\":\"cs\"}");
    Eq("en", old.UiLang, "migrace");
    var chosen = AppSettings.FromJson("{\"UiLang\":\"cs\",\"UiLangVersion\":1}");
    Eq("cs", chosen.UiLang, "volba po migraci zůstává");
    Eq("en", new AppSettings().UiLang, "výchozí");
    await Task.CompletedTask;
});
T("Lokalizace: výběr jazyka, náhrada a pád zpět na češtinu; en.json pokrývá XAML", async () =>
{
    var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Overlingo.App", "Languages");
    var av = Loc.Available(dir);
    True(av.Any(a => a.Code == "cs") && av.Any(a => a.Code == "en"), "dostupné jazyky");
    Eq("en", Loc.Resolve("", "de", av), "výchozí je angličtina");
    Eq("en", Loc.Resolve("", "cs", av), "i při české Windows je výchozí angličtina");
    Eq("cs", Loc.Resolve("cs", "en", av), "uložená čeština");
    Eq("en", Loc.Resolve("en", "cs", av), "uložená volba");
    True(Loc.Set("en", dir), "načtení en");
    Eq("Save", Loc.T("Uložit"), "překlad");
    Eq("Neexistující text", Loc.T("Neexistující text"), "chybějící klíč → česky");
    Loc.Set("en", dir);
    Eq("Report saved: C:\\x.txt", Loc.T("Report uložen: C:\\x.txt"), "vzor s hodnotou");
    Eq("Mode: everything on screen.", Loc.T("Režim: vše na obrazovce."), "vzor + přeložená hodnota");
    Eq("12 ms (average 10 ms, method: Auto)", Loc.T("12 ms (průměr 10 ms, metoda: Auto)"), "hodnota dashboardu");
    Eq("Saved, but these values were invalid and kept their old values: Upscale, Retries", Loc.T("Uloženo, ale tyto hodnoty byly neplatné a zůstaly původní: Zvětšení, Opakování"), "seznam hodnot");
    Eq("Unexpected error:\nboom\n\nDetails are in the Logs tab / in C:\\L", Loc.T("Neočekávaná chyba:\nboom\n\nDetail je v záložce Logy / v C:\\L"), "víceřádkový vzor");
    Loc.Set("cs", dir); Eq("Uložit", Loc.T("Uložit"), "zpět na cs");
    // každý viditelný text v MainWindow.xaml musí mít anglický překlad
    var xaml = File.ReadAllText(Path.Combine(dir, "..", "MainWindow.xaml"));
    var en = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "en.json"))).RootElement.GetProperty("strings");
    var missing = new List<string>();
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xaml, "(?:Text|Content|Header|ToolTip)=\"([^\"{]*[A-Za-zÀ-ž]{3}[^\"]*)\""))
    {
        var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        if (!v.StartsWith("Hey, keep") && !en.TryGetProperty(v, out _)) missing.Add(v);
    }
    True(missing.Count == 0, "chybí v en.json: " + string.Join(" | ", missing.Take(5)));
    await Task.CompletedTask;
});
T("Tracker: trvale viditelný text (HUD) se nevrací každých 30 s", async () =>
{
    var clock = DateTime.UtcNow; var t = new SubtitleTracker(2, 3, clock: () => clock); int n = 0;
    for (int i = 0; i < 200; i++) { clock = clock.AddSeconds(1); if (t.Observe("PHOTO studio").Event == TrackerEvent.NewSubtitle) n++; }
    Eq(1, n, "jen jednou");
    await Task.CompletedTask;
});
T("Hedging: pomalý první požadavek předběhne záložní", async () =>
{
    var tr = new FirstSlow();
    var svc = new TranslationService(tr, new TranslationCache()) { HedgeMs = 100 };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = await svc.TranslateAsync("Hello there", default);
    True(sw.ElapsedMilliseconds < 800, $"trvalo {sw.ElapsedMilliseconds} ms");
    Eq(2, tr.Calls, "dva požadavky"); Eq(1L, svc.Stats.Hedged, "hedged");
});
T("LightBounds: jasné prvky scény mimo text se ignorují", async () =>
{
    var f = new Frame(400, 100);
    void Fill(int x0, int x1, int y0, int y1) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int o = (y * 400 + x) * 4; f.Bgra[o] = f.Bgra[o + 1] = f.Bgra[o + 2] = 255; f.Bgra[o + 3] = 255; } }
    Fill(100, 300, 70, 82);          // text (hustý pruh)
    Fill(20, 32, 5, 14);             // jasný objekt scény vlevo nahoře
    Fill(370, 380, 8, 16);           // další vpravo nahoře
    var b = ImagePrep.LightBounds(f, 175);
    True(b != null, "nenalezeno");
    True(b!.Value.Y0 > 0.6 && b.Value.Y1 < 0.9, $"Y {b.Value.Y0}-{b.Value.Y1}");
    True(b.Value.X0 > 0.2 && b.Value.X1 < 0.8, $"X {b.Value.X0}-{b.Value.X1}");
    await Task.CompletedTask;
});
T("OcrStrategy: druhý engine se použije jen při slabém čtení", async () =>
{
    var f = new Frame(60, 20); for (int i = 0; i < 60 * 20; i++) { f.Bgra[i * 4] = f.Bgra[i * 4 + 1] = f.Bgra[i * 4 + 2] = 255; f.Bgra[i * 4 + 3] = 255; }
    var o = new OcrOptions { Mode = OcrMode.Auto, Upscale = 1 };
    int called = 0;
    Task<string> Second(Frame _) { called++; return Task.FromResult("Hello there my friend"); }
    var weak = await OcrStrategy.RunAsync(f, o, _ => Task.FromResult("He11o tbere rny fr1end x7"), Second);
    Eq("tesseract", weak.Method, "slabé → tesseract"); Eq(1, called);
    var good = await OcrStrategy.RunAsync(f, o, _ => Task.FromResult("Hello there my friend"), Second);
    Eq("isolated", good.Method, "dobré → bez tesseractu"); Eq(1, called, "neměl se volat");
    var empty = await OcrStrategy.RunAsync(f, o, _ => Task.FromResult(""), Second);
    Eq(1, called, "prázdné čtení se nezkouší");
});
T("BMP 24: hlavička a velikost", async () =>
{
    var b = ImagePrep.ToBmp24(new Frame(5, 3));
    Eq((byte)'B', b[0]); Eq(54 + 16 * 3, b.Length); Eq(5, BitConverter.ToInt32(b, 18)); Eq(3, BitConverter.ToInt32(b, 22));
    await Task.CompletedTask;
});
T("LightBounds: hranice světlého pruhu", async () =>
{
    var f = new Frame(100, 20);
    for (int y = 5; y < 10; y++) for (int x = 30; x < 70; x++) { int o = (y * 100 + x) * 4; f.Bgra[o] = f.Bgra[o + 1] = f.Bgra[o + 2] = 255; f.Bgra[o + 3] = 255; }
    var b = ImagePrep.LightBounds(f, 175);
    True(b != null, "nenalezeno");
    True(Math.Abs(b!.Value.X0 - 0.30) < 0.03 && Math.Abs(b.Value.X1 - 0.70) < 0.03, $"X {b.Value.X0}-{b.Value.X1}");
    True(ImagePrep.LightBounds(new Frame(100, 20), 175) == null, "prázdný");
    await Task.CompletedTask;
});
Console.WriteLine("== ImagePrep");
Frame Solid(int w, int h, byte b, byte g, byte r) { var f = new Frame(w, h); for (int i = 0; i < w * h; i++) { f.Bgra[i * 4] = b; f.Bgra[i * 4 + 1] = g; f.Bgra[i * 4 + 2] = r; f.Bgra[i * 4 + 3] = 255; } return f; }
T("Crop + Scale rozměry", async () =>
{
    var f = Solid(100, 50, 10, 10, 10);
    var c = FrameCrop.Crop(f, new RegionF { X = 0.5, Y = 0.5, W = 0.5, H = 0.5 });
    Eq(50, c.Width); Eq(25, c.Height);
    var s = ImagePrep.ScaleBilinear(c, 2); Eq(100, s.Width); Eq(50, s.Height);
    await Task.CompletedTask;
});
T("IsolateLight: bílé → černé, tmavé → bílé", async () =>
{
    var f = Solid(4, 4, 255, 255, 255); var o = ImagePrep.IsolateLight(f, 175, 2);
    Eq(8, o.Width);
    Eq((byte)0, o.Bgra[(4 * 8 + 4) * 4], "text");
    Eq((byte)255, o.Bgra[0], "okraj");
    var d = ImagePrep.IsolateLight(Solid(4, 4, 30, 30, 30), 175, 2);
    Eq((byte)255, d.Bgra[(4 * 8 + 4) * 4], "pozadí");
    await Task.CompletedTask;
});
T("LightMaskSignature: žádný text vs. text", async () =>
{
    True(ImagePrep.LightMaskSignature(Solid(64, 16, 20, 20, 20), 175).LightFraction < 0.002, "tma");
    var f = Solid(64, 16, 20, 20, 20);
    for (int y = 4; y < 8; y++) for (int x = 10; x < 40; x++) { int i = (y * 64 + x) * 4; f.Bgra[i] = f.Bgra[i + 1] = f.Bgra[i + 2] = 255; }
    var a = ImagePrep.LightMaskSignature(f, 175); True(a.LightFraction > 0.05, "světlé");
    True(ImagePrep.Distance(a, ImagePrep.LightMaskSignature(f, 175)) < 1e-9, "stejné");
    True(ImagePrep.Distance(a, ImagePrep.LightMaskSignature(Solid(64, 16, 20, 20, 20), 175)) > 0.02, "rozdílné");
    await Task.CompletedTask;
});

Console.WriteLine("== OcrStrategy");
T("Auto: dobrý izolovaný výsledek stačí jedním voláním", async () =>
{
    int calls = 0;
    var r = await OcrStrategy.RunAsync(Solid(40, 10, 255, 255, 255), new OcrOptions(), _ => { calls++; return Task.FromResult("Keep your voice down"); });
    Eq(1, calls); Eq("isolated", r.Method);
});
T("Auto: špatný izolovaný → vybere lepší originál", async () =>
{
    int calls = 0;
    var r = await OcrStrategy.RunAsync(Solid(40, 10, 255, 255, 255), new OcrOptions(), _ =>
    { calls++; return Task.FromResult(calls == 1 ? "Heyjskeepyour&Xz" : "Hey, keep your voice down."); });
    Eq(2, calls); Eq("original", r.Method); Eq("Hey, keep your voice down.", r.Text);
});

Console.WriteLine("== Nastavení");
T("uložení/načtení + Normalize + klíče nejsou v čitelné podobě", async () =>
{
    var p = Path.Combine(Path.GetTempPath(), "gtl_" + Guid.NewGuid().ToString("N") + ".json");
    var s = new AppSettings { IntervalMs = 5, FontSize = 999, Translator = TranslatorKind.DeepL, DeepLKeyProtected = new PlainProtector().Protect("abc:fx") };
    s.Region = new RegionF { X = 0.9, Y = 0.9, W = 0.9, H = 0.9 };
    s.Save(p);
    var l = AppSettings.Load(p);
    Eq(100, l.IntervalMs); Eq(120.0, l.FontSize); Eq(TranslatorKind.DeepL, l.Translator);
    True(l.Region.X + l.Region.W <= 1.0001, "region v mezích");
    var raw = File.ReadAllText(p); True(!raw.Contains("abc:fx"), "klíč nesmí být čitelně v JSON");
    File.Delete(p);
    await Task.CompletedTask;
});
T("poškozený soubor → výchozí", async () =>
{
    var p = Path.Combine(Path.GetTempPath(), "gtl_" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(p, "{ rozbité"); var l = AppSettings.Load(p); Eq(200, l.IntervalMs); File.Delete(p);
    await Task.CompletedTask;
});

Console.WriteLine("== Překladače (falešné HTTP)");
T("Google unofficial: URL a parsování více segmentů", async () =>
{
    var h = H(_ => (HttpStatusCode.OK, "[[[\"Ahoj, \",\"Hello, \",null,null,1],[\"jak se máš?\",\"how are you?\",null,null,1]],null,\"en\"]"));
    var t = new GoogleUnofficialTranslator(new HttpClient(h));
    Eq("Ahoj, jak se máš?", await t.TranslateAsync("Hello, how are you?", null, default));
    var u = h.Requests[0].RequestUri!.AbsoluteUri;
    True(u.Contains("sl=en") && u.Contains("tl=cs") && u.Contains("client=gtx") && u.Contains("q=Hello%2C%20how"), u);
});
T("Google unofficial: 429 → RateLimit, špatný JSON → BadResponse", async () =>
{
    var t = new GoogleUnofficialTranslator(new HttpClient(H(_ => ((HttpStatusCode)429, "x"))));
    try { await t.TranslateAsync("a b c", null, default); throw new Exception("mělo vyhodit"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.RateLimit, e.Kind); }
    var t2 = new GoogleUnofficialTranslator(new HttpClient(H(_ => (HttpStatusCode.OK, "<html>captcha</html>"))));
    try { await t2.TranslateAsync("a b c", null, default); throw new Exception("mělo vyhodit"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.BadResponse, e.Kind); }
});
T("Google Cloud: hlavička klíče, tělo a HTML dekódování", async () =>
{
    var h = H(_ => (HttpStatusCode.OK, "{\"data\":{\"translations\":[{\"translatedText\":\"Nech to b&#39;tt\"}]}}"));
    var t = new GoogleCloudTranslator(new HttpClient(h), "KEY123");
    var r = await t.TranslateAsync("Leave it", null, default);
    Eq("Nech to b'tt", r);
    Eq("KEY123", h.Requests[0].Headers.GetValues("X-goog-api-key").First());
    True(h.Bodies[0].Contains("\"target\":\"cs\"") && h.Bodies[0].Contains("\"source\":\"en\""), h.Bodies[0]);
});
T("DeepL: endpoint podle klíče, hlavička, tělo, kontext", async () =>
{
    Eq("https://api-free.deepl.com", DeepLTranslator.BaseUrlFor("abc:fx"));
    Eq("https://api.deepl.com", DeepLTranslator.BaseUrlFor("abc"));
    var h = H(_ => (HttpStatusCode.OK, "{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"Ahoj světe\"}]}"));
    var t = new DeepLTranslator(new HttpClient(h), "abc:fx");
    Eq("Ahoj světe", await t.TranslateAsync("Hello world", "Previous line.", default));
    var r = h.Requests[0];
    Eq("https://api-free.deepl.com/v2/translate", r.RequestUri!.ToString());
    Eq("DeepL-Auth-Key abc:fx", r.Headers.Authorization!.ToString());
    True(h.Bodies[0].Contains("\"target_lang\":\"CS\"") && h.Bodies[0].Contains("\"source_lang\":\"EN\"") && h.Bodies[0].Contains("\"context\":\"Previous line.\""), h.Bodies[0]);
});
T("DeepL: mapování chyb 403/456/429/500", async () =>
{
    foreach (var (code, kind) in new[] { (403, TranslationErrorKind.Auth), (456, TranslationErrorKind.Quota), (429, TranslationErrorKind.RateLimit), (500, TranslationErrorKind.Server) })
    {
        var t = new DeepLTranslator(new HttpClient(H(_ => ((HttpStatusCode)code, "err"))), "k");
        try { await t.TranslateAsync("a b c", null, default); throw new Exception("mělo vyhodit " + code); }
        catch (TranslationException e) { Eq(kind, e.Kind, "HTTP " + code); }
    }
});
T("DeepL: test spojení čte /v2/usage", async () =>
{
    var h = H(_ => (HttpStatusCode.OK, "{\"character_count\":1234,\"character_limit\":500000}"));
    var r = await new DeepLTranslator(new HttpClient(h), "abc:fx").TestAsync(default);
    True(r.Ok && r.CharsUsed == 1234 && r.CharsLimit == 500000, r.Message);
    True(h.Requests[0].RequestUri!.AbsolutePath == "/v2/usage", "usage");
    var bad = await new DeepLTranslator(new HttpClient(H(_ => (HttpStatusCode.Forbidden, "no"))), "x").TestAsync(default);
    True(!bad.Ok, "403 nesmí být OK");
});
T("Síťová chyba → Network", async () =>
{
    var h = new ThrowHandler();
    try { await new GoogleUnofficialTranslator(new HttpClient(h)).TranslateAsync("a b c", null, default); throw new Exception("mělo vyhodit"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.Network, e.Kind); }
});
T("MissingKeyTranslator nikdy tiše nepřepne", async () =>
{
    try { await new MissingKeyTranslator("DeepL").TranslateAsync("a", null, default); throw new Exception("mělo vyhodit"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.NotConfigured, e.Kind); }
});

Console.WriteLine("== Cache + služba");
T("cache LRU + persistence", async () =>
{
    var c = new TranslationCache(2);
    c.Put("a", "1"); c.Put("b", "2"); c.TryGet("a", out _); c.Put("c", "3");
    True(c.TryGet("a", out _) && !c.TryGet("b", out _) && c.TryGet("c", out _), "LRU");
    var p = Path.Combine(Path.GetTempPath(), "gtc_" + Guid.NewGuid().ToString("N") + ".json");
    c.Save(p); var c2 = new TranslationCache(10); c2.Load(p); True(c2.TryGet("c", out var v) && v == "3", "load"); File.Delete(p);
    await Task.CompletedTask;
});
T("služba: cache hit nevolá síť, kontext se posílá DeepL", async () =>
{
    var h = H(_ => (HttpStatusCode.OK, "{\"translations\":[{\"text\":\"Ahoj\"}]}"));
    var svc = new TranslationService(new DeepLTranslator(new HttpClient(h), "k:fx"), new TranslationCache()) { UseContext = true, ContextLines = 2 };
    var r1 = await svc.TranslateAsync("Hello there", default);
    var r2 = await svc.TranslateAsync("Hello  THERE", default);
    True(!r1.FromCache && r2.FromCache, "druhé z cache"); Eq(1, h.Requests.Count, "počet HTTP");
    await svc.TranslateAsync("Second sentence here", default);
    True(h.Bodies[1].Contains("\"context\":\"Hello there\""), h.Bodies[1]);
});
T("služba počítá chyby a chybu vrací", async () =>
{
    var svc = new TranslationService(new DeepLTranslator(new HttpClient(H(_ => (HttpStatusCode.Forbidden, "x"))), "k"), new TranslationCache());
    try { await svc.TranslateAsync("a b c", default); throw new Exception("mělo vyhodit"); } catch (TranslationException) { }
    Eq(1L, svc.Stats.Errors); True(svc.Stats.LastError != null, "LastError");
});

Console.WriteLine("== Pipeline");
T("pipeline: ručně tikaná sekvence", async () =>
{
    var src = new ScriptedSource(); var ocr = new ScriptedOcr(new[] { "First subtitle line", "First subtitle line", "Second one now", "Second one now" });
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
    var s = new AppSettings { StableReads = 2 };
    var pl = new TranslationPipeline(ocr, svc, () => s);
    var got = new List<string>(); var tcs = new TaskCompletionSource();
    pl.Translated += r => { lock (got) { got.Add(r.English + "→" + r.Czech); if (got.Count == 2) tcs.TrySetResult(); } };
    var q = pl.BeginManual(src);
    for (int i = 0; i < 6; i++) { src.NextVariant(i); await pl.TickAsync(s, default); }
    var emitted = new List<string>(); while (q.TryRead(out var e)) emitted.Add(e);
    Eq("First subtitle line|Second one now", string.Join("|", emitted));
    True(pl.Stats.Skipped >= 1, "alespoň jeden přeskok bez změny");
});

Console.WriteLine("== Etapa 1: jazyky + slovník");
T("Languages: DeepL kódy a značka páru", async () =>
{
    Eq("EN-GB", Languages.Find("en").DeepL!); Eq("PT-BR", Languages.Find("pt").DeepL!);
    Eq("", Languages.PairTag("en", "cs")); Eq("en>de", Languages.PairTag("en", "de"));
    Eq("PT", Languages.Find("pt").DeepLSource); Eq("cs", Languages.Find("xx").Code);
    await Task.CompletedTask;
});
T("Glossary: ochrana jmen a návrat", async () =>
{
    var g = Glossary.Parse("Vice City\n# komentář\njackpot = výhra");
    var p = g.Protect("Welcome to Vice City, win the jackpot!");
    True(!p.Text.Contains("Vice") && !p.Text.Contains("jackpot"), p.Text);
    var back = Glossary.Restore(p.Text.Replace("Welcome to", "Vítej v"), p);
    Eq("Vítej v Vice City, win the výhra!", back!);
    True(Glossary.Restore("nic", p) == null, "zničená značka");
    await Task.CompletedTask;
});
T("Služba: slovník + jazyk v klíči cache", async () =>
{
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache()) { HedgeMs = 0, Glossary = Glossary.Parse("Lucia") };
    var r = await svc.TranslateAsync("Hi Lucia", CancellationToken.None);
    Eq("CS:Hi Lucia", r.Text);
    svc.LangTag = "en>de";
    var r2 = await svc.TranslateAsync("Hi Lucia", CancellationToken.None);
    True(!r2.FromCache, "jiný jazyk = jiný klíč");
});
T("DeepL: nepodporovaný jazyk = srozumitelná chyba", async () =>
{
    var d = new DeepLTranslator(new HttpClient(H(_ => (HttpStatusCode.OK, "{}"))), "k:fx") { TargetLang = Languages.Find("hi") };
    try { await d.TranslateAsync("x", null, CancellationToken.None); throw new Exception("mělo selhat"); }
    catch (TranslationException e) { True(e.Message.Contains("nepodporuje"), e.Message); }
});

T("SubtitleHistory + ProfileStore + diagnostika", async () =>
{
    var h = new SubtitleHistory(2); h.Add("a", "A"); h.Add("b", "B"); h.Add("c", "C");
    Eq(2, h.Snapshot().Count); h.Add("d", "D", replaceLast: true); Eq("D", h.Snapshot()[^1].Translation);
    var dir = Path.Combine(Path.GetTempPath(), "ovl" + Guid.NewGuid().ToString("N"));
    var ps = new ProfileStore(dir);
    var s = new AppSettings { FontSize = 33, DeepLKeyProtected = "SECRET" }; 
    ps.Save("Hra / test", s);
    Eq(1, ps.List().Count);
    var cur = new AppSettings { DeepLKeyProtected = "MOJE" };
    var l = ps.Load(ps.List()[0], cur);
    Eq(33.0, (double)l.FontSize); Eq("MOJE", l.DeepLKeyProtected);
    var zip = Path.Combine(dir, "d.zip");
    DiagnosticsBundle.Create(zip, s, dir, dir);
    using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
    {
        var e = z.GetEntry("settings.json")!; using var r = new StreamReader(e.Open());
        var txt = r.ReadToEnd(); True(!txt.Contains("SECRET") && txt.Contains("skryto"), "klíč musí být skrytý");
    }
    await Task.CompletedTask;
});
T("Migrace nastavení ze staré složky", async () =>
{
    var a = Path.Combine(Path.GetTempPath(), "old" + Guid.NewGuid().ToString("N")); var b = a + "_new";
    Directory.CreateDirectory(a); File.WriteAllText(Path.Combine(a, "settings.json"), "{}");
    AppSettings.MigrateOld(a, b);
    True(File.Exists(Path.Combine(b, "settings.json")), "zkopírováno");
    await Task.CompletedTask;
});
T("OBS server: stránka a /state", async () =>
{
    using var srv = new OverlayWebServer(); srv.Start(0);
    using var http = new HttpClient();
    var page = await http.GetStringAsync(srv.Url); True(page.Contains("Overlingo"), "stránka");
    srv.Update("Hello", "Ahoj", true);
    var st = await http.GetStringAsync(srv.Url + "state");
    True(st.Contains("\"translation\":\"Ahoj\"") && st.Contains("\"visible\":true"), st);
    srv.Clear();
    True((await http.GetStringAsync(srv.Url + "state")).Contains("\"visible\":false"), "skryto");
    var r = await http.GetAsync(srv.Url + "nic"); Eq(404, (int)r.StatusCode);
});

Console.WriteLine("== Etapa 2: AI překladače");
T("Gemini: požadavek a parsování", async () =>
{
    string? url = null, hdr = null, body = null;
    var h = H(r => { url = r.RequestUri!.ToString(); hdr = r.Headers.GetValues("x-goog-api-key").First(); body = r.Content!.ReadAsStringAsync().Result;
        return (HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\\\"Ahoj světe\\\"\"}]}}]}"); });
    var g = new GeminiTranslator(new HttpClient(h), "KEY") { GameContext = "GTA" };
    var r = await g.TranslateAsync("Hello world", "Prev line", CancellationToken.None);
    Eq("Ahoj světe", r); Eq("KEY", hdr!);
    True(url!.Contains("gemini-3.8-flash:generateContent"), url!);
    True(body!.Contains("GTA") && body.Contains("Prev line") && body.Contains("Czech"), body!);
});
T("OpenAI-kompatibilní: Bearer, model, chyby", async () =>
{
    string? auth = null, body = null;
    var h = H(r => { auth = r.Headers.Authorization?.ToString(); body = r.Content!.ReadAsStringAsync().Result;
        return (HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"Překlad: Ahoj\"}}]}"); });
    var t = new OpenAiCompatibleTranslator(new HttpClient(h), "sk-x") { Model = "llama3", BaseUrl = "http://localhost:1234/v1/" };
    Eq("Ahoj", await t.TranslateAsync("Hi", null, CancellationToken.None));
    Eq("Bearer sk-x", auth!); True(body!.Contains("\"model\":\"llama3\""), body!);
    var nomodel = new OpenAiCompatibleTranslator(new HttpClient(h), "") ;
    try { await nomodel.TranslateAsync("Hi", null, CancellationToken.None); throw new Exception("mělo selhat"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.NotConfigured, e.Kind); }
    var bad = new OpenAiCompatibleTranslator(new HttpClient(H(_ => (HttpStatusCode.Unauthorized, "no"))), "") { Model = "m" };
    try { await bad.TranslateAsync("Hi", null, CancellationToken.None); throw new Exception("mělo selhat"); }
    catch (TranslationException e) { Eq(TranslationErrorKind.Auth, e.Kind); }
});
T("AiPrompt.Clean", async () =>
{
    Eq("Ahoj", AiPrompt.Clean("  \"Ahoj\" ")); Eq("Ahoj", AiPrompt.Clean("Translation: Ahoj"));
    await Task.CompletedTask;
});

Console.WriteLine("== Fáze 2");
T("HotkeyParser: parse/format/odmítnutí", async () =>
{
    True(HotkeyParser.TryParse("F8", out var m, out var vk) && m == 0 && vk == 0x77, "F8");
    True(HotkeyParser.TryParse("Ctrl+Shift+F12", out m, out vk) && m == (HotkeyParser.Ctrl | HotkeyParser.Shift) && vk == 0x7B, "ctrl+shift+f12");
    True(HotkeyParser.TryParse("alt+k", out m, out vk) && vk == 'K', "alt+k");
    True(!HotkeyParser.TryParse("Ctrl+", out _, out _), "bez klávesy");
    True(!HotkeyParser.TryParse("F8+F9", out _, out _), "dvě klávesy");
    True(!HotkeyParser.TryParse("blbost", out _, out _), "neznámá");
    Eq("Ctrl+Alt+F9", HotkeyParser.Format(HotkeyParser.Ctrl | HotkeyParser.Alt, 0x78));
    await Task.CompletedTask;
});
T("Nastavení: neplatná zkratka se vrátí na výchozí, meze", async () =>
{
    var s = new AppSettings { HkToggle = "xx++", RetryCount = 99, CacheMax = 1 }; s.Normalize();
    Eq("F8", s.HkToggle); Eq(3, s.RetryCount); Eq(100, s.CacheMax);
    await Task.CompletedTask;
});
T("StripJunk odstraní „oo:c“ a symboly, nechá ceny a slova", async () =>
{
    Eq("Don", TextTools.StripJunk("Don oo:c"));
    Eq("Okay, so I just grabbed some stuff", TextTools.StripJunk("Okay, so I just grabbed some stuff @ oo:c"));
    Eq("$1,124 Can you come?", TextTools.StripJunk("$1,124 Can you come?"));
    await Task.CompletedTask;
});
T("ContainsIgnored", async () =>
{
    True(TextTools.ContainsIgnored("Unknown 00:11", "unknown; camera"), "unknown");
    True(!TextTools.ContainsIgnored("Hello", "unknown;"), "nic");
    True(!TextTools.ContainsIgnored("Hello", ""), "prázdné");
    await Task.CompletedTask;
});
T("Adaptivní interval: zdvojení po klidu, vypínatelné", async () =>
{
    var s = new AppSettings { IntervalMs = 300, AdaptiveInterval = true };
    Eq(300, TranslationPipeline.EffectiveInterval(s, 3)); Eq(300, TranslationPipeline.EffectiveInterval(s, 10));
    s.IntervalMs = 150; Eq(150, TranslationPipeline.EffectiveInterval(s, 3)); Eq(300, TranslationPipeline.EffectiveInterval(s, 10)); s.IntervalMs = 300;
    s.AdaptiveInterval = false; Eq(300, TranslationPipeline.EffectiveInterval(s, 50));
    s.AdaptiveInterval = true; s.IntervalMs = 2000; Eq(2000, TranslationPipeline.EffectiveInterval(s, 50));
    await Task.CompletedTask;
});
T("Retry: síťová chyba se zopakuje, chyba klíče ne", async () =>
{
    int calls = 0;
    var h = H(_ => { calls++; return calls == 1 ? (HttpStatusCode.BadGateway, "x") : (HttpStatusCode.OK, "{\"translations\":[{\"text\":\"Ahoj\"}]}"); });
    var svc = new TranslationService(new DeepLTranslator(new HttpClient(h), "k:fx"), new TranslationCache()) { RetryCount = 1, DelayFn = _ => Task.CompletedTask };
    Eq("Ahoj", (await svc.TranslateAsync("Hello there", default)).Text); Eq(2, calls, "volání");
    int c2 = 0;
    var svc2 = new TranslationService(new DeepLTranslator(new HttpClient(H(_ => { c2++; return (HttpStatusCode.Forbidden, "no"); })), "k"), new TranslationCache()) { RetryCount = 3, DelayFn = _ => Task.CompletedTask };
    try { await svc2.TranslateAsync("a b c", default); throw new Exception("mělo vyhodit"); } catch (TranslationException) { }
    Eq(1, c2, "403 se neopakuje");
});
T("DeepL dávka: jeden požadavek, pořadí zachováno, počet se kontroluje", async () =>
{
    var h = H(_ => (HttpStatusCode.OK, "{\"translations\":[{\"text\":\"Jedna\"},{\"text\":\"Dva\"}]}"));
    var t = new DeepLTranslator(new HttpClient(h), "k:fx");
    var r = await t.TranslateManyAsync(new[] { "One", "Two" }, null, default);
    Eq("Jedna|Dva", string.Join("|", r)); Eq(1, h.Requests.Count);
    True(h.Bodies[0].Contains("\"text\":[\"One\",\"Two\"]"), h.Bodies[0]);
    var bad = new DeepLTranslator(new HttpClient(H(_ => (HttpStatusCode.OK, "{\"translations\":[{\"text\":\"x\"}]}"))), "k");
    try { await bad.TranslateManyAsync(new[] { "a b", "c d" }, null, default); throw new Exception("mělo vyhodit"); } catch (TranslationException e) { Eq(TranslationErrorKind.BadResponse, e.Kind); }
});
T("Služba: dávka přeloží jen necachované, výchozí TranslateMany u Google", async () =>
{
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
    await svc.TranslateAsync("Known line", default);
    var r = await svc.TranslateManyAsync(new[] { "Known line", "New line" }, default);
    Eq("CS:Known line", r[0].Text); True(r[0].FromCache && !r[1].FromCache, "cache"); Eq("CS:New line", r[1].Text);
});
T("Dedup překrývajících se řádků nechá delší, odlišné nechá obě", async () =>
{
    var r = ScreenTranslationPipeline.DedupOverlapping(new List<OcrLine>
    {
        new("Right. Andy. The one with the clothes.", 0.3, 0.9, 0.4, 0.03),
        new("Right. Andy. The one", 0.3, 0.9, 0.2, 0.025),
        new("Press X to continue", 0.4, 0.5, 0.2, 0.03)
    });
    Eq(2, r.Count); True(r.Any(x => x.Text.StartsWith("Right. Andy. The one with")), "delší zůstal");
    await Task.CompletedTask;
});
T("Celá obrazovka: druhý průchod oblastí titulků najde text, který čtení celého snímku míjí, se správnou polohou", async () =>
{
    var src = new BlockSource();
    var ocr = new ZoneOcr();
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
    var s = new AppSettings { StableReads = 1, AutoGameArea = false, Region = new RegionF { X = 0.25, Y = 0.5, W = 0.5, H = 0.25 }, Upscale = 1 };
    var pl = new ScreenTranslationPipeline(ocr, svc, () => s);
    var updates = new List<IReadOnlyList<ScreenLine>>(); pl.Updated += u => { lock (updates) updates.Add(u); };
    pl.BeginManual(src);
    await pl.TickAsync(s, default); if (pl.PendingTranslation != null) await pl.PendingTranslation;
    lock (updates)
    {
        var l = updates.Last().FirstOrDefault(x => x.English.StartsWith("Zone line"));
        True(l != null, "řádek z oblasti titulků chybí");
        // v ořezu (200x100 px po paddingu 12) je řádek na px 12+20, 12+10 → v celém snímku 800x400: x=200+20, y=200+10
        True(Math.Abs(l!.X - 220.0 / 800) < 1e-6 && Math.Abs(l.Y - 210.0 / 400) < 1e-6, $"poloha {l.X},{l.Y}");
    }
});
T("Pipeline celé obrazovky: stabilizace, překlad dávkou, zmizení řádků", async () =>
{
    var src = new ScriptedSource();
    var ocr = new ScriptedScreenOcr(new[]
    {
        new[] { new OcrLine("Open the map now", 0.1, 0.1, 0.2, 0.03), new OcrLine("Press X to continue", 0.4, 0.8, 0.3, 0.03), new OcrLine("|| ~", 0.5, 0.5, 0.1, 0.02) },
        new[] { new OcrLine("Open the map now", 0.1, 0.1, 0.2, 0.03), new OcrLine("Press X to continue", 0.4, 0.8, 0.3, 0.03) },
        new[] { new OcrLine("Open the map now", 0.1, 0.1, 0.2, 0.03) },
        new[] { new OcrLine("Open the map now", 0.1, 0.1, 0.2, 0.03) },
        new[] { new OcrLine("Open the map now", 0.1, 0.1, 0.2, 0.03) },
    });
    var svc = new TranslationService(new EchoTranslator(), new TranslationCache());
    var s = new AppSettings { StableReads = 2 };
    var pl = new ScreenTranslationPipeline(ocr, svc, () => s);
    var updates = new List<IReadOnlyList<ScreenLine>>(); pl.Updated += u => { lock (updates) updates.Add(u); };
    pl.BeginManual(src);
    for (int i = 0; i < 5; i++)
    {
        src.NextVariant(i % 2 == 0 ? 0 : 3);
        await pl.TickAsync(s, default);
        if (pl.PendingTranslation != null) await pl.PendingTranslation;
    }
    lock (updates)
    {
        True(updates.Count >= 2, "aktualizací: " + updates.Count);
        var two = updates.First(u => u.Count == 2);
        True(two.Any(l => l.Czech == "CS:Open the map now" && Math.Abs(l.X - 0.1) < 1e-9), "řádek 1 s polohou");
        True(two.All(l => !l.English.Contains("||")), "smetí odfiltrováno");
        Eq(1, updates[^1].Count, "po zmizení druhého řádku zbyde jeden");
    }
});

Console.WriteLine($"\nVýsledek: {pass} OK, {fail} selhalo");
return fail == 0 ? 0 : 1;

sealed class ThrowHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new HttpRequestException("no route");
}
sealed class FirstSlow : ITranslator
{
    public int Calls;
    public string Name => "firstslow";
    public async Task<string> TranslateAsync(string t, string? c, CancellationToken ct) { int n = Interlocked.Increment(ref Calls); await Task.Delay(n == 1 ? 2000 : 20, ct); return "CS:" + t; }
    public Task<ConnectionTestResult> TestAsync(CancellationToken ct) => Task.FromResult(new ConnectionTestResult(true, "ok"));
}
sealed class SlowEcho : ITranslator
{
    readonly int _ms; public SlowEcho(int ms) { _ms = ms; }
    public string Name => "slow";
    public async Task<string> TranslateAsync(string t, string? c, CancellationToken ct) { await Task.Delay(_ms, ct); return "CS:" + t; }
    public Task<ConnectionTestResult> TestAsync(CancellationToken ct) => Task.FromResult(new ConnectionTestResult(true, "ok"));
}
sealed class EchoTranslator : ITranslator
{
    public string Name => "echo";
    public Task<string> TranslateAsync(string t, string? c, CancellationToken ct) => Task.FromResult("CS:" + t);
    public Task<ConnectionTestResult> TestAsync(CancellationToken ct) => Task.FromResult(new ConnectionTestResult(true, "ok"));
}
sealed class BoundsOcr : IOcrEngine, IOcrBounds
{
    public int MaxDimension => 4000;
    public RegionF? LastBounds => new RegionF { X = 0.3, Y = 0.2, W = 0.4, H = 0.5 };
    public Task<string> RecognizeAsync(Frame f) => Task.FromResult("This is a long enough subtitle line");
}
sealed class ScriptedOcr : IOcrEngine
{
    readonly string[] _t; int _i;
    public ScriptedOcr(string[] t) { _t = t; }
    public int MaxDimension => 4000;
    public Task<string> RecognizeAsync(Frame f) => Task.FromResult(_t[Math.Min(_i++, _t.Length - 1)]);
}
/// <summary>Zdroj, který vrací světlý pruh a při změně varianty změní pozici (aby se změna detekovala).</summary>
sealed class ScriptedSource : ICaptureSource
{
    int _variant = -1;
    public string Description => "scripted"; public double Fps => 60; public string? LastError => null; public DateTime LastFrameUtc => DateTime.UtcNow;
    public Task StartAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void NextVariant(int i) { _variant = i; }
    public Task<Frame?> GrabAsync(RegionF r)
    {
        var f = new Frame(128, 32);
        // pruh se mění: i<3 → levá část, i>=3 → pravá část
        int x0 = _variant < 3 ? 10 : 70;
        for (int y = 8; y < 16; y++) for (int x = x0; x < x0 + 40; x++) { int o = (y * 128 + x) * 4; f.Bgra[o] = f.Bgra[o + 1] = f.Bgra[o + 2] = 255; f.Bgra[o + 3] = 255; }
        return Task.FromResult<Frame?>(f);
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, (HttpStatusCode, string)> Fn = _ => (HttpStatusCode.OK, "{}");
    public List<HttpRequestMessage> Requests = new();
    public List<string> Bodies = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Requests.Add(r); Bodies.Add(r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct));
        var (c, b) = Fn(r);
        return new HttpResponseMessage(c) { Content = new StringContent(b, Encoding.UTF8, "application/json") };
    }
}

sealed class ScriptedScreenOcr : IScreenOcr
{
    readonly OcrLine[][] _calls; int _i;
    public ScriptedScreenOcr(OcrLine[][] calls) { _calls = calls; }
    public int MaxDimension => 4000;
    public Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Frame f) => Task.FromResult<IReadOnlyList<OcrLine>>(_calls[Math.Min(_i++, _calls.Length - 1)]);
}

sealed class BlockSource : ICaptureSource
{
    public string Description => "block"; public double Fps => 60; public string? LastError => null; public DateTime LastFrameUtc => DateTime.UtcNow;
    public Task StartAsync() => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<Frame?> GrabAsync(RegionF r)
    {
        var f = new Frame(800, 400);
        for (int y = 210; y < 230; y++) for (int x = 220; x < 300; x++) { int o = (y * 800 + x) * 4; f.Bgra[o] = f.Bgra[o + 1] = f.Bgra[o + 2] = 255; f.Bgra[o + 3] = 255; }
        return Task.FromResult<Frame?>(f);
    }
}

/// <summary>Celý snímek (800x400) nic nenajde; izolovaný ořez (200x100 + padding 12) vrátí řádek na px (32,22).</summary>
sealed class ZoneOcr : IScreenOcr
{
    public int MaxDimension => 4000;
    public Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Frame f)
    {
        if (f.Width == 800) return Task.FromResult<IReadOnlyList<OcrLine>>(Array.Empty<OcrLine>());
        return Task.FromResult<IReadOnlyList<OcrLine>>(new[] { new OcrLine("Zone line of subtitle", 32.0 / f.Width, 22.0 / f.Height, 80.0 / f.Width, 20.0 / f.Height) });
    }
}
