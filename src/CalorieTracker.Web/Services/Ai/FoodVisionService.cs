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
    /// <summary>Comma-separated models to try when <see cref="Model"/> is rate limited, overloaded or retired. Empty turns switching off.</summary>
    public string? ModelFallbacks { get; set; }
    public string? OllamaBaseUrl { get; set; }
    public string? OllamaModel { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Gemini models in the order to try them.</summary>
    public IReadOnlyList<string> GeminiModels =>
        new[] { string.IsNullOrWhiteSpace(Model) ? "gemini-flash-lite-latest" : Model.Trim() }
            .Concat((ModelFallbacks ?? "gemini-3.5-flash-lite,gemini-3.1-flash-lite").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public interface IFoodVisionProvider
{
    string Name { get; }
    string Model { get; }
    bool IsConfigured { get; }
    /// <summary>
    /// One structured-output call returning the model's JSON text. The image is optional and the schema is passed in,
    /// so the same path serves photo scans, text search and meal ideas. Schema(true) = Gemini's upper-case type names.
    /// </summary>
    Task<string?> GenerateJsonAsync(byte[]? image, string? mimeType, string systemPrompt, string userText, Func<bool, JsonObject> schema, CancellationToken ct);
}

public record FoodVisionOutcome(FoodAnalysisResult Result, string Provider, string Model);
public record MealIdeasOutcome(IReadOnlyList<MealIdea> Meals, string Provider, string Model);

public interface IFoodVisionService
{
    bool IsConfigured { get; }
    IReadOnlyList<string> ConfiguredProviders { get; }
    Task<FoodVisionOutcome> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct);
    /// <summary>Estimates nutrition for foods matching a text search (no database lookup).</summary>
    Task<FoodVisionOutcome> EstimateFromTextAsync(string query, CancellationToken ct);
    /// <summary>Suggests meals that fit the remaining targets. Only these numbers and the user's preference text are sent.</summary>
    Task<MealIdeasOutcome> SuggestMealsAsync(MealIdeasRequest request, CancellationToken ct);
    /// <summary>Breaks one estimated dish (for example "chicken rice bowl") into separate ingredients.</summary>
    Task<FoodVisionOutcome> SplitDishAsync(FoodItemAnalysis dish, CancellationToken ct);
}

/// <summary>Uses the selected provider first, then any other configured provider when the first is out of quota or down.</summary>
public class FoodVisionService(IEnumerable<IFoodVisionProvider> providers, AiOptions options, ILogger<FoodVisionService> log) : IFoodVisionService
{
    private readonly List<IFoodVisionProvider> _ordered = providers.Where(p => p.IsConfigured)
        .OrderBy(p => string.Equals(p.Name, options.Provider, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

    public bool IsConfigured => _ordered.Count > 0;
    public IReadOnlyList<string> ConfiguredProviders => _ordered.Select(p => p.Name).Distinct().ToList();

    public async Task<FoodVisionOutcome> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct)
    {
        var (result, provider, model) = await RunAsync(p => p.GenerateJsonAsync(image, mimeType, FoodVisionPrompt.System, FoodVisionPrompt.UserMessage(userContext), FoodVisionPrompt.Schema, ct), FoodAnalysisParser.Parse);
        return new FoodVisionOutcome(result, provider, model);
    }

    public async Task<FoodVisionOutcome> EstimateFromTextAsync(string query, CancellationToken ct)
    {
        var (result, provider, model) = await RunAsync(p => p.GenerateJsonAsync(null, null, FoodVisionPrompt.TextSearchSystem, FoodVisionPrompt.TextSearchMessage(query), FoodVisionPrompt.Schema, ct), FoodAnalysisParser.Parse);
        return new FoodVisionOutcome(result, provider, model);
    }

    public async Task<MealIdeasOutcome> SuggestMealsAsync(MealIdeasRequest request, CancellationToken ct)
    {
        var (meals, provider, model) = await RunAsync(p => p.GenerateJsonAsync(null, null, FoodVisionPrompt.MealIdeasSystem, FoodVisionPrompt.MealIdeasMessage(request), FoodVisionPrompt.MealIdeasSchema, ct), FoodAnalysisParser.ParseMeals);
        return new MealIdeasOutcome(meals, provider, model);
    }

    public async Task<FoodVisionOutcome> SplitDishAsync(FoodItemAnalysis dish, CancellationToken ct)
    {
        var (result, provider, model) = await RunAsync(p => p.GenerateJsonAsync(null, null, FoodVisionPrompt.SplitDishSystem, FoodVisionPrompt.SplitDishMessage(dish), FoodVisionPrompt.Schema, ct), FoodAnalysisParser.Parse);
        return new FoodVisionOutcome(result, provider, model);
    }

    // Parsing happens after the provider call, so an unreadable answer is reported rather than retried on another provider.
    private async Task<(T Result, string Provider, string Model)> RunAsync<T>(Func<IFoodVisionProvider, Task<string?>> call, Func<string?, T> parse)
    {
        if (_ordered.Count == 0)
            throw new FoodVisionException(FoodVisionErrorKind.NotConfigured, "AI estimates aren't set up yet. You can still add food manually.");

        FoodVisionException? last = null;
        foreach (var p in _ordered)
        {
            try
            {
                var text = await call(p);
                return (parse(text), p.Name, p.Model);
            }
            catch (FoodVisionException ex) when (ex.CanFallBack)
            {
                log.LogWarning("Vision provider {Provider} model {Model} failed with {Kind}; trying the next configured one if any.", p.Name, p.Model, ex.Kind);
                last = ex;
            }
        }
        throw last!;
    }
}

/// <summary>One Gemini model. Program.cs registers one per entry in AiOptions.GeminiModels, so FoodVisionService switches between them.</summary>
public class GeminiFoodVisionProvider(HttpClient http, AiOptions options, string model, ILogger<GeminiFoodVisionProvider> log) : IFoodVisionProvider
{
    public string Name => "gemini";
    public string Model => model;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public async Task<string?> GenerateJsonAsync(byte[]? image, string? mimeType, string systemPrompt, string userText, Func<bool, JsonObject> schema, CancellationToken ct)
    {
        var parts = new JsonArray();
        if (image is not null) parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = Convert.ToBase64String(image) } });
        parts.Add(new JsonObject { ["text"] = userText });
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema(true),
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
            throw new FoodVisionException(FoodVisionErrorKind.Rejected, "The AI provider declined this request. Try different wording or another photo, or add the food manually.");
        // Newer models may return thought parts first; the answer is the last text part.
        return (candidate?["content"]?["parts"] as JsonArray)?.LastOrDefault(p => p?["text"] is not null)?["text"]?.GetValue<string>();
    }
}

/// <summary>Local, free vision model through Ollama (e.g. qwen2.5vl or llama3.2-vision).</summary>
public class OllamaFoodVisionProvider(HttpClient http, AiOptions options, ILogger<OllamaFoodVisionProvider> log) : IFoodVisionProvider
{
    public string Name => "ollama";
    public string Model => string.IsNullOrWhiteSpace(options.OllamaModel) ? "qwen2.5vl:7b" : options.OllamaModel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.OllamaBaseUrl);

    public async Task<string?> GenerateJsonAsync(byte[]? image, string? mimeType, string systemPrompt, string userText, Func<bool, JsonObject> schema, CancellationToken ct)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = userText };
        if (image is not null) user["images"] = new JsonArray(Convert.ToBase64String(image));
        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = false,
            ["format"] = schema(false),
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = systemPrompt }, user),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.OllamaBaseUrl!), "/api/chat")) { Content = JsonContent.Create(body) };
        var json = await VisionHttp.SendAsync(http, req, Name, log, ct);
        return json["message"]?["content"]?.GetValue<string>();
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
