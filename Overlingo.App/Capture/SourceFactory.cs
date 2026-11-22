using Overlingo.App.Win;
using Overlingo.Core;

namespace Overlingo.App.Capture;

internal static class SourceFactory
{
    public static WindowInfo? FindWindow(string match)
    {
        if (string.IsNullOrWhiteSpace(match)) return null;
        int self = Environment.ProcessId;
        return WindowEnumerator.FindCandidates(WindowEnumerator.Enumerate(), match)
            .Where(w => w.Pid != self && !w.Minimized)
            .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height)
            .FirstOrDefault();
    }

    /// <summary>Okna k výběru v seznamu (bez vlastních oken, nezakrytá a s názvem).</summary>
    public static List<WindowInfo> ListWindows()
    {
        int self = Environment.ProcessId;
        return WindowEnumerator.Enumerate().Where(w => w.Pid != self && !w.Cloaked && w.Title.Trim().Length > 0)
            .OrderBy(w => w.ProcessName).ThenBy(w => w.Title).ToList();
    }

    /// <summary>Procesy, které mají viditelné okno (pro zdroj „Program“).</summary>
    public static List<string> ListPrograms() =>
        ListWindows().Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Největší viditelné okno procesu daného jména (po restartu hry se najde nové).</summary>
    public static WindowInfo? FindProgramWindow(string processName)
    {
        var n = (processName ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.Length == 0) return null;
        return ListWindows().Where(w => !w.Minimized && string.Equals(w.ProcessName, n, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height).FirstOrDefault();
    }

    public static ICaptureSource Create(AppSettings s)
    {
        switch (s.Source)
        {
            case SourceKind.Window:
            {
                var w = FindWindow(s.WindowMatch)
                    ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(s.WindowMatch) ? "Není vybráno žádné okno – zvolte ho v záložce Zdroj obrazu." : $"Okno obsahující „{s.WindowMatch}“ nebylo nalezeno (je spuštěná hra a není minimalizovaná?).");
                var h = w.Handle;
                return new WgcSource(() => CaptureItemFactory.ForWindow(h), $"Okno: {w.Title} [{w.ProcessName}]");
            }
            case SourceKind.Program:
            {
                var w = FindProgramWindow(s.ProgramName)
                    ?? throw new InvalidOperationException($"Program „{s.ProgramName}“ nemá viditelné okno (je spuštěný a není minimalizovaný?).");
                var h = w.Handle;
                return new WgcSource(() => CaptureItemFactory.ForWindow(h), $"Program: {w.ProcessName} – {w.Title}");
            }
            case SourceKind.Monitor:
            {
                var mons = MonitorEnumerator.List();
                if (mons.Count == 0) throw new InvalidOperationException("Nenalezen žádný monitor.");
                var m = mons[Math.Clamp(s.MonitorIndex, 0, mons.Count - 1)];
                return new WgcSource(() => CaptureItemFactory.ForMonitor(m.Handle), m.ToString());
            }
            case SourceKind.Elgato: return new ElgatoMfSource(s.CaptureDevice);
            case SourceKind.ImageFile: return new ImageFileSource(s.TestImagePath);
            default: throw new InvalidOperationException("Neznámý zdroj.");
        }
    }
}
