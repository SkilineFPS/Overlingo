using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Overlingo.Core;

public enum TranslationErrorKind { Network, RateLimit, Auth, Quota, Server, BadResponse, NotConfigured }

public sealed class TranslationException : Exception
{
    public TranslationErrorKind Kind { get; }
    public TranslationException(TranslationErrorKind kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}

public sealed record TranslationResult(string Text, string Provider, int Ms, bool FromCache = false);
public sealed record ConnectionTestResult(bool Ok, string Message, long? CharsUsed = null, long? CharsLimit = null);

public interface ITranslator
{
    string Name { get; }
    /// <summary>Překlad zdrojový → cílový jazyk (nastavuje továrna). context = předchozí věty (jen když to poskytovatel umí).</summary>
    Task<string> TranslateAsync(string text, string? context, CancellationToken ct);
    Task<ConnectionTestResult> TestAsync(CancellationToken ct);

    /// <summary>Překlad více vět najednou (pořadí zachováno). Výchozí: po jedné s omezenou paralelitou; DeepL přepisuje jedním požadavkem.</summary>
    async Task<IReadOnlyList<string>> TranslateManyAsync(IReadOnlyList<string> texts, string? context, CancellationToken ct)
    {
        var res = new string[texts.Count];
        using var gate = new SemaphoreSlim(3);
        var tasks = texts.Select(async (t, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { res[i] = await TranslateAsync(t, context, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return res;
    }
}

static class Http
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient c, HttpRequestMessage req, CancellationToken ct)
    {
        try { return await c.SendAsync(req, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TranslationException(TranslationErrorKind.Network, "Časový limit požadavku vypršel."); }
        catch (HttpRequestException e)
        { throw new TranslationException(TranslationErrorKind.Network, $"Síťová chyba: {e.Message}", e); }
    }

    public static void ThrowForStatus(HttpStatusCode code, string body, string provider)
    {
        int c = (int)code;
        if (c is >= 200 and < 300) return;
        string snippet = body.Length > 200 ? body[..200] : body;
        throw c switch
        {
            401 or 403 => new TranslationException(TranslationErrorKind.Auth, $"{provider}: neplatný klíč / přístup odepřen (HTTP {c}). {snippet}"),
            456 => new TranslationException(TranslationErrorKind.Quota, $"{provider}: vyčerpán limit znaků (HTTP 456)."),
            429 => new TranslationException(TranslationErrorKind.RateLimit, $"{provider}: příliš mnoho požadavků (HTTP 429). Zvyšte interval nebo počkejte."),
            >= 500 => new TranslationException(TranslationErrorKind.Server, $"{provider}: chyba serveru (HTTP {c})."),
            _ => new TranslationException(TranslationErrorKind.BadResponse, $"{provider}: HTTP {c}. {snippet}")
        };
    }
}

/// <summary>Neoficiální bezklíčový endpoint Google Translate. Může kdykoli přestat fungovat / být omezen.</summary>
public sealed class GoogleUnofficialTranslator : ITranslator
{
    public string Source { get; set; } = "en";
    public string Target { get; set; } = "cs";
    readonly HttpClient _http;
    public string BaseUrl { get; set; } = "https://translate.googleapis.com/translate_a/single";
    public string Name => "Google Translate (neoficiální, bez klíče)";
    public GoogleUnofficialTranslator(HttpClient http) { _http = http; }

    public async Task<string> TranslateAsync(string text, string? context, CancellationToken ct)
    {
        var url = $"{BaseUrl}?client=gtx&sl={Uri.EscapeDataString(Source)}&tl={Uri.EscapeDataString(Target)}&dt=t&q={Uri.EscapeDataString(text)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Http.ThrowForStatus(resp.StatusCode, body, "Google");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            foreach (var seg in doc.RootElement[0].EnumerateArray())
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            var r = sb.ToString().Trim();
            if (r.Length == 0) throw new TranslationException(TranslationErrorKind.BadResponse, "Google vrátil prázdný překlad.");
            return r;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new TranslationException(TranslationErrorKind.BadResponse, "Google: neočekávaný formát odpovědi (neoficiální endpoint se mohl změnit).", e); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try { var r = await TranslateAsync("Hello, how are you?", null, ct); return new(true, $"OK → „{r}“"); }
        catch (TranslationException e) { return new(false, e.Message); }
    }
}

/// <summary>Oficiální Google Cloud Translation v2 (Basic) s API klíčem.</summary>
public sealed class GoogleCloudTranslator : ITranslator
{
    public string Source { get; set; } = "en";
    public string Target { get; set; } = "cs";
    readonly HttpClient _http; readonly string _key;
    public string Endpoint { get; set; } = "https://translation.googleapis.com/language/translate/v2";
    public string Name => "Google Cloud Translation (oficiální API)";
    public GoogleCloudTranslator(HttpClient http, string apiKey) { _http = http; _key = apiKey; }

    public async Task<string> TranslateAsync(string text, string? context, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Add("X-goog-api-key", _key);
        req.Content = new StringContent(JsonSerializer.Serialize(new { q = text, source = Source, target = Target, format = "text" }), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Http.ThrowForStatus(resp.StatusCode, body, "Google Cloud");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var t = doc.RootElement.GetProperty("data").GetProperty("translations")[0].GetProperty("translatedText").GetString();
            if (string.IsNullOrWhiteSpace(t)) throw new TranslationException(TranslationErrorKind.BadResponse, "Google Cloud vrátil prázdný překlad.");
            return WebUtility.HtmlDecode(t);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new TranslationException(TranslationErrorKind.BadResponse, "Google Cloud: neočekávaný formát odpovědi.", e); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try { var r = await TranslateAsync("Hello, how are you?", null, ct); return new(true, $"OK → „{r}“"); }
        catch (TranslationException e) { return new(false, e.Message); }
    }
}

/// <summary>DeepL přes oficiální REST API v2 (Free i Pro; endpoint se volí podle klíče).</summary>
public sealed class DeepLTranslator : ITranslator
{
    readonly HttpClient _http; readonly string _key;
    public string Name => IsFree(_key) ? "DeepL API Free" : "DeepL API Pro";
    public string? BaseOverride { get; set; }
    public Lang SourceLang { get; set; } = Languages.Find("en");
    public Lang TargetLang { get; set; } = Languages.Find("cs");
    public DeepLTranslator(HttpClient http, string apiKey) { _http = http; _key = apiKey.Trim(); }

    public static bool IsFree(string key) => key.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
    public static string BaseUrlFor(string key) => IsFree(key) ? "https://api-free.deepl.com" : "https://api.deepl.com";
    string Base => BaseOverride ?? BaseUrlFor(_key);

    HttpRequestMessage Make(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, Base + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _key);
        return r;
    }

    public async Task<string> TranslateAsync(string text, string? context, CancellationToken ct) =>
        (await TranslateManyAsync(new[] { text }, context, ct).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<string>> TranslateManyAsync(IReadOnlyList<string> texts, string? context, CancellationToken ct)
    {
        if (TargetLang.DeepL == null)
            throw new TranslationException(TranslationErrorKind.NotConfigured, $"DeepL nepodporuje cílový jazyk „{TargetLang.Name}“. Zvolte Google nebo jiný překladač.");
        using var req = Make(HttpMethod.Post, "/v2/translate");
        var payload = new Dictionary<string, object?>
        {
            ["text"] = texts.ToArray(), ["source_lang"] = SourceLang.DeepLSource, ["target_lang"] = TargetLang.DeepL
        };
        if (!string.IsNullOrWhiteSpace(context)) payload["context"] = context;
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Http.ThrowForStatus(resp.StatusCode, body, "DeepL");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("translations");
            if (arr.GetArrayLength() != texts.Count) throw new TranslationException(TranslationErrorKind.BadResponse, "DeepL vrátil jiný počet překladů, než bylo odesláno.");
            var res = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                var t = el.GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(t)) throw new TranslationException(TranslationErrorKind.BadResponse, "DeepL vrátil prázdný překlad.");
                res.Add(t);
            }
            return res;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new TranslationException(TranslationErrorKind.BadResponse, "DeepL: neočekávaný formát odpovědi.", e); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            using var req = Make(HttpMethod.Get, "/v2/usage");
            using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Http.ThrowForStatus(resp.StatusCode, body, "DeepL");
            using var doc = JsonDocument.Parse(body);
            long used = doc.RootElement.GetProperty("character_count").GetInt64();
            long limit = doc.RootElement.GetProperty("character_limit").GetInt64();
            return new(true, $"{Name}: spojení OK, využito {used:N0} / {limit:N0} znaků.", used, limit);
        }
        catch (TranslationException e) { return new(false, e.Message); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, "DeepL: neočekávaná odpověď při testu spojení."); }
    }
}

/// <summary>Zástupce, když není zadán klíč – vždy čestně selže (žádné tiché přepínání na jiný překladač).</summary>
public sealed class MissingKeyTranslator : ITranslator
{
    readonly string _name;
    public MissingKeyTranslator(string name) { _name = name; }
    public string Name => _name;
    public Task<string> TranslateAsync(string text, string? context, CancellationToken ct) =>
        throw new TranslationException(TranslationErrorKind.NotConfigured, $"{_name}: není zadán API klíč.");
    public Task<ConnectionTestResult> TestAsync(CancellationToken ct) =>
        Task.FromResult(new ConnectionTestResult(false, $"{_name}: není zadán API klíč."));
}
