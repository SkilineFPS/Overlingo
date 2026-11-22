namespace Overlingo.Core;

/// <summary>Jednoduché logování: kruhová paměť pro záložku Logy + denní soubor v %AppData%.</summary>
public static class AppLog
{
    static readonly object Lk = new();
    static readonly List<string> Ring = new();
    public static bool FileLogging { get; set; } = true;
    public static event Action<string>? LineAdded;

    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overlingo", "logs");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        try { msg = Loc.T(msg); } catch { }
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Lk)
        {
            Ring.Add(line);
            if (Ring.Count > 3000) Ring.RemoveRange(0, 500);
            if (FileLogging)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                }
                catch { /* logování nesmí shodit aplikaci */ }
            }
        }
        try { LineAdded?.Invoke(line); } catch { }
    }

    public static string[] Snapshot() { lock (Lk) return Ring.ToArray(); }
    public static void Clear() { lock (Lk) Ring.Clear(); }
}
