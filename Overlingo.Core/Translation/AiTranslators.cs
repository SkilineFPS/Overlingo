using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Overlingo.Core;

/// <summary>Společné sestavení instrukce pro jazykové modely (titulky ze hry).</summary>
public static class AiPrompt
{
    public static string System(Lang src, Lang tgt, string? gameContext) =>
        $"You translate video game subtitles from {src.Name} to {tgt.Name}. " +
        "Return ONLY the translation of the last subtitle line, with no quotes, notes or explanations. " +
        "Keep it natural, concise and in the speaker's tone (slang and swearing included). Keep proper names unchanged. " +
        "The text comes from OCR and may contain small recognition errors – silently fix obvious ones. " +
        (string.IsNullOrWhiteSpace(gameContext) ? "" : $"Game context: {gameContext.Trim()} ");

    public static string User(string text, string? previous) =>
        string.IsNullOrWhiteSpace(previous) ? text : $"Previous lines (context only, do not translate): {previous}\n\nTranslate this line:\n{text}";

    /// <summary>Odstraní typické obalení odpovědi (uvozovky, „Translation:“, bloky kódu).</summary>
    public static string Clean(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```")) { s = s.Trim('`').Trim(); }
        foreach (var pre in new[] { "Translation:", "Překlad:" })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) s = s[pre.Length..].Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '„' && s[^1] == '“')) s = s[1..^1].Trim();
        return s;
    }
}

public sealed class GeminiTranslator : ITranslator
{
    readonly HttpClient _http; readonly string _key;
    public string Model { get; set; } = "gemini-3.8-flash";
    public string GameContext { get; set; } = "";
    public Lang Source { get; set; } = Languages.Find("en");
    public Lang Target { get; set; } = Languages.Find("cs");
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    public GeminiTranslator(HttpClient http, string key) { _http = http; _key = key; }
    public string Name => $"Gemini ({Model})";

    public async Task<string> TranslateAsync(string text, string? context, CancellationToken ct)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(Model)}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-goog-api-key", _key);
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = AiPrompt.System(Source, Target, GameContext) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = AiPrompt.User(text, context) } } } },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 400, thinkingConfig = new { thinkingBudget = 0 } }
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Http.ThrowForStatus(resp.StatusCode, body, "Gemini");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
            var sb = new StringBuilder();
            foreach (var p in parts.EnumerateArray()) if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            var r = AiPrompt.Clean(sb.ToString());
            if (r.Length == 0) throw new TranslationException(TranslationErrorKind.BadResponse, "Gemini vrátil prázdnou odpověď (možná zablokováno filtrem obsahu).");
            return r;
        }
        catch (TranslationException) { throw; }
        catch (Exception e) { throw new TranslationException(TranslationErrorKind.BadResponse, "Gemini: nečekaná odpověď.", e); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try { var r = await TranslateAsync("Hello, how are you?", null, ct).ConfigureAwait(false); return new ConnectionTestResult(true, $"OK: „{r}“"); }
        catch (TranslationException e) { return new ConnectionTestResult(false, e.Message); }
    }
}

/// <summary>Libovolný server s OpenAI API (OpenAI, OpenRouter, Ollama, LM Studio…): POST {BaseUrl}/chat/completions.</summary>
public sealed class OpenAiCompatibleTranslator : ITranslator
{
    readonly HttpClient _http; readonly string _key;
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";
    public string GameContext { get; set; } = "";
    public Lang Source { get; set; } = Languages.Find("en");
    public Lang Target { get; set; } = Languages.Find("cs");
    public OpenAiCompatibleTranslator(HttpClient http, string key) { _http = http; _key = key; }
    public string Name => $"AI ({(string.IsNullOrWhiteSpace(Model) ? "?" : Model)})";

    public async Task<string> TranslateAsync(string text, string? context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model))
            throw new TranslationException(TranslationErrorKind.NotConfigured, "AI překladač: není zadán název modelu.");
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(_key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        var payload = new
        {
            model = Model, temperature = 0.2, max_tokens = 400,
            messages = new object[]
            {
                new { role = "system", content = AiPrompt.System(Source, Target, GameContext) },
                new { role = "user", content = AiPrompt.User(text, context) }
            }
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Http.ThrowForStatus(resp.StatusCode, body, "AI");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = AiPrompt.Clean(doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
            if (r.Length == 0) throw new TranslationException(TranslationErrorKind.BadResponse, "AI vrátila prázdnou odpověď.");
            return r;
        }
        catch (TranslationException) { throw; }
        catch (Exception e) { throw new TranslationException(TranslationErrorKind.BadResponse, "AI: nečekaná odpověď.", e); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try { var r = await TranslateAsync("Hello, how are you?", null, ct).ConfigureAwait(false); return new ConnectionTestResult(true, $"OK: „{r}“"); }
        catch (TranslationException e) { return new ConnectionTestResult(false, e.Message); }
    }
}
