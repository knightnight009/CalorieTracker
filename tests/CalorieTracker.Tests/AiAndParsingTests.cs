using System.Text.Json.Nodes;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.Services.Ai;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalorieTracker.Tests;

public class FoodAnalysisParserTests
{
    private const string Valid = """
        {"items":[
          {"name":"Cooked white rice","portionDescription":"1 cup","estimatedWeightGrams":158,"calories":205,"proteinGrams":4.3,"carbsGrams":44.5,"fatGrams":0.4,"confidence":0.88,"notes":"Visible serving."},
          {"name":"Grilled chicken breast","portionDescription":"~120 g","estimatedWeightGrams":120,"calories":198,"proteinGrams":37,"carbsGrams":0,"fatGrams":4.3,"confidence":0.76,"notes":"Oil may add calories."}
        ],"totalCalories":9999,"overallConfidence":0.82,"requiresClarification":false,"clarificationQuestion":null}
        """;

    [Fact]
    public void Parses_Items_AndRecomputesTotals_IgnoringAiTotals()
    {
        var r = FoodAnalysisParser.Parse(Valid);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(403m, r.TotalCalories); // not 9999
        Assert.Equal(41.3m, r.TotalProteinGrams);
        Assert.Equal(4.7m, r.TotalFatGrams);
        Assert.Equal(0.82m, r.OverallConfidence);
        Assert.False(r.RequiresClarification);
    }

    [Fact]
    public void Accepts_MarkdownFencedJson() =>
        Assert.Equal(2, FoodAnalysisParser.Parse("```json\n" + Valid + "\n```").Items.Count);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"items\": [")]
    public void Rejects_Malformed(string text)
    {
        var ex = Assert.Throws<FoodVisionException>(() => FoodAnalysisParser.Parse(text));
        Assert.Equal(FoodVisionErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void Drops_ImplausibleItems_WithWarning()
    {
        var r = FoodAnalysisParser.Parse("""
            {"items":[
              {"name":"Apple","portionDescription":"1 medium","calories":95,"proteinGrams":0.5,"carbsGrams":25,"fatGrams":0.3,"confidence":0.9,"notes":""},
              {"name":"Impossible","portionDescription":"x","calories":-10,"proteinGrams":1,"carbsGrams":1,"fatGrams":1,"confidence":0.9,"notes":""},
              {"name":"","portionDescription":"x","calories":10,"proteinGrams":1,"carbsGrams":1,"fatGrams":1,"confidence":0.9,"notes":""},
              {"name":"Huge","portionDescription":"x","calories":90000,"proteinGrams":1,"carbsGrams":1,"fatGrams":1,"confidence":0.9,"notes":""}
            ],"overallConfidence":0.9,"requiresClarification":false}
            """);
        Assert.Single(r.Items);
        Assert.Equal(3, r.Warnings.Count(w => w.Contains("left out")));
    }

    [Fact]
    public void Flags_CaloriesInconsistentWithMacros()
    {
        var r = FoodAnalysisParser.Parse("""
            {"items":[{"name":"Mystery","portionDescription":"1","calories":900,"proteinGrams":10,"carbsGrams":10,"fatGrams":5,"confidence":0.6,"notes":""}],
             "overallConfidence":0.6,"requiresClarification":false}
            """);
        Assert.Contains("don't match the macros", r.Items[0].Warnings.Single());
    }

    [Fact]
    public void NoItems_MeansClarificationNeeded()
    {
        var r = FoodAnalysisParser.Parse("""{"items":[],"overallConfidence":0.1,"requiresClarification":false}""");
        Assert.True(r.RequiresClarification);
        Assert.NotNull(r.ClarificationQuestion);
    }

    [Fact]
    public void Clamps_ConfidenceAndTruncatesText()
    {
        var longName = new string('x', 500);
        var r = FoodAnalysisParser.Parse($$"""
            {"items":[{"name":"{{longName}}","portionDescription":"1","calories":100,"proteinGrams":5,"carbsGrams":10,"fatGrams":4,"confidence":85,"notes":"n"}],
             "overallConfidence":250,"requiresClarification":true,"clarificationQuestion":"How much oil?"}
            """);
        Assert.Equal(120, r.Items[0].Name.Length);
        Assert.Equal(0.85m, r.Items[0].Confidence);
        Assert.Equal(1m, r.OverallConfidence);
        Assert.Equal("How much oil?", r.ClarificationQuestion);
    }

    [Fact]
    public void Schema_HasRequiredFields()
    {
        var s = FoodVisionPrompt.Schema(upperCaseTypes: true);
        Assert.Equal("OBJECT", s["type"]!.GetValue<string>());
        var required = s["properties"]!["items"]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("calories", required);
        Assert.Contains("confidence", required);
    }

    [Fact]
    public void UserContext_IsWrappedAsData() =>
        Assert.Contains("<notes>\nfried in butter\n</notes>", FoodVisionPrompt.UserMessage("  fried in butter "));
}

public class MealIdeasTests
{
    [Fact]
    public void ParseMeals_RecomputesTotals_AndDropsUnusableMeals()
    {
        var meals = FoodAnalysisParser.ParseMeals("""
            {"meals":[
              {"name":"Tuna salad","totalCalories":9999,"items":[
                {"name":"Tuna in water","portionDescription":"1 can","estimatedWeightGrams":120,"calories":130,"proteinGrams":29,"carbsGrams":0,"fatGrams":1,"confidence":0.9,"notes":""},
                {"name":"Olive oil","portionDescription":"1 tsp","estimatedWeightGrams":5,"calories":40,"proteinGrams":0,"carbsGrams":0,"fatGrams":4.5,"confidence":0.9,"notes":""}]},
              {"name":"Broken","items":[{"name":"x","portionDescription":"1","calories":-5,"proteinGrams":1,"carbsGrams":1,"fatGrams":1,"confidence":0.5,"notes":""}]},
              {"name":"Empty","items":[]}
            ]}
            """);
        var m = Assert.Single(meals);
        Assert.Equal("Tuna salad", m.Name);
        Assert.Equal(170m, m.TotalCalories); // not 9999
        Assert.Equal(29m, m.TotalProteinGrams);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"meals\":[]}")]
    [InlineData("nope")]
    public void ParseMeals_RejectsNothingUsable(string text) =>
        Assert.Equal(FoodVisionErrorKind.InvalidResponse, Assert.Throws<FoodVisionException>(() => FoodAnalysisParser.ParseMeals(text)).Kind);

    private static MealIdea Meal(decimal kcal, decimal protein) =>
        new("M", [new FoodItemAnalysis("x", "1", null, null, kcal, protein, 0, 0, null, null, null, 0.9m, null, [])]);

    [Theory]
    [InlineData(480, 46, true, null)]
    [InlineData(520, 41, true, null)]               // exactly the budget; 41 ≥ 90% of 45
    [InlineData(530, 45, true, null)]               // 10 kcal over is within the 3% rounding allowance
    [InlineData(390, 36, false, "9 g protein short")]
    [InlineData(600, 50, false, "80 kcal over")]
    [InlineData(600, 30, false, "80 kcal over, 15 g protein short")]
    public void Fit_UsesRemainingCaloriesAndProtein(decimal kcal, decimal protein, bool fits, string? note)
    {
        var (f, n) = DashboardService.MealIdeaFit(Meal(kcal, protein), new RemainingAmount(2000, 1480), new RemainingAmount(140, 95));
        Assert.Equal(fits, f);
        Assert.Equal(note, n);
    }

    [Theory]
    [InlineData("", "Breakfast", 3)]          // nothing logged: breakfast gets a third
    [InlineData("Breakfast", "Lunch", 2)]     // lunch and dinner left
    [InlineData("Breakfast,Lunch", "Dinner", 1)] // last meal gets everything
    [InlineData("", "Snack", 4)]              // a snack is one extra share
    [InlineData("Breakfast,Lunch,Dinner", "Snack", 1)]
    [InlineData("Breakfast", "Breakfast", 3)] // a second breakfast still leaves lunch and dinner
    public void MealBudget_SplitsWhatsLeftAcrossMealsStillToEat(string logged, string chosen, int shares)
    {
        var loggedSet = logged.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<CalorieTracker.Web.Models.MealType>).ToHashSet();
        var meals = Enum.GetValues<CalorieTracker.Web.Models.MealType>().Select(t => new MealSummary(t, NutrientTotals.Zero, loggedSet.Contains(t) ? ["food"] : [])).ToList();
        var b = DashboardService.MealBudget(Enum.Parse<CalorieTracker.Web.Models.MealType>(chosen), meals, new RemainingAmount(2400, 0), new RemainingAmount(150, 30), null, null);
        Assert.Equal(shares, b.MealsLeft);
        Assert.Equal(2400m / shares, b.Calories);
        Assert.Equal(120m / shares, b.Protein);
    }

    private static RecentMealCandidate Past(string name, decimal kcal, decimal protein) =>
        new(name, "Logged", 1, null, new NutrientTotals(kcal, protein, 0, 0));

    [Fact]
    public void RecentMeals_FitAsLoggedFirst_ThenScaled_ThenCloseMisses()
    {
        var ranked = DashboardService.RankRecentMeals(
        [
            Past("Small breakfast", 400, 28),   // short as logged, fits at 1.5× (600 kcal, 42 g)
            Past("Chicken rice bowl", 590, 45), // fits as logged
            Past("Salmon dinner", 680, 46),     // 60 kcal over at every portion that keeps protein: near miss
            Past("Pizza night", 2000, 20),      // nowhere close: left out
        ], budgetKcal: 620, budgetProtein: 44);

        Assert.Equal(["Chicken rice bowl", "Small breakfast", "Salmon dinner"], ranked.Select(r => r.Meal.Name));
        Assert.Equal(1m, ranked[0].Factor);
        Assert.True(ranked[1].Fits);
        Assert.Equal(1.5m, ranked[1].Factor);
        Assert.Equal(600m, ranked[1].Scaled.Calories);
        Assert.False(ranked[2].Fits);
        Assert.Equal("60 kcal over", ranked[2].Note);
    }

    [Fact]
    public void RecentMeals_ShrinkBigMeals_AndCapAtThree()
    {
        var ranked = DashboardService.RankRecentMeals(
            Enumerable.Range(1, 6).Select(i => Past($"Big {i}", 1100 + i, 90)), budgetKcal: 600, budgetProtein: 40);
        Assert.Equal(3, ranked.Count);
        Assert.All(ranked, r => { Assert.True(r.Fits); Assert.Equal(0.5m, r.Factor); });
    }

    [Fact]
    public void DefaultMeal_IsFirstUnloggedMainMeal_ElseSnack()
    {
        MealSummary S(CalorieTracker.Web.Models.MealType t, bool logged) => new(t, NutrientTotals.Zero, logged ? ["food"] : []);
        Assert.Equal(CalorieTracker.Web.Models.MealType.Lunch, DashboardService.DefaultIdeaMeal([S(CalorieTracker.Web.Models.MealType.Breakfast, true), S(CalorieTracker.Web.Models.MealType.Lunch, false), S(CalorieTracker.Web.Models.MealType.Dinner, false)]));
        Assert.Equal(CalorieTracker.Web.Models.MealType.Snack, DashboardService.DefaultIdeaMeal([S(CalorieTracker.Web.Models.MealType.Breakfast, true), S(CalorieTracker.Web.Models.MealType.Lunch, true), S(CalorieTracker.Web.Models.MealType.Dinner, true)]));
    }
}

public class FoodVisionServiceTests
{
    private sealed class FakeProvider(string name, bool configured, Func<string> run) : IFoodVisionProvider
    {
        public int Calls;
        public (byte[]? Image, string Prompt, string Text) Last;
        public string Name => name;
        public string Model => "m";
        public bool IsConfigured => configured;
        public Task<string?> GenerateJsonAsync(byte[]? image, string? mimeType, string systemPrompt, string userText, Func<bool, JsonObject> schema, CancellationToken ct)
        {
            Calls++;
            Last = (image, systemPrompt, userText);
            return Task.FromResult<string?>(run());
        }
    }

    private const string Ok = """{"items":[{"name":"Egg","portionDescription":"1","calories":78,"proteinGrams":6,"carbsGrams":0.6,"fatGrams":5,"confidence":0.9,"notes":""}],"overallConfidence":0.9,"requiresClarification":false}""";

    [Fact]
    public async Task NotConfigured_ThrowsClearError()
    {
        var svc = new FoodVisionService([], new AiOptions(), NullLogger<FoodVisionService>.Instance);
        Assert.False(svc.IsConfigured);
        var ex = await Assert.ThrowsAsync<FoodVisionException>(() => svc.AnalyzeAsync([1], "image/jpeg", null, default));
        Assert.Equal(FoodVisionErrorKind.NotConfigured, ex.Kind);
    }

    [Fact]
    public async Task FallsBack_WhenPrimaryQuotaExhausted()
    {
        var gemini = new FakeProvider("gemini", true, () => throw new FoodVisionException(FoodVisionErrorKind.QuotaExceeded, "quota"));
        var ollama = new FakeProvider("ollama", true, () => Ok);
        var svc = new FoodVisionService([ollama, gemini], new AiOptions { Provider = "gemini" }, NullLogger<FoodVisionService>.Instance);
        var outcome = await svc.AnalyzeAsync([1], "image/jpeg", null, default);
        Assert.Equal("ollama", outcome.Provider);
        Assert.Equal(1, gemini.Calls); // selected provider was tried first
    }

    [Fact]
    public async Task TextSearch_SendsNoImage_AndUsesSearchPrompt()
    {
        var gemini = new FakeProvider("gemini", true, () => Ok);
        var svc = new FoodVisionService([gemini], new AiOptions(), NullLogger<FoodVisionService>.Instance);
        var outcome = await svc.EstimateFromTextAsync("2 boiled eggs", default);
        Assert.Single(outcome.Result.Items);
        Assert.Null(gemini.Last.Image);
        Assert.Equal(FoodVisionPrompt.TextSearchSystem, gemini.Last.Prompt);
        Assert.Contains("<search>\n2 boiled eggs\n</search>", gemini.Last.Text);
    }

    [Fact]
    public async Task MealIdeas_SendOnlyRemainingNumbersAndPreferences()
    {
        var gemini = new FakeProvider("gemini", true, () => """
            {"meals":[{"name":"Chicken rice bowl","items":[
              {"name":"Chicken breast","portionDescription":"150 g","estimatedWeightGrams":150,"calories":248,"proteinGrams":46,"carbsGrams":0,"fatGrams":5.4,"confidence":0.9,"notes":""},
              {"name":"Cooked rice","portionDescription":"150 g","estimatedWeightGrams":150,"calories":195,"proteinGrams":4,"carbsGrams":43,"fatGrams":0.4,"confidence":0.9,"notes":""}]}]}
            """);
        var svc = new FoodVisionService([gemini], new AiOptions(), NullLogger<FoodVisionService>.Instance);
        var outcome = await svc.SuggestMealsAsync(new MealIdeasRequest(CalorieTracker.Web.Models.MealType.Dinner, 520.4m, 45, 62, 18, "no pork  "), default);

        var meal = Assert.Single(outcome.Meals);
        Assert.Equal(443m, meal.TotalCalories);
        Assert.Equal(50m, meal.TotalProteinGrams);
        Assert.Equal(FoodVisionPrompt.MealIdeasSystem, gemini.Last.Prompt);
        Assert.Contains("Suggest 3 dinner ideas", gemini.Last.Text);
        Assert.Contains("Budget for this meal: 520 kcal, 45 g protein, 62 g carbs, 18 g fat", gemini.Last.Text);
        Assert.Contains("<preferences>\nno pork\n</preferences>", gemini.Last.Text);
    }

    [Fact]
    public void TextSearchMessage_TruncatesLongQueries() =>
        Assert.True(FoodVisionPrompt.TextSearchMessage(new string('a', 500)).Length < 200);

    [Fact]
    public async Task DoesNotFallBack_OnInvalidResponse()
    {
        var gemini = new FakeProvider("gemini", true, () => "this is not json");
        var ollama = new FakeProvider("ollama", true, () => Ok);
        var svc = new FoodVisionService([gemini, ollama], new AiOptions { Provider = "gemini" }, NullLogger<FoodVisionService>.Instance);
        await Assert.ThrowsAsync<FoodVisionException>(() => svc.AnalyzeAsync([1], "image/jpeg", null, default));
        Assert.Equal(0, ollama.Calls);
    }
}

public class ImageUploadValidatorTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static IFormFile File(byte[] bytes, string name, string type) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photo", name) { Headers = new HeaderDictionary(), ContentType = type };

    [Fact]
    public async Task Accepts_RealJpeg()
    {
        var (img, err) = await ImageUploadValidator.ValidateAsync(File(Jpeg, "meal.jpg", "image/jpeg"), default);
        Assert.Null(err);
        Assert.Equal("image/jpeg", img!.MimeType);
    }

    [Theory]
    [InlineData("meal.gif", "image/gif")]          // unsupported extension
    [InlineData("meal.png", "image/jpeg")]         // content-type mismatch
    [InlineData("meal.exe", "application/octet-stream")]
    public async Task Rejects_BadExtensionOrType(string name, string type)
    {
        var (img, err) = await ImageUploadValidator.ValidateAsync(File(Jpeg, name, type), default);
        Assert.Null(img);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task Rejects_SpoofedContent()
    {
        // Named and typed as PNG, but the bytes are a script.
        var (img, err) = await ImageUploadValidator.ValidateAsync(File("<script>alert(1)</script>"u8.ToArray(), "x.png", "image/png"), default);
        Assert.Null(img);
        Assert.Contains("isn't a valid image", err);
    }

    [Fact]
    public async Task Rejects_Empty_AndOversized()
    {
        Assert.NotNull((await ImageUploadValidator.ValidateAsync(null, default)).Error);
        var big = new FormFile(new MemoryStream(Png), 0, ImageUploadValidator.MaxBytes + 1, "photo", "x.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        Assert.Contains("too large", (await ImageUploadValidator.ValidateAsync(big, default)).Error);
    }
}

public class UsdaParserTests
{
    [Fact]
    public void Parses_SearchResultShape()
    {
        var food = UsdaFoodNutritionService.ParseFood(JsonNode.Parse("""
            {"fdcId":171477,"description":"Chicken, broilers or fryers, breast, meat only, cooked, roasted","dataType":"SR Legacy",
             "foodNutrients":[
               {"nutrientId":1003,"nutrientNumber":"203","unitName":"G","value":31.0},
               {"nutrientId":1004,"nutrientNumber":"204","unitName":"G","value":3.57},
               {"nutrientId":1005,"nutrientNumber":"205","unitName":"G","value":0.0},
               {"nutrientId":1062,"nutrientNumber":"268","unitName":"KJ","value":690},
               {"nutrientId":1008,"nutrientNumber":"208","unitName":"KCAL","value":165},
               {"nutrientId":1093,"nutrientNumber":"307","unitName":"MG","value":74}
             ]}
            """)!.AsObject())!;
        Assert.Equal("171477", food.FdcId);
        Assert.Equal(165m, food.Calories);
        Assert.Equal(31m, food.ProteinGrams);
        Assert.Equal(3.57m, food.FatGrams);
        Assert.Equal(74m, food.SodiumMilligrams);
        Assert.Null(food.FiberGrams);
    }

    [Fact]
    public void Parses_AbridgedShape_WithAtwaterEnergyFallback()
    {
        var food = UsdaFoodNutritionService.ParseFood(JsonNode.Parse("""
            {"fdcId":"2646170","description":"Egg, whole, raw","dataType":"Foundation",
             "foodNutrients":[{"number":"958","name":"Energy (Atwater General Factors)","amount":143,"unitName":"KCAL"},
                              {"number":"203","name":"Protein","amount":12.4,"unitName":"G"}]}
            """)!.AsObject())!;
        Assert.Equal(143m, food.Calories);
        Assert.Equal(12.4m, food.ProteinGrams);
    }

    [Fact]
    public void Skips_FoodsWithoutEnergy() =>
        Assert.Null(UsdaFoodNutritionService.ParseFood(JsonNode.Parse("""{"fdcId":1,"description":"Water","foodNutrients":[]}""")!.AsObject()));
}

public class InfrastructureTests
{
    [Fact]
    public void ConvertsNeonUrl_ToNpgsqlConnectionString()
    {
        var cs = PostgresConnectionString.Normalize("postgresql://app_user:p%40ss@ep-cool-name-123.us-east-2.aws.neon.tech/neondb?sslmode=require&channel_binding=require")!;
        Assert.Contains("Host=ep-cool-name-123.us-east-2.aws.neon.tech", cs);
        Assert.Contains("Database=neondb", cs);
        Assert.Contains("Username=app_user", cs);
        Assert.Contains("Password=p@ss", cs);
        Assert.Contains("SSL Mode=Require", cs);
        Assert.Contains("Channel Binding=Require", cs);
    }

    [Fact]
    public void KeyValueConnectionString_PassesThrough() =>
        Assert.Equal("Host=x;Database=y", PostgresConnectionString.Normalize("Host=x;Database=y"));

    [Fact]
    public void TodayIn_UsesUserTimeZone()
    {
        var utc = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2025, 12, 31), NutritionPlanService.TodayIn("America/New_York", utc));
        Assert.Equal(new DateOnly(2026, 1, 1), NutritionPlanService.TodayIn("Asia/Tokyo", utc));
        Assert.Equal(new DateOnly(2026, 1, 1), NutritionPlanService.TodayIn("Not/AZone", utc));
    }
}
