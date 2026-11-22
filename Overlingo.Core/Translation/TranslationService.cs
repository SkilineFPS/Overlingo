using System.Diagnostics;
using System.Text.Json;

namespace Overlingo.Core;

public sealed class TranslationCache
{
    readonly int _max; readonly object _lk = new();
    readonly Dictionary<string, LinkedListNode<(string K, string V)>> _map = new();
    readonly LinkedList<(string K, string V)> _lru = new();
    public TranslationCache(int max = 2000) { _max = max; }
    public int Count { get { lock (_lk) return _map.Count; } }

    public static string MakeKey(string provider, string text) => provider + "\u0001" + TextTools.Key(text);

    public bool TryGet(string key, out string value)
    {
        lock (_lk)
        {
            if (_map.TryGetValue(key, out var n)) { _lru.Remove(n); _lru.AddFirst(n); value = n.Value.V; return true; }
        }
        value = ""; return false;
    }

    public void Put(string key, string value)
    {
        lock (_lk)
        {
            if (_map.TryGetValue(key, out var old)) { _lru.Remove(old); _map.Remove(key); }
            var n = new LinkedListNode<(string, string)>((key, value));
            _lru.AddFirst(n); _map[key] = n;
            while (_map.Count > _max) { var last = _lru.Last!; _lru.RemoveLast(); _map.Remove(last.Value.K); }
        }
    }

    public void Clear() { lock (_lk) { _map.Clear(); _lru.Clear(); } }

    public void Save(string path)
    {
        try
        {
            Dictionary<string, string> d;
            lock (_lk) d = _lru.Reverse().ToDictionary(x => x.K, x => x.V);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(d));
        }
        catch (Exception e) { AppLog.Warn($"Cache se nepodařilo uložit: {e.Message}"); }
    }

    public void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (d != null) foreach (var kv in d) Put(kv.Key, kv.Value);
        }
        catch (Exception e) { AppLog.Warn($"Cache se nepodařilo načíst: {e.Message}"); }
    }
}

public sealed class TranslationStats
{
    public long Requests, CacheHits, Errors, CharsSent, Hedged;
    public int LastMs;
    public string? LastError;
    public DateTime? LastErrorUtc;
}

/// <summary>Překlad s cache, kontextem předchozích vět, opakováním při krátkodobých chybách a poctivým hlášením chyb (bez automatického přepínání).</summary>
public sealed class TranslationService
{
    ITranslator _translator;
    readonly TranslationCache _cache;
    readonly List<string> _history = new();
    readonly object _lk = new();
    public TranslationStats Stats { get; } = new();
    public bool UseContext { get; set; } = true;
    public int ContextLines { get; set; } = 2;
    /// <summary>Kolikrát zopakovat požadavek po síťové chybě / chybě serveru (ne po chybě klíče, limitu, 429).</summary>
    public int RetryCount { get; set; } = 1;
    /// <summary>Když odpověď nepřijde do tolika ms, pošle se druhý (záložní) požadavek a vezme se ten, který skončí dřív. 0 = vypnuto. Řeší občasné pomalé odpovědi Googlu.</summary>
    public int HedgeMs { get; set; } = 600;
    /// <summary>Značka páru jazyků (viz Languages.PairTag) – součást klíče cache.</summary>
    public string LangTag { get; set; } = "";
    Glossary _glossary = Glossary.Parse("");
    public Glossary Glossary { get => _glossary; set => _glossary = value ?? Glossary.Parse(""); }
    string KeyFor(string provider, string text) => TranslationCache.MakeKey(provider + (LangTag.Length > 0 ? "|" + LangTag : "") + (_glossary.IsEmpty ? "" : "|g" + _glossary.Version), text);
    /// <summary>Vlastní opravy (mají přednost před překladačem i cache).</summary>
    public CorrectionStore? Corrections { get; set; }
    public Func<int, Task> DelayFn { get; set; } = ms => Task.Delay(ms);
    public ITranslator Translator { get => _translator; set { _translator = value; } }

    public TranslationService(ITranslator t, TranslationCache cache) { _translator = t; _cache = cache; }

    string? Context()
    {
        lock (_lk)
        {
            if (UseContext && ContextLines > 0 && _history.Count > 0)
                return string.Join(" ", _history.Skip(Math.Max(0, _history.Count - ContextLines)));
            return null;
        }
    }

    async Task<T> WithRetry<T>(Func<Task<T>> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await call().ConfigureAwait(false); }
            catch (TranslationException e) when (attempt < RetryCount && e.Kind is TranslationErrorKind.Network or TranslationErrorKind.Server)
            {
                AppLog.Warn($"Překlad selhal ({e.Kind}), zkouším znovu ({attempt + 1}/{RetryCount}).");
                await DelayFn(250 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    async Task<string> Hedged(Func<Task<string>> call, CancellationToken ct)
    {
        if (HedgeMs <= 0) return await call().ConfigureAwait(false);
        var first = call();
        var wait = Task.Delay(HedgeMs, ct);
        if (await Task.WhenAny(first, wait).ConfigureAwait(false) == first) return await first.ConfigureAwait(false);
        Stats.Hedged++;
        var second = call();
        var done = await Task.WhenAny(first, second).ConfigureAwait(false);
        if (done.IsFaulted)
        {
            var other = done == first ? second : first;
            try { return await other.ConfigureAwait(false); } catch { return await done.ConfigureAwait(false); }
        }
        return await done.ConfigureAwait(false);
    }

    void Fail(Exception e) { Stats.Errors++; Stats.LastError = e.Message; Stats.LastErrorUtc = DateTime.UtcNow; }

    public async Task<TranslationResult> TranslateAsync(string english, CancellationToken ct)
    {
        var t = _translator;
        if (Corrections != null && Corrections.TryGet(english, LangTag, out var fixedText))
        {
            Stats.CacheHits++; AddHistory(english);
            return new TranslationResult(fixedText, "vaše oprava", 0, true);
        }
        var key = KeyFor(t.Name, english);
        if (_cache.TryGet(key, out var hit))
        {
            Stats.CacheHits++; AddHistory(english);
            return new TranslationResult(hit, t.Name, 0, true);
        }
        var ctx = Context();
        var sw = Stopwatch.StartNew();
        try
        {
            Stats.Requests++; Stats.CharsSent += english.Length;
            string cs;
            var gl = _glossary;
            if (!gl.IsEmpty)
            {
                var prot = gl.Protect(english);
                var raw = await WithRetry(() => Hedged(() => t.TranslateAsync(prot.Text, ctx, ct), ct)).ConfigureAwait(false);
                cs = prot.Map.Count == 0 ? raw : Glossary.Restore(raw, prot)
                     ?? await WithRetry(() => Hedged(() => t.TranslateAsync(english, ctx, ct), ct)).ConfigureAwait(false);
            }
            else cs = await WithRetry(() => Hedged(() => t.TranslateAsync(english, ctx, ct), ct)).ConfigureAwait(false);
            sw.Stop();
            Stats.LastMs = (int)sw.ElapsedMilliseconds;
            _cache.Put(key, cs); AddHistory(english);
            return new TranslationResult(cs, t.Name, Stats.LastMs);
        }
        catch (Exception e) { Fail(e); throw; }
    }

    /// <summary>Překlad více vět (režim celé obrazovky): cache zásahy se nevolají, zbytek jde v jedné dávce. Kontext se nepoužívá.</summary>
    public async Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(IReadOnlyList<string> english, CancellationToken ct)
    {
        var t = _translator;
        var res = new TranslationResult?[english.Count];
        var todo = new List<int>();
        for (int i = 0; i < english.Count; i++)
        {
            if (Corrections != null && Corrections.TryGet(english[i], LangTag, out var fx)) { Stats.CacheHits++; res[i] = new TranslationResult(fx, "vaše oprava", 0, true); }
            else if (_cache.TryGet(KeyFor(t.Name, english[i]), out var hit)) { Stats.CacheHits++; res[i] = new TranslationResult(hit, t.Name, 0, true); }
            else todo.Add(i);
        }
        if (todo.Count > 0)
        {
            var gl = _glossary;
            var prots = todo.Select(i => gl.Protect(english[i])).ToList();
            var texts = prots.Select(p => p.Text).ToList();
            var sw = Stopwatch.StartNew();
            try
            {
                Stats.Requests++; Stats.CharsSent += texts.Sum(x => x.Length);
                var cs = (await WithRetry(() => t.TranslateManyAsync(texts, null, ct)).ConfigureAwait(false)).ToList();
                for (int k = 0; k < cs.Count && k < prots.Count; k++)
                    if (prots[k].Map.Count > 0) cs[k] = Glossary.Restore(cs[k], prots[k]) ?? cs[k].Replace("Qzx", "").Replace("xq", "");
                sw.Stop(); Stats.LastMs = (int)sw.ElapsedMilliseconds;
                for (int k = 0; k < todo.Count; k++)
                {
                    _cache.Put(KeyFor(t.Name, english[todo[k]]), cs[k]);
                    res[todo[k]] = new TranslationResult(cs[k], t.Name, Stats.LastMs);
                }
            }
            catch (Exception e) { Fail(e); throw; }
        }
        return res!;
    }

    void AddHistory(string s)
    {
        lock (_lk)
        {
            if (_history.Count > 0 && TextTools.Similarity(_history[^1], s) > 0.95) return;
            _history.Add(s);
            if (_history.Count > 20) _history.RemoveAt(0);
        }
    }
    public void ResetContext() { lock (_lk) _history.Clear(); }
}

public static class TranslatorFactory
{
    public static ITranslator Create(AppSettings s, HttpClient http, ISecretProtector prot)
    {
        switch (s.Translator)
        {
            case TranslatorKind.GoogleCloud:
            {
                var k = Unprotect(prot, s.GoogleCloudKeyProtected);
                return string.IsNullOrWhiteSpace(k) ? new MissingKeyTranslator("Google Cloud Translation") : new GoogleCloudTranslator(http, k) { Source = s.SourceLang, Target = s.TargetLang };
            }
            case TranslatorKind.DeepL:
            {
                var k = Unprotect(prot, s.DeepLKeyProtected);
                return string.IsNullOrWhiteSpace(k) ? new MissingKeyTranslator("DeepL") : new DeepLTranslator(http, k) { SourceLang = Languages.Find(s.SourceLang), TargetLang = Languages.Find(s.TargetLang) };
            }
            case TranslatorKind.Gemini:
            {
                var k = Unprotect(prot, s.GeminiKeyProtected);
                return string.IsNullOrWhiteSpace(k) ? new MissingKeyTranslator("Gemini") : new GeminiTranslator(http, k)
                { Model = s.GeminiModel, GameContext = s.AiGameContext, Source = Languages.Find(s.SourceLang), Target = Languages.Find(s.TargetLang) };
            }
            case TranslatorKind.OpenAiCompatible:
                return new OpenAiCompatibleTranslator(http, Unprotect(prot, s.AiKeyProtected))
                { BaseUrl = s.AiBaseUrl, Model = s.AiModel, GameContext = s.AiGameContext, Source = Languages.Find(s.SourceLang), Target = Languages.Find(s.TargetLang) };
            default: return new GoogleUnofficialTranslator(http) { Source = s.SourceLang, Target = s.TargetLang };
        }
    }

    public static string Unprotect(ISecretProtector prot, string b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try { return prot.Unprotect(b64); } catch (Exception e) { AppLog.Warn($"Klíč nelze dešifrovat: {e.Message}"); return ""; }
    }

    public static HttpClient NewHttp(int timeoutSec = 8) => new() { Timeout = TimeSpan.FromSeconds(timeoutSec) };
}
