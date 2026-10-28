using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CalorieTracker.Web.Services.Ai;

public class AiOptions
{
    /// <summary>"gemini" or "ollama": which configured provider to try first.</summary>
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? OllamaBaseUrl { get; set; }
    public string? OllamaModel { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

public interface IFoodVisionProvider
{
    string Name { get; }
    string Model { get; }
    bool IsConfigured { get; }
    Task<FoodAnalysisResult> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct);
}

public record FoodVisionOutcome(FoodAnalysisResult Result, string Provider, string Model);

public interface IFoodVisionService
{
    bool IsConfigured { get; }
    IReadOnlyList<string> ConfiguredProviders { get; }
    Task<FoodVisionOutcome> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct);
}

/// <summary>Uses the selected provider first, then any other configured provider when the first is out of quota or down.</summary>
public class FoodVisionService(IEnumerable<IFoodVisionProvider> providers, AiOptions options, ILogger<FoodVisionService> log) : IFoodVisionService
{
    private readonly List<IFoodVisionProvider> _ordered = providers.Where(p => p.IsConfigured)
        .OrderBy(p => string.Equals(p.Name, options.Provider, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

    public bool IsConfigured => _ordered.Count > 0;
    public IReadOnlyList<string> ConfiguredProviders => _ordered.Select(p => p.Name).ToList();

    public async Task<FoodVisionOutcome> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct)
    {
        if (_ordered.Count == 0)
            throw new FoodVisionException(FoodVisionErrorKind.NotConfigured, "AI food scanning isn't set up yet. You can still add food manually.");

        FoodVisionException? last = null;
        foreach (var p in _ordered)
        {
            try
            {
                return new FoodVisionOutcome(await p.AnalyzeAsync(image, mimeType, userContext, ct), p.Name, p.Model);
            }
            catch (FoodVisionException ex) when (ex.CanFallBack)
            {
                log.LogWarning("Vision provider {Provider} failed with {Kind}; trying the next configured provider if any.", p.Name, ex.Kind);
                last = ex;
            }
        }
        throw last!;
    }
}

public class GeminiFoodVisionProvider(HttpClient http, AiOptions options, ILogger<GeminiFoodVisionProvider> log) : IFoodVisionProvider
{
    public string Name => "gemini";
    public string Model => string.IsNullOrWhiteSpace(options.Model) ? "gemini-2.5-flash" : options.Model;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public async Task<FoodAnalysisResult> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = FoodVisionPrompt.System }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = Convert.ToBase64String(image) } },
                    new JsonObject { ["text"] = FoodVisionPrompt.UserMessage(userContext) }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = FoodVisionPrompt.Schema(upperCaseTypes: true),
                ["temperature"] = 0.2,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(Model)}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        // Header, not query string, so the key never shows up in request logs.
        req.Headers.Add("x-goog-api-key", options.ApiKey);

        var json = await VisionHttp.SendAsync(http, req, Name, log, ct);
        var candidate = json["candidates"]?[0];
        if (json["promptFeedback"]?["blockReason"] is not null || candidate?["finishReason"]?.GetValue<string>() is "SAFETY" or "PROHIBITED_CONTENT")
            throw new FoodVisionException(FoodVisionErrorKind.Rejected, "The AI provider declined to analyze this image. Try another photo or add the food manually.");
        var text = candidate?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        return FoodAnalysisParser.Parse(text);
    }
}

/// <summary>Local, free vision model through Ollama (e.g. qwen2.5vl or llama3.2-vision).</summary>
public class OllamaFoodVisionProvider(HttpClient http, AiOptions options, ILogger<OllamaFoodVisionProvider> log) : IFoodVisionProvider
{
    public string Name => "ollama";
    public string Model => string.IsNullOrWhiteSpace(options.OllamaModel) ? "qwen2.5vl:7b" : options.OllamaModel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.OllamaBaseUrl);

    public async Task<FoodAnalysisResult> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = false,
            ["format"] = FoodVisionPrompt.Schema(upperCaseTypes: false),
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = FoodVisionPrompt.System },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FoodVisionPrompt.UserMessage(userContext),
                    ["images"] = new JsonArray(Convert.ToBase64String(image)),
                }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.OllamaBaseUrl!), "/api/chat")) { Content = JsonContent.Create(body) };
        var json = await VisionHttp.SendAsync(http, req, Name, log, ct);
        return FoodAnalysisParser.Parse(json["message"]?["content"]?.GetValue<string>());
    }
}

internal static class VisionHttp
{
    public static async Task<JsonNode> SendAsync(HttpClient http, HttpRequestMessage req, string provider, ILogger log, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FoodVisionException(FoodVisionErrorKind.Timeout, "The AI took too long to respond. Please try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Could not reach vision provider {Provider}", provider);
            throw new FoodVisionException(FoodVisionErrorKind.Unavailable, "The AI service can't be reached right now. You can add the food manually.", ex);
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // Log status and a short body excerpt only; provider errors never echo the API key.
                log.LogWarning("Vision provider {Provider} returned {Status}: {Body}", provider, (int)res.StatusCode, text.Length > 300 ? text[..300] : text);
                throw res.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests when text.Contains("quota", StringComparison.OrdinalIgnoreCase) =>
                        new FoodVisionException(FoodVisionErrorKind.QuotaExceeded, "The AI provider's free quota is used up for now. Try again later, or add the food manually."),
                    HttpStatusCode.TooManyRequests =>
                        new FoodVisionException(FoodVisionErrorKind.RateLimited, "The AI provider is rate limiting requests. Wait a minute and try again."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest when text.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
                        new FoodVisionException(FoodVisionErrorKind.NotConfigured, "The AI provider rejected the configured API key. Check AI_API_KEY."),
                    HttpStatusCode.NotFound =>
                        new FoodVisionException(FoodVisionErrorKind.NotConfigured, "The configured AI model wasn't found. Check AI_MODEL / OLLAMA_MODEL."),
                    >= HttpStatusCode.InternalServerError =>
                        new FoodVisionException(FoodVisionErrorKind.Unavailable, "The AI service had a problem. Please try again shortly."),
                    _ => new FoodVisionException(FoodVisionErrorKind.InvalidResponse, "The AI service couldn't process this request. Try another photo or add the food manually."),
                };
            }
            try { return JsonNode.Parse(text) ?? throw new FormatException(); }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                throw new FoodVisionException(FoodVisionErrorKind.InvalidResponse, "The AI service returned an unreadable response. Please try again.", ex);
            }
        }
    }
}
