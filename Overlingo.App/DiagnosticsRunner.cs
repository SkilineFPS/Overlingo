using System.IO;
using System.Runtime.InteropServices;
using Overlingo.App.Capture;
using Overlingo.App.Win;
using Overlingo.Core;
using Windows.Graphics.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Ocr;

namespace Overlingo.App;

/// <summary>Záložka Diagnostika: ověří řetěz zdroj → OCR → překlad → overlay → zkratky a řekne, co je v pořádku a co ne.</summary>
internal static class DiagnosticsRunner
{
    public static async Task<List<string>> RunAsync(AppController c, HotkeyManager? hot, Action<string>? progress = null)
    {
        var o = new List<string>();
        void Ok(string s) => o.Add("[ OK ]  " + s);
        void Warn(string s) => o.Add("[POZOR] " + s);
        void Fail(string s) => o.Add("[CHYBA] " + s);
        void Info(string s) => o.Add("        " + s);
        void Head(string s) { o.Add(""); o.Add("== " + s); progress?.Invoke(s); }
        var S = c.Settings;

        o.Add($"Overlingo – diagnostika {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        Head("Systém");
        Info($"OS: {RuntimeInformation.OSDescription}  ({RuntimeInformation.OSArchitecture})");
        Info($".NET: {RuntimeInformation.FrameworkDescription}, procesorů: {Environment.ProcessorCount}");
        try { if (GraphicsCaptureSession.IsSupported()) Ok("Windows Graphics Capture je podporováno."); else Fail("Windows Graphics Capture NENÍ podporováno (nutné Windows 10 1903+)."); }
        catch (Exception e) { Fail("WGC test: " + e.Message); }
        var mons = MonitorEnumerator.List();
        foreach (var m in mons) Info(m.ToString());
        Info("Stav oznámení Windows: " + Native.DescribeNotificationState());

        Head("OCR");
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            Info("Jazyky Windows OCR: " + (langs.Count == 0 ? "žádné" : string.Join(", ", langs)));
        }
        catch (Exception e) { Warn("Výpis OCR jazyků: " + e.Message); }
        if (c.OcrAvailable) Ok(c.OcrInfo); else Fail(c.OcrInfo);
        Info(c.TesseractInfo + (S.UseTesseract ? "" : "  [v nastavení VYPNUTO]"));

        Head("Zdroj obrazu");
        Info($"Zvolený zdroj: {S.Source}, režim překladu: {(S.Scope == TranslationScope.FullScreen ? "vše na obrazovce" : "jen konverzace")}");
        if (S.Source == SourceKind.Window)
        {
            var w = SourceFactory.FindWindow(S.WindowMatch);
            if (w == null) Fail($"Okno obsahující „{S.WindowMatch}“ nebylo nalezeno.");
            else
            {
                Ok($"Okno nalezeno: {w.Title} [{w.ProcessName}]");
                foreach (var l in w.DescribeLines()) Info(l);
                if (w.Mode == WindowMode.Minimized) Warn("Okno je minimalizované – WGC z něj nedostane snímky.");
            }
        }
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            Info("Media Foundation zdroje: " + (groups.Count == 0 ? "žádné" : string.Join(" | ", groups.Select(g => g.DisplayName))));
            if (S.Source == SourceKind.Elgato && !groups.Any(g => ElgatoMfSource.IsElgato(g.DisplayName))) Fail("Elgato není mezi zařízeními.");
        }
        catch (Exception e) { Warn("Výpis MF zařízení: " + e.Message); }

        Frame? full = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            full = await c.GrabOnceAsync(RegionF.Full);
            sw.Stop();
            if (full == null) Fail("Ze zdroje nepřišel žádný snímek do 2 s.");
            else
            {
                long sum = 0; int n = 0, nonBlack = 0;
                for (int i = 0; i < full.Bgra.Length; i += 4 * 97) { int l = (full.Bgra[i] + full.Bgra[i + 1] + full.Bgra[i + 2]) / 3; sum += l; n++; if (l > 12) nonBlack++; }
                double avg = n == 0 ? 0 : (double)sum / n; double nb = n == 0 ? 0 : 100.0 * nonBlack / n;
                Ok($"Snímek získán: {full.Width}×{full.Height} za {sw.ElapsedMilliseconds} ms, průměrný jas {avg:0}, ne-černých bodů {nb:0} %.");
                if (nb < 1) Warn("Obraz je (téměř) černý – okno může být chráněné/minimalizované, nebo hra zrovna nezobrazuje obraz.");
            }
        }
        catch (Exception e) { Fail("Záchyt obrazu selhal: " + e.Message); }
        var asrc = c.ActiveSource;
        if (asrc != null) Info($"Běžící zdroj: {asrc.Description}, FPS {asrc.Fps:0.0}{(asrc.LastError != null ? ", chyba: " + asrc.LastError : "")}");

        Head(S.Scope == TranslationScope.FullScreen ? "OCR celého snímku (režim vše na obrazovce)" : "OCR na aktuální oblasti titulků");
        try
        {
            if (full != null && c.OcrAvailable && S.Scope == TranslationScope.FullScreen)
            {
                var (lines, ms) = await c.OcrLinesOnceAsync(full);
                var good = lines.Where(l => TextTools.IsPlausible(TextTools.StripJunk(l.Text), S.MinLetters)).ToList();
                Ok($"OCR celého snímku doběhlo za {ms} ms: {lines.Count} řádků, z toho {good.Count} použitelných.");
                foreach (var l in good.Take(15)) Info($"[{l.X:P0},{l.Y:P0}  {l.W:P0}×{l.H:P0}]  {TextTools.StripJunk(l.Text)}");
                if (good.Count > 15) Info($"… a dalších {good.Count - 15}");
                if (lines.Count > 0 && good.Count == 0) Warn("Řádky nalezeny, ale žádný neprošel filtrem (min. počet písmen / kvalita).");
            }
            else if (full != null && c.OcrAvailable)
            {
                var crop = FrameCrop.Crop(full, S.EffectiveRegion);
                var r = await c.OcrOnceAsync(crop);
                Ok($"OCR doběhlo za {r.Ms} ms (metoda {r.Method}). Rozpoznáno: {(r.Text.Length == 0 ? "(nic – titulek zrovna nemusí být vidět)" : r.Text)}");
                try
                {
                    var tr = await c.TesseractOnceAsync(crop);
                    if (tr is { } tt) Ok($"Tesseract (test na stejném výřezu) doběhl za {tt.Ms} ms. Rozpoznáno: {(tt.Text.Length == 0 ? "(nic)" : tt.Text)}");
                }
                catch (Exception te) { Fail("Tesseract: " + te.Message); }
            }
            else Warn("Přeskočeno (není snímek nebo OCR).");
        }
        catch (Exception e) { Fail("OCR: " + e.Message); }

        Head("Překlad");
        try
        {
            var t = c.BuildTranslatorForTest(S);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var r = await t.TestAsync(cts.Token);
            if (r.Ok) Ok($"{t.Name}: {r.Message}"); else Fail($"{t.Name}: {r.Message}");
        }
        catch (Exception e) { Fail("Test překladu: " + e.Message); }
        var ts = c.Service.Stats;
        Info($"Dosud: požadavků {ts.Requests}, cache zásahů {ts.CacheHits}, chyb {ts.Errors}, odesláno znaků {ts.CharsSent:N0}, cache {c.Cache.Count} vět.");

        Head("Overlay");
        var ov = c.Overlay;
        if (ov.Hwnd != IntPtr.Zero)
        {
            Native.GetWindowRect(ov.Hwnd, out var r);
            long ex = Native.GetLong(ov.Hwnd, Native.GWL_EXSTYLE);
            Info($"Overlay titulků: {r}, topmost: {((ex & Native.WS_EX_TOPMOST) != 0 ? "ano" : "ne")}, click-through: {((ex & Native.WS_EX_TRANSPARENT) != 0 ? "ano" : "ne")}, viditelný: {ov.Visibility}");
            if ((ex & Native.WS_EX_TOPMOST) == 0) Warn("Overlay není topmost – může zmizet pod hrou.");
            bool ex2 = ov.ExcludedFromCaptureNow;
            Info($"Skryt před záchytem obrazovky (OBS ho {(ex2 ? "NEUVIDÍ" : "uvidí")}): {(ex2 ? "ano" : "ne")}");
            if (S.Source == SourceKind.Monitor && !ex2) Fail("Zdroj je celý monitor a overlay není vyloučen ze záchytu – překlad by se četl zpět!");
            else Ok("Overlay neohrožuje OCR (nepřečte se zpět).");
        }

        Head("Zkratky");
        if (hot == null) Warn("Zkratky nejsou inicializovány.");
        else
        {
            if (hot.Failed.Count == 0) Ok("Všechny zkratky jsou zaregistrovány.");
            foreach (var f in hot.Failed) Fail("Nezaregistrováno: " + f);
            foreach (var f in hot.Fallback) Warn("Náhradní zkratka: " + f);
        }

        Head("Výkon");
        var st = c.ActiveStats;
        Info($"CPU aplikace: {c.CpuPercent:0.0} % celkové kapacity, paměť {c.RamMb:0} MB");
        Info($"Cykly {st.Ticks}, OCR {st.OcrRuns}, přeskočeno {st.Skipped} ({(st.Ticks == 0 ? 0 : 100.0 * st.Skipped / st.Ticks):0} %), OCR ø {st.AvgOcrMs:0} ms, poslední cyklus {st.LastTickMs} ms");
        if (c.CpuPercent > 15) Warn("Vysoké zatížení CPU – zvyšte interval OCR nebo zmenšete oblast titulků.");

        Head("Soubory");
        Info("Nastavení: " + AppSettings.DefaultPath);
        Info("Logy: " + AppLog.LogDir);
        return o;
    }
}
