using System.Diagnostics;
using System.IO;
using Overlingo.Core;

namespace Overlingo.App.Capture;

/// <summary>
/// Volitelný druhý OCR engine: spouští nainstalovaný tesseract.exe (např. UB-Mannheim build). Žádný NuGet balíček,
/// takže když Tesseract není nainstalovaný, aplikace funguje beze změny. Volá se jen při slabém výsledku Windows OCR.
/// </summary>
internal sealed class TesseractOcr
{
    public string Path { get; }
    TesseractOcr(string path) { Path = path; }

    public static TesseractOcr? TryCreate(string configured)
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) cands.Add(configured.Trim().Trim('"'));
        cands.Add(@"C:\Program Files\Tesseract-OCR\tesseract.exe");
        cands.Add(@"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe");
        cands.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Tesseract-OCR\tesseract.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { cands.Add(System.IO.Path.Combine(dir.Trim().Trim('"'), "tesseract.exe")); } catch { }
        foreach (var c in cands)
            try { if (File.Exists(c)) return new TesseractOcr(c); } catch { }
        return null;
    }

    public async Task<string> RecognizeAsync(Frame frame)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gtalt_{Guid.NewGuid():N}.bmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, ImagePrep.ToBmp24(frame)).ConfigureAwait(false);
            var psi = new ProcessStartInfo(Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in new[] { tmp, "stdout", "-l", "eng", "--psm", "6" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("tesseract.exe se nespustil.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return ""; }
            return (await outTask.ConfigureAwait(false)).Replace('\n', ' ').Replace('\r', ' ');
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
