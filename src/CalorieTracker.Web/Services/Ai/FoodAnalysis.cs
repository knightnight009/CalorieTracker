using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CalorieTracker.Web.Services.Ai;

public record FoodItemAnalysis(
    string Name,
    string PortionDescription,
    string? PortionUnit,
    decimal? EstimatedWeightGrams,
    decimal Calories,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams,
    decimal? FiberGrams,
    decimal? SugarGrams,
    string? CookingMethod,
    decimal Confidence,
    string? Notes,
    IReadOnlyList<string> Warnings);

public record FoodAnalysisResult(
    IReadOnlyList<FoodItemAnalysis> Items,
    /// <summary>Recomputed on the server from the items; AI-supplied totals are ignored.</summary>
    decimal TotalCalories,
    decimal TotalProteinGrams,
    decimal TotalCarbsGrams,
    decimal TotalFatGrams,
    decimal OverallConfidence,
    bool RequiresClarification,
    string? ClarificationQuestion,
    IReadOnlyList<string> Warnings);

/// <summary>What's left for the day; the only user data sent with a meal-ideas request.</summary>
public record MealIdeasRequest(Models.MealType MealType, decimal Calories, decimal ProteinGrams, decimal CarbohydratesGrams, decimal FatGrams,
    string? Preferences, int Count = 3);

/// <summary>A suggested meal. Totals are always the server's sum of its items.</summary>
public record MealIdea(string Name, IReadOnlyList<FoodItemAnalysis> Items)
{
    public decimal TotalCalories => Items.Sum(i => i.Calories);
    public decimal TotalProteinGrams => Items.Sum(i => i.ProteinGrams);
    public decimal TotalCarbsGrams => Items.Sum(i => i.CarbsGrams);
    public decimal TotalFatGrams => Items.Sum(i => i.FatGrams);
}

public enum FoodVisionErrorKind { NotConfigured, QuotaExceeded, RateLimited, Timeout, Unavailable, InvalidResponse, Rejected }

public class FoodVisionException(FoodVisionErrorKind kind, string userMessage, Exception? inner = null)
    : Exception(userMessage, inner)
{
    public FoodVisionErrorKind Kind { get; } = kind;
    /// <summary>Errors where trying another configured provider might help.</summary>
    public bool CanFallBack => Kind is FoodVisionErrorKind.QuotaExceeded or FoodVisionErrorKind.RateLimited
        or FoodVisionErrorKind.Unavailable or FoodVisionErrorKind.Timeout or FoodVisionErrorKind.NotConfigured;
}

/// <summary>Validates untrusted model output into a <see cref="FoodAnalysisResult"/>.</summary>
public static class FoodAnalysisParser
{
    public const int MaxItems = 25;
    private const decimal MaxItemCalories = 4000, MaxItemMacroGrams = 500, MaxItemWeightGrams = 3000;

    public static FoodAnalysisResult Parse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText)) throw Invalid("The AI returned an empty response.");
        var text = modelText.Trim();
        // Some models wrap JSON in a markdown fence despite instructions.
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start) throw Invalid("The AI response was not JSON.");
            text = text[start..(end + 1)];
        }

        JsonObject root;
        try { root = JsonNode.Parse(text) as JsonObject ?? throw Invalid("The AI response was not a JSON object."); }
        catch (JsonException ex) { throw Invalid("The AI response was not valid JSON.", ex); }

        var warnings = new List<string>();
        var items = new List<FoodItemAnalysis>();
        if (root["items"] is JsonArray arr)
        {
            foreach (var node in arr.Take(MaxItems))
            {
                if (node is not JsonObject o) continue;
                var item = ParseItem(o);
                if (item is null) { warnings.Add("One detected item had missing or implausible values and was left out. Add it manually if needed."); continue; }
                items.Add(item);
            }
            if (arr.Count > MaxItems) warnings.Add($"Only the first {MaxItems} items were kept.");
        }

        var requiresClarification = Bool(root["requiresClarification"]);
        var question = Str(root["clarificationQuestion"], 500);
        if (items.Count == 0 && !requiresClarification)
        {
            requiresClarification = true;
            question ??= "No food could be identified. Try a clearer photo, or describe the meal in the context box.";
        }

        var overall = Confidence(root["overallConfidence"]) ?? (items.Count > 0 ? items.Average(i => i.Confidence) : 0);
        if (overall < 0.5m) warnings.Add("Low confidence: please check each item carefully before saving.");

        return new FoodAnalysisResult(items,
            items.Sum(i => i.Calories), items.Sum(i => i.ProteinGrams), items.Sum(i => i.CarbsGrams), items.Sum(i => i.FatGrams),
            overall, requiresClarification, requiresClarification ? question : null, warnings);
    }

    public const int MaxMeals = 5;

    /// <summary>Validates a meal-ideas answer. Meals whose items are all implausible are dropped.</summary>
    public static IReadOnlyList<MealIdea> ParseMeals(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText)) throw Invalid("The AI returned an empty response.");
        var text = modelText.Trim();
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start) throw Invalid("The AI response was not JSON.");
            text = text[start..(end + 1)];
        }
        JsonObject root;
        try { root = JsonNode.Parse(text) as JsonObject ?? throw Invalid("The AI response was not a JSON object."); }
        catch (JsonException ex) { throw Invalid("The AI response was not valid JSON.", ex); }

        var meals = new List<MealIdea>();
        if (root["meals"] is JsonArray arr)
            foreach (var node in arr.Take(MaxMeals).OfType<JsonObject>())
            {
                var items = (node["items"] as JsonArray)?.Take(MaxItems).OfType<JsonObject>().Select(ParseItem).OfType<FoodItemAnalysis>().ToList() ?? [];
                if (items.Count == 0) continue;
                meals.Add(new MealIdea(Str(node["name"], 120) ?? "Meal idea", items));
            }
        if (meals.Count == 0) throw Invalid("The AI didn't return any usable meal ideas.");
        return meals;
    }

    private static FoodItemAnalysis? ParseItem(JsonObject o)
    {
        var name = Str(o["name"], 120);
        var calories = Num(o["calories"]);
        var protein = Num(o["proteinGrams"]);
        var carbs = Num(o["carbsGrams"]);
        var fat = Num(o["fatGrams"]);
        if (name is null || calories is null || protein is null || carbs is null || fat is null) return null;
        if (calories is < 0 or > MaxItemCalories) return null;
        if (new[] { protein, carbs, fat }.Any(g => g is < 0 or > MaxItemMacroGrams)) return null;

        var weight = Num(o["estimatedWeightGrams"]);
        if (weight is <= 0 or > MaxItemWeightGrams) weight = null;
        var fiber = Num(o["fiberGrams"]);
        var sugar = Num(o["sugarGrams"]);

        var itemWarnings = new List<string>();
        var macroKcal = protein.Value * 4 + carbs.Value * 4 + fat.Value * 9;
        if (Math.Abs(calories.Value - macroKcal) > Math.Max(50, macroKcal * 0.25m))
            itemWarnings.Add($"Calories ({calories:0}) don't match the macros (~{macroKcal:0} kcal). Please check.");

        return new FoodItemAnalysis(name,
            Str(o["portionDescription"], 100) ?? "1 portion",
            Str(o["portionUnit"], 30),
            weight, calories.Value, protein.Value, carbs.Value, fat.Value,
            fiber is >= 0 and <= MaxItemMacroGrams ? fiber : null,
            sugar is >= 0 and <= MaxItemMacroGrams ? sugar : null,
            Str(o["cookingMethod"], 60),
            Confidence(o["confidence"]) ?? 0.5m,
            Str(o["notes"], 500),
            itemWarnings);
    }

    private static FoodVisionException Invalid(string msg, Exception? inner = null) =>
        new(FoodVisionErrorKind.InvalidResponse, msg + " Please try again or add the food manually.", inner);

    private static string? Str(JsonNode? n, int max)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
    }

    private static decimal? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return Math.Round(d, 2);
        if (v.TryGetValue<double>(out var dbl) && double.IsFinite(dbl)) return Math.Round((decimal)dbl, 2);
        if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return Math.Round(d, 2);
        return null;
    }

    private static decimal? Confidence(JsonNode? n)
    {
        var c = Num(n);
        if (c is null or < 0) return null;
        if (c > 1 && c <= 100) c /= 100; // some models answer in percent
        return Math.Min(c.Value, 1);
    }

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
