using System.Text.Json;

namespace Overlingo.Core;

/// <summary>Vlastní opravy překladu: původní věta (+ jazykový pár) → váš překlad. Má přednost před překladačem i cache. Ukládá se do corrections.json.</summary>
public sealed class CorrectionStore
{
    readonly string _path; readonly object _lk = new();
    readonly Dictionary<string, string> _map = new();
    public CorrectionStore(string path) { _path = path; Load(); }
    public int Count { get { lock (_lk) return _map.Count; } }

    static string K(string original, string tag) => tag + "\u0001" + TextTools.Key(original);

    public bool TryGet(string original, string tag, out string translation)
    {
        lock (_lk) return _map.TryGetValue(K(original, tag), out translation!);
    }

    public void Set(string original, string tag, string translation)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translation)) return;
        lock (_lk) _map[K(original, tag)] = translation.Trim();
        Save();
    }

    public bool Remove(string original, string tag)
    {
        bool r; lock (_lk) r = _map.Remove(K(original, tag));
        if (r) Save();
        return r;
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (d != null) lock (_lk) foreach (var kv in d) _map[kv.Key] = kv.Value;
        }
        catch (Exception e) { AppLog.Warn($"Opravy překladu se nepodařilo načíst: {e.Message}"); }
    }

    void Save()
    {
        try
        {
            Dictionary<string, string> copy; lock (_lk) copy = new(_map);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, _path, true);
        }
        catch (Exception e) { AppLog.Warn($"Opravy překladu se nepodařilo uložit: {e.Message}"); }
    }
}
