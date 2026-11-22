namespace Overlingo.Core;

/// <summary>Pojmenované profily nastavení (soubory *.json ve složce profiles). Šifrované klíče se do profilu nekopírují.</summary>
public sealed class ProfileStore
{
    readonly string _dir;
    public ProfileStore(string? dir = null) { _dir = dir ?? Path.Combine(AppSettings.DefaultDir, "profiles"); }

    static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
    string PathOf(string name) => Path.Combine(_dir, Safe(name) + ".json");

    public IReadOnlyList<string> List() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList()! : new List<string>();

    public void Save(string name, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(Safe(name))) throw new ArgumentException("Prázdný název profilu.");
        Directory.CreateDirectory(_dir);
        var copy = AppSettings.FromJson(s.ToJson());
        copy.GoogleCloudKeyProtected = ""; copy.DeepLKeyProtected = ""; copy.GeminiKeyProtected = ""; copy.AiKeyProtected = "";
        File.WriteAllText(PathOf(name), copy.ToJson());
    }

    /// <summary>Načte profil; klíče a umístění okna převezme z aktuálního nastavení.</summary>
    public AppSettings Load(string name, AppSettings current)
    {
        var p = AppSettings.FromJson(File.ReadAllText(PathOf(name)));
        p.GoogleCloudKeyProtected = current.GoogleCloudKeyProtected;
        p.DeepLKeyProtected = current.DeepLKeyProtected;
        p.GeminiKeyProtected = current.GeminiKeyProtected; p.AiKeyProtected = current.AiKeyProtected;
        return p;
    }

    public void Delete(string name) { var f = PathOf(name); if (File.Exists(f)) File.Delete(f); }
}
