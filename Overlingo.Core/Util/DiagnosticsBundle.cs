using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Overlingo.Core;

/// <summary>Zip pro hlášení chyb: logy, nastavení bez klíčů, poslední diagnostické reporty.</summary>
public static class DiagnosticsBundle
{
    public static string RedactSettingsJson(AppSettings s)
    {
        var c = AppSettings.FromJson(s.ToJson());
        c.GoogleCloudKeyProtected = c.GoogleCloudKeyProtected.Length > 0 ? "<skryto>" : "";
        c.DeepLKeyProtected = c.DeepLKeyProtected.Length > 0 ? "<skryto>" : "";
        c.GeminiKeyProtected = c.GeminiKeyProtected.Length > 0 ? "<skryto>" : "";
        c.AiKeyProtected = c.AiKeyProtected.Length > 0 ? "<skryto>" : "";
        return c.ToJson();
    }

    public static string Create(string zipPath, AppSettings s, string logDir, string diagDir, string? extraInfo = null)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var z = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string name, string text) { var e = z.CreateEntry(name); using var w = new StreamWriter(e.Open()); w.Write(text); }
        Add("settings.json", RedactSettingsJson(s));
        Add("info.txt", $"Overlingo diagnostika {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nOS: {Environment.OSVersion}\n{extraInfo}");
        void AddLatest(string dir, string pattern, int n, string prefix)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern).OrderByDescending(File.GetLastWriteTimeUtc).Take(n))
            {
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs);
                    Add(prefix + Path.GetFileName(f), r.ReadToEnd());
                }
                catch (Exception e) { AppLog.Warn($"Do diagnostiky nelze přidat {f}: {e.Message}"); }
            }
        }
        AddLatest(logDir, "*.log", 3, "logs/");
        AddLatest(diagDir, "*.txt", 3, "diagnostika/");
        return zipPath;
    }
}
