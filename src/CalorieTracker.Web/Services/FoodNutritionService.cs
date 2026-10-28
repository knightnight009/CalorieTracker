using System.Text.Json.Nodes;

namespace CalorieTracker.Web.Services;

public class UsdaOptions
{
    public string? ApiKey { get; set; }
}

/// <summary>Nutrients per 100 g, as published by USDA FoodData Central.</summary>
public record UsdaFood(string FdcId, string Description, string? Brand, string DataType,
    decimal Calories, decimal ProteinGrams, decimal CarbohydratesGrams, decimal FatGrams,
    decimal? FiberGrams, decimal? SugarGrams, decimal? SodiumMilligrams);

public interface IFoodNutritionService
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<UsdaFood>> SearchAsync(string query, CancellationToken ct);
    Task<UsdaFood?> GetAsync(string fdcId, CancellationToken ct);
}

public class UsdaFoodNutritionService(HttpClient http, UsdaOptions options, ILogger<UsdaFoodNutritionService> log) : IFoodNutritionService
{
    private const string BaseUrl = "https://api.nal.usda.gov/fdc/v1/";
    // Energy (kcal) is 208 in most data types; Foundation foods sometimes only carry Atwater energy (958 general, 957 specific).
    private static readonly string[] EnergyNumbers = ["208", "958", "957"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public async Task<IReadOnlyList<UsdaFood>> SearchAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{BaseUrl}foods/search?query={Uri.EscapeDataString(query.Trim())}&pageSize=15&dataType=Foundation,SR%20Legacy,Survey%20(FNDDS),Branded";
        var json = await GetJsonAsync(url, ct);
        return json?["foods"] is JsonArray foods ? foods.Select(f => f is JsonObject o ? ParseFood(o) : null).OfType<UsdaFood>().ToList() : [];
    }

    public async Task<UsdaFood?> GetAsync(string fdcId, CancellationToken ct)
    {
        if (!IsConfigured || !fdcId.All(char.IsDigit) || fdcId.Length is 0 or > 12) return null;
        var json = await GetJsonAsync($"{BaseUrl}food/{fdcId}?format=abridged", ct);
        return json is JsonObject o ? ParseFood(o) : null;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", options.ApiKey);
        try
        {
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("USDA FoodData Central returned {Status}", (int)res.StatusCode);
                throw new FoodDatabaseUnavailableException();
            }
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "USDA FoodData Central request failed");
            throw new FoodDatabaseUnavailableException();
        }
    }

    /// <summary>Handles both the search result shape (nutrientNumber/value) and the abridged food shape (number/amount).</summary>
    public static UsdaFood? ParseFood(JsonObject o)
    {
        var id = o["fdcId"]?.ToString();
        var name = o["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

        var nutrients = new Dictionary<string, decimal>();
        if (o["foodNutrients"] is JsonArray arr)
            foreach (var n in arr.OfType<JsonObject>())
            {
                var number = (n["nutrientNumber"] ?? n["number"] ?? n["nutrient"]?["number"])?.ToString();
                var unit = (n["unitName"] ?? n["nutrient"]?["unitName"])?.ToString();
                var value = n["value"] ?? n["amount"];
                if (number is null || value is null || !decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                if (EnergyNumbers.Contains(number) && !string.Equals(unit, "KCAL", StringComparison.OrdinalIgnoreCase)) continue; // skip kJ rows
                nutrients.TryAdd(number, v);
            }

        decimal? Get(params string[] numbers) => numbers.Select(n => nutrients.TryGetValue(n, out var v) ? v : (decimal?)null).FirstOrDefault(v => v is not null);
        var kcal = Get(EnergyNumbers);
        if (kcal is null) return null; // without energy the entry is not usable for tracking

        var brand = (o["brandName"] ?? o["brandOwner"])?.GetValue<string>();
        name = name.Length > 200 ? name[..200] : name;
        return new UsdaFood(id, name, brand, o["dataType"]?.GetValue<string>() ?? "",
            kcal.Value, Get("203") ?? 0, Get("205") ?? 0, Get("204") ?? 0, Get("291"), Get("269"), Get("307"));
    }
}

public class FoodDatabaseUnavailableException() : Exception("The USDA food database is unavailable right now.");
