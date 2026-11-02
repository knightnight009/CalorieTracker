using System.Net;
using System.Text.RegularExpressions;
using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using CalorieTracker.Web.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace CalorieTracker.Tests;

/// <summary>
/// Runs the real app against a throwaway PostgreSQL database built from the EF migrations.
/// Set TEST_DATABASE_CONNECTION to point elsewhere (CI uses a postgres service container).
/// </summary>
public class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Integration-Test-Pass-1";
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=calorietracker_test;Username=calorie;Password=devpassword";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("AUTH_POSTS_PER_MINUTE", "1000");
        // Make sure no developer AI/USDA keys leak into tests.
        builder.UseSetting("AI_API_KEY", "");
        builder.UseSetting("OLLAMA_BASE_URL", "");
        builder.UseSetting("USDA_API_KEY", "");
    }

    public async Task InitializeAsync()
    {
        // Clean database every run: this also proves the migrations apply to an empty database.
        // Uses the app's own DbContext registration: Identity's schema version comes from app services.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public async Task<string> CreateUserAsync(string email)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    public async Task<HttpClient> LoginAsync(string email, string password = Password)
    {
        var client = NewClient();
        var res = await PostFormAsync(client, "/Identity/Account/Login", "/Identity/Account/Login",
            new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return client;
    }

    /// <summary>Loads a page to obtain this user's antiforgery token, then posts the form like a browser would.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string tokenPage, string url, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(tokenPage);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"No antiforgery token on {tokenPage}");
        fields["__RequestVerificationToken"] = token;
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}

public class IntegrationTests(AppFactory app) : IClassFixture<AppFactory>
{
    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.test";

    private record Seeded(int FoodId, int EntryId, int ItemId, int WeightId, int ScanId);

    private Task<Seeded> SeedAsync(string userId, string foodName) => app.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var food = new Food { UserId = userId, Name = foodName, ServingDescription = "1 serving", Calories = 300, ProteinGrams = 20, CarbohydratesGrams = 30, FatGrams = 10, DataSource = NutritionSource.CustomFood, IsCustomFood = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        var entry = new FoodDiaryEntry
        {
            UserId = userId, DiaryDate = new DateOnly(2026, 3, 1), MealType = MealType.Lunch, CreatedAtUtc = now, UpdatedAtUtc = now,
            Items = [new FoodDiaryItem { Food = food, FoodNameSnapshot = foodName, Quantity = 1, Calories = 300, ProteinGrams = 20, CarbohydratesGrams = 30, FatGrams = 10, NutritionSource = NutritionSource.CustomFood }],
        };
        var weight = new WeightLog { UserId = userId, Date = new DateOnly(2026, 3, 1), WeightKg = 70, CreatedAtUtc = now };
        var scan = new FoodScan { UserId = userId, CreatedAtUtc = now, Provider = "gemini", Model = "m", DiaryDate = new DateOnly(2026, 3, 1), Status = FoodScanStatus.Analyzed, ResultJson = """{"Items":[],"TotalCalories":0,"TotalProteinGrams":0,"TotalCarbsGrams":0,"TotalFatGrams":0,"OverallConfidence":0,"RequiresClarification":true,"ClarificationQuestion":"x","Warnings":[]}""" };
        db.AddRange(food, entry, weight, scan);
        await db.SaveChangesAsync();
        return new Seeded(food.Id, entry.Id, entry.Items[0].Id, weight.Id, scan.Id);
    });

    [Fact]
    public async Task PrivatePages_RequireLogin()
    {
        var client = app.NewClient();
        foreach (var url in new[] { "/Dashboard", "/FoodDiary", "/FoodScanner", "/NutritionGoals", "/Progress", "/Profile" })
        {
            var res = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Contains("/Identity/Account/Login", res.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task HealthChecks_AreMinimal()
    {
        var client = app.NewClient();
        var live = await client.GetAsync("/health");
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_SignsInImmediately_WithoutEmailConfirmation()
    {
        var client = app.NewClient();
        var email = Email("signup");
        var res = await AppFactory.PostFormAsync(client, "/Identity/Account/Register", "/Identity/Account/Register", new()
        {
            ["Input.Email"] = email, ["Input.Password"] = AppFactory.Password, ["Input.ConfirmPassword"] = AppFactory.Password,
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.DoesNotContain("RegisterConfirmation", res.Headers.Location!.ToString());

        // Already signed in: private pages work without a separate login.
        Assert.Contains("/Profile/Setup", (await client.GetAsync("/Dashboard")).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_WrongPassword_LocksAccountAfterFiveAttempts()
    {
        var email = Email("lockout");
        await app.CreateUserAsync(email);
        var client = app.NewClient();
        HttpResponseMessage res = null!;
        for (var i = 0; i < 5; i++)
            res = await AppFactory.PostFormAsync(client, "/Identity/Account/Login", "/Identity/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "wrong-password-123" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("Lockout", res.Headers.Location!.ToString());

        // Even the correct password is refused while locked out.
        res = await AppFactory.PostFormAsync(client, "/Identity/Account/Login", "/Identity/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = AppFactory.Password });
        Assert.Contains("Lockout", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task UserA_CannotReadModifyOrDelete_UserBsRecords()
    {
        var emailA = Email("a");
        await app.CreateUserAsync(emailA);
        var userB = await app.CreateUserAsync(Email("b"));
        var b = await SeedAsync(userB, "Bs secret lasagna");
        var a = await app.LoginAsync(emailA);

        // Read
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/FoodDiary/EditItem/{b.ItemId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/FoodDiary/FoodForm/{b.FoodId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/FoodScanner/Review/{b.ScanId}")).StatusCode);
        Assert.DoesNotContain("Bs secret lasagna", await a.GetStringAsync("/FoodDiary?date=2026-03-01"));
        Assert.DoesNotContain("Bs secret lasagna", await a.GetStringAsync("/FoodDiary/Foods"));
        Assert.DoesNotContain("Bs secret lasagna", await a.GetStringAsync("/FoodDiary/Add?date=2026-03-01&q=lasagna"));

        // Modify / delete by changing ids in the URL or form
        const string tokenPage = "/FoodDiary/Foods";
        var attempts = new (string Url, Dictionary<string, string> Form)[]
        {
            ($"/FoodDiary/DeleteItem/{b.ItemId}", new()),
            ($"/FoodDiary/DeleteEntry/{b.EntryId}", new()),
            ("/FoodDiary/EditItem", new() { ["Id"] = b.ItemId.ToString(), ["Name"] = "hacked", ["Quantity"] = "1", ["RescaleFromQuantity"] = "true" }),
            ("/FoodDiary/FoodForm", new() { ["Id"] = b.FoodId.ToString(), ["Name"] = "hacked", ["ServingDescription"] = "x", ["Nutrients.Calories"] = "1", ["Nutrients.ProteinGrams"] = "1", ["Nutrients.CarbohydratesGrams"] = "1", ["Nutrients.FatGrams"] = "1" }),
            ($"/FoodDiary/DeleteFood/{b.FoodId}", new()),
            ("/FoodDiary/AddFromFood", new() { ["FoodId"] = b.FoodId.ToString(), ["Quantity"] = "1", ["Date"] = "2026-03-02", ["MealType"] = "Lunch", ["ClientRequestId"] = Guid.NewGuid().ToString() }),
            ("/FoodDiary/AddRecent", new() { ["DiaryItemId"] = b.ItemId.ToString(), ["Date"] = "2026-03-02", ["MealType"] = "Lunch", ["ClientRequestId"] = Guid.NewGuid().ToString() }),
            ("/FoodDiary/CopyEntry", new() { ["EntryId"] = b.EntryId.ToString(), ["TargetDate"] = "2026-03-02", ["TargetMealType"] = "Lunch" }),
            ($"/Progress/DeleteWeight/{b.WeightId}", new()),
            ($"/FoodScanner/Delete/{b.ScanId}", new()),
            ("/FoodScanner/Confirm", new() { ["ScanId"] = b.ScanId.ToString(), ["Date"] = "2026-03-02", ["MealType"] = "Lunch", ["Items[0].Include"] = "true", ["Items[0].Name"] = "x", ["Items[0].Calories"] = "1", ["Items[0].ProteinGrams"] = "1", ["Items[0].CarbohydratesGrams"] = "1", ["Items[0].FatGrams"] = "1" }),
        };
        foreach (var (url, form) in attempts)
        {
            var res = await AppFactory.PostFormAsync(a, tokenPage, url, form);
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{url} returned {res.StatusCode}");
        }

        // B's data is untouched, and A gained nothing.
        await app.WithDbAsync(async db =>
        {
            Assert.Equal("Bs secret lasagna", (await db.FoodDiaryItems.SingleAsync(i => i.Id == b.ItemId)).FoodNameSnapshot);
            Assert.Equal("Bs secret lasagna", (await db.Foods.SingleAsync(f => f.Id == b.FoodId)).Name);
            Assert.True(await db.WeightLogs.AnyAsync(w => w.Id == b.WeightId));
            Assert.True(await db.FoodScans.AnyAsync(s => s.Id == b.ScanId));
            Assert.Equal(1, await db.FoodDiaryEntries.CountAsync(e => e.UserId == userB));
            return 0;
        });
    }

    [Fact]
    public async Task ManualEntry_DoubleSubmit_CreatesOneEntry_AndDayTotalsAddUp()
    {
        var email = Email("diary");
        var userId = await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);
        var requestId = Guid.NewGuid().ToString();
        Dictionary<string, string> Form(string name, string kcal, string id) => new()
        {
            ["Manual.Date"] = "2026-04-10", ["Manual.MealType"] = "Breakfast", ["Manual.ClientRequestId"] = id, ["Manual.Name"] = name,
            ["Manual.Nutrients.Calories"] = kcal, ["Manual.Nutrients.ProteinGrams"] = "20.5", ["Manual.Nutrients.CarbohydratesGrams"] = "30", ["Manual.Nutrients.FatGrams"] = "5",
        };

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, "/FoodDiary/Add?date=2026-04-10", "/FoodDiary/AddManual", Form("Oats", "250", requestId))).StatusCode);
        await AppFactory.PostFormAsync(client, "/FoodDiary/Add?date=2026-04-10", "/FoodDiary/AddManual", Form("Banana", "105", Guid.NewGuid().ToString()));

        var count = await app.WithDbAsync(db => db.FoodDiaryEntries.CountAsync(e => e.UserId == userId));
        Assert.Equal(2, count);
        var page = await client.GetStringAsync("/FoodDiary?date=2026-04-10");
        Assert.Contains("355 kcal", page); // 250 + 105
        Assert.Contains("P 41 g", page);   // 20.5 × 2
    }

    [Fact]
    public async Task EditingAFood_DoesNotChangeLoggedHistory_AndRescaleWorks()
    {
        var email = Email("snap");
        var userId = await app.CreateUserAsync(email);
        var seeded = await SeedAsync(userId, "Granola");
        var client = await app.LoginAsync(email);

        await AppFactory.PostFormAsync(client, "/FoodDiary/Foods", "/FoodDiary/FoodForm", new()
        {
            ["Id"] = seeded.FoodId.ToString(), ["Name"] = "Granola v2", ["ServingDescription"] = "1 serving",
            ["Nutrients.Calories"] = "999", ["Nutrients.ProteinGrams"] = "1", ["Nutrients.CarbohydratesGrams"] = "1", ["Nutrients.FatGrams"] = "1",
        });
        var item = await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId));
        Assert.Equal("Granola", item.FoodNameSnapshot);
        Assert.Equal(300m, item.Calories);

        // Double the serving with server-side rescaling.
        await AppFactory.PostFormAsync(client, $"/FoodDiary/EditItem/{seeded.ItemId}", "/FoodDiary/EditItem", new()
        {
            ["Id"] = seeded.ItemId.ToString(), ["Name"] = "Granola", ["Quantity"] = "2", ["RescaleFromQuantity"] = "true", ["MealType"] = "Lunch",
        });
        item = await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId));
        Assert.Equal(600m, item.Calories);
        Assert.Equal(40m, item.ProteinGrams);

        // Give it a weight (no rescale: there was none to compare with), then change only the weight.
        await AppFactory.PostFormAsync(client, $"/FoodDiary/EditItem/{seeded.ItemId}", "/FoodDiary/EditItem", new()
        {
            ["Id"] = seeded.ItemId.ToString(), ["Name"] = "Granola", ["Quantity"] = "2", ["WeightGrams"] = "100", ["RescaleFromQuantity"] = "true", ["MealType"] = "Lunch",
        });
        Assert.Equal(600m, (await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId))).Calories);

        await AppFactory.PostFormAsync(client, $"/FoodDiary/EditItem/{seeded.ItemId}", "/FoodDiary/EditItem", new()
        {
            ["Id"] = seeded.ItemId.ToString(), ["Name"] = "Granola", ["Quantity"] = "2", ["WeightGrams"] = "150", ["RescaleFromQuantity"] = "true", ["MealType"] = "Lunch",
        });
        item = await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId));
        Assert.Equal(900m, item.Calories);   // 600 × 150/100
        Assert.Equal(60m, item.ProteinGrams);
        Assert.Equal(3m, item.Quantity);     // servings follow the weight
        Assert.Equal(150m, item.WeightGrams);

        // Changing servings moves the weight along.
        await AppFactory.PostFormAsync(client, $"/FoodDiary/EditItem/{seeded.ItemId}", "/FoodDiary/EditItem", new()
        {
            ["Id"] = seeded.ItemId.ToString(), ["Name"] = "Granola", ["Quantity"] = "1", ["WeightGrams"] = "150", ["RescaleFromQuantity"] = "true", ["MealType"] = "Lunch",
        });
        item = await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemId));
        Assert.Equal(300m, item.Calories);
        Assert.Equal(50m, item.WeightGrams);
    }

    [Fact]
    public async Task Scanner_WithoutAiProvider_ShowsSetupAndDoesNotFail()
    {
        var email = Email("noai");
        await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);
        Assert.Contains("isn't set up yet", await client.GetStringAsync("/FoodScanner"));

        var html = await client.GetStringAsync("/FoodScanner");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("2026-04-10"), "date" },
            { new StringContent("Lunch"), "mealType" },
            { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]) { Headers = { ContentType = new("image/jpeg") } }, "photo", "meal.jpg" },
        };
        var res = await client.PostAsync("/FoodScanner/Analyze", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("isn't set up yet", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProfileSetup_ComputesTargets_AndDashboardShowsRemaining()
    {
        var email = Email("profile");
        var userId = await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);
        Assert.Contains("/Profile/Setup", (await client.GetAsync("/Dashboard")).Headers.Location!.ToString());

        var res = await AppFactory.PostFormAsync(client, "/Profile/Setup", "/Profile/Setup", new()
        {
            ["SexForBmrCalculation"] = "Male", ["DateOfBirth"] = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-36).AddDays(-2).ToString("yyyy-MM-dd"), ["CurrentWeight"] = "82", ["PreferredWeightUnit"] = "Kg",
            ["HeightUnit"] = "Cm", ["HeightCm"] = "180", ["ActivityLevel"] = "ModeratelyActive", ["WorkoutDaysPerWeek"] = "4",
            ["AverageWorkoutDurationMinutes"] = "60", ["WorkoutIntensity"] = "Moderate", ["PrimaryGoal"] = "LoseFat",
            ["PreferredCalorieDeficitPercentage"] = "15", ["PreferredCalorieSurplusPercentage"] = "10", ["ProteinTargetGramsPerKg"] = "1.8",
            ["FatTargetPercentage"] = "30", ["TimeZoneId"] = "UTC",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var goals = await client.GetStringAsync("/NutritionGoals");
        Assert.Contains("1,770", goals);   // BMR at age 36: 820 + 1125 - 180 + 5
        Assert.Contains("148 g", goals);   // protein
        var snapshots = await app.WithDbAsync(db => db.NutritionGoalSnapshots.CountAsync(s => s.UserId == userId));
        Assert.Equal(1, snapshots);

        var dash = await client.GetStringAsync("/Dashboard");
        Assert.Contains("kcal remaining", dash);
    }

    [Fact]
    public async Task ScanReview_EditedItemsAreSavedOnce_WithServerTotals()
    {
        var email = Email("scan");
        var userId = await app.CreateUserAsync(email);
        var analysis = CalorieTracker.Web.Services.Ai.FoodAnalysisParser.Parse("""
            {"items":[
              {"name":"White rice","portionDescription":"1 cup","estimatedWeightGrams":158,"calories":205,"proteinGrams":4.3,"carbsGrams":44.5,"fatGrams":0.4,"confidence":0.88,"notes":"<script>alert(1)</script>"},
              {"name":"Grilled chicken","portionDescription":"120 g","estimatedWeightGrams":120,"calories":198,"proteinGrams":37,"carbsGrams":0,"fatGrams":4.3,"confidence":0.76,"notes":"Oil may add calories."},
              {"name":"Cucumber","portionDescription":"5 slices","calories":8,"proteinGrams":0.3,"carbsGrams":1.8,"fatGrams":0.1,"confidence":0.9,"notes":""}
            ],"overallConfidence":0.82,"requiresClarification":false}
            """);
        var scanId = await app.WithDbAsync(async db =>
        {
            var scan = new FoodScan { UserId = userId, CreatedAtUtc = DateTime.UtcNow, Provider = "gemini", Model = "test", MealType = MealType.Dinner, DiaryDate = new DateOnly(2026, 5, 5), Status = FoodScanStatus.Analyzed, ResultJson = System.Text.Json.JsonSerializer.Serialize(analysis) };
            db.FoodScans.Add(scan);
            await db.SaveChangesAsync();
            return scan.Id;
        });
        var client = await app.LoginAsync(email);

        var review = await client.GetStringAsync($"/FoodScanner/Review/{scanId}");
        Assert.Contains("Grilled chicken", review);
        Assert.DoesNotContain("<script>alert(1)</script>", review); // AI text is HTML-encoded

        // User doubles the chicken, drops the cucumber, adds a missing sauce, and fakes nothing else.
        var form = new Dictionary<string, string>
        {
            ["ScanId"] = scanId.ToString(), ["Date"] = "2026-05-05", ["MealType"] = "Dinner",
            ["Items[0].OriginalIndex"] = "0", ["Items[0].Include"] = "true", ["Items[0].Name"] = "White rice", ["Items[0].Calories"] = "205", ["Items[0].ProteinGrams"] = "4.3", ["Items[0].CarbohydratesGrams"] = "44.5", ["Items[0].FatGrams"] = "0.4",
            ["Items[1].OriginalIndex"] = "1", ["Items[1].Include"] = "true", ["Items[1].Name"] = "Grilled chicken", ["Items[1].Calories"] = "396", ["Items[1].ProteinGrams"] = "74", ["Items[1].CarbohydratesGrams"] = "0", ["Items[1].FatGrams"] = "8.6",
            ["Items[2].OriginalIndex"] = "2", ["Items[2].Include"] = "false", ["Items[2].Name"] = "", ["Items[2].Calories"] = "8", ["Items[2].ProteinGrams"] = "0", ["Items[2].CarbohydratesGrams"] = "0", ["Items[2].FatGrams"] = "0",
            ["Items[3].OriginalIndex"] = "-1", ["Items[3].Include"] = "true", ["Items[3].Name"] = "Teriyaki sauce", ["Items[3].Calories"] = "45", ["Items[3].ProteinGrams"] = "1", ["Items[3].CarbohydratesGrams"] = "9", ["Items[3].FatGrams"] = "0",
        };
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, $"/FoodScanner/Review/{scanId}", "/FoodScanner/Confirm", new(form))).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var entry = await db.FoodDiaryEntries.Include(e => e.Items).SingleAsync(e => e.UserId == userId);
            Assert.Equal(3, entry.Items.Count);
            Assert.Equal(646m, entry.Items.Sum(i => i.Calories)); // 205 + 396 + 45
            Assert.True(entry.Items.Single(i => i.FoodNameSnapshot == "Grilled chicken").IsAiEstimated);
            Assert.Equal(0.76m, entry.Items.Single(i => i.FoodNameSnapshot == "Grilled chicken").AiConfidence);
            Assert.False(entry.Items.Single(i => i.FoodNameSnapshot == "Teriyaki sauce").IsAiEstimated);
            var scan = await db.FoodScans.SingleAsync(s => s.Id == scanId);
            Assert.Equal(FoodScanStatus.Saved, scan.Status);
            Assert.Equal(entry.Id, scan.FoodDiaryEntryId);
            return 0;
        });
    }

    [Fact]
    public async Task AddAiEstimate_ScalesByServings_AndIsLabelledAsAi()
    {
        var email = Email("aisearch");
        var userId = await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);

        // Without an AI provider the AI search page still renders (no crash, no fake results).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/FoodDiary/AiSearch?date=2026-06-01&meal=Lunch&q=egg")).StatusCode);

        var res = await AppFactory.PostFormAsync(client, "/FoodDiary/Add?date=2026-06-01", "/FoodDiary/AddAiEstimate", new()
        {
            ["Date"] = "2026-06-01", ["MealType"] = "Lunch", ["ClientRequestId"] = Guid.NewGuid().ToString(),
            ["Name"] = "Boiled egg", ["PortionDescription"] = "1 large", ["WeightGrams"] = "50", ["Confidence"] = "0.9", ["Quantity"] = "2",
            ["Nutrients.Calories"] = "78", ["Nutrients.ProteinGrams"] = "6.3", ["Nutrients.CarbohydratesGrams"] = "0.6", ["Nutrients.FatGrams"] = "5.3",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var item = await app.WithDbAsync(db => db.FoodDiaryItems.AsNoTracking().SingleAsync(i => i.Entry!.UserId == userId));
        Assert.Equal(156m, item.Calories);
        Assert.Equal(12.6m, item.ProteinGrams);
        Assert.Equal(100m, item.WeightGrams);
        Assert.Equal("2 × 1 large", item.PortionDescription);
        Assert.True(item.IsAiEstimated);
        Assert.Equal(NutritionSource.AiEstimate, item.NutritionSource);

        // Implausible posted values are rejected like manual entry.
        res = await AppFactory.PostFormAsync(client, "/FoodDiary/Add?date=2026-06-01", "/FoodDiary/AddAiEstimate", new()
        {
            ["Date"] = "2026-06-01", ["MealType"] = "Lunch", ["ClientRequestId"] = Guid.NewGuid().ToString(), ["Name"] = "Bad", ["Quantity"] = "1",
            ["Nutrients.Calories"] = "-5", ["Nutrients.ProteinGrams"] = "1", ["Nutrients.CarbohydratesGrams"] = "1", ["Nutrients.FatGrams"] = "1",
        });
        Assert.Equal(1, await app.WithDbAsync(db => db.FoodDiaryItems.CountAsync(i => i.Entry!.UserId == userId)));
    }

    [Fact]
    public async Task ManualEntry_InvalidValues_ReshowsTheForm()
    {
        var email = Email("reshow");
        await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);
        var res = await AppFactory.PostFormAsync(client, "/FoodDiary/Add?date=2026-06-01", "/FoodDiary/AddManual", new()
        {
            ["Manual.Date"] = "2026-06-01", ["Manual.MealType"] = "Lunch", ["Manual.ClientRequestId"] = Guid.NewGuid().ToString(), ["Manual.Name"] = "",
            ["Manual.Nutrients.Calories"] = "100", ["Manual.Nutrients.ProteinGrams"] = "1", ["Manual.Nutrients.CarbohydratesGrams"] = "1", ["Manual.Nutrients.FatGrams"] = "1",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Add food", await res.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, string> MealIdeaForm(string date) => new()
    {
        ["Date"] = date, ["MealType"] = "Dinner", ["ClientRequestId"] = Guid.NewGuid().ToString(), ["MealName"] = "Chicken rice bowl",
        ["Items[0].Include"] = "true", ["Items[0].Name"] = "Chicken breast", ["Items[0].PortionDescription"] = "150 g", ["Items[0].WeightGrams"] = "150",
        ["Items[0].Calories"] = "248", ["Items[0].ProteinGrams"] = "46", ["Items[0].CarbohydratesGrams"] = "0", ["Items[0].FatGrams"] = "5.4", ["Items[0].Confidence"] = "0.9",
        ["Items[1].Include"] = "true", ["Items[1].Name"] = "Cooked rice", ["Items[1].PortionDescription"] = "150 g", ["Items[1].WeightGrams"] = "150",
        ["Items[1].Calories"] = "195", ["Items[1].ProteinGrams"] = "4", ["Items[1].CarbohydratesGrams"] = "43", ["Items[1].FatGrams"] = "0.4", ["Items[1].Confidence"] = "0.85",
    };

    [Fact]
    public async Task MealIdea_AddLogsOneEntryWithEachFood_AndReviewOpensEditableScan()
    {
        var email = Email("ideas");
        var userId = await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);

        var form = MealIdeaForm("2026-11-01");
        for (var i = 0; i < 2; i++) // double click
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, "/FoodDiary/Foods", "/FoodDiary/AddMealIdea", new(form))).StatusCode);

        var entry = await app.WithDbAsync(db => db.FoodDiaryEntries.Include(e => e.Items).SingleAsync(e => e.UserId == userId));
        Assert.Equal("Chicken rice bowl", entry.Notes);
        Assert.Equal(MealType.Dinner, entry.MealType);
        Assert.Equal(2, entry.Items.Count);
        Assert.Equal(443m, entry.Items.Sum(i => i.Calories));
        Assert.All(entry.Items, i => Assert.True(i.IsAiEstimated));

        var res = await AppFactory.PostFormAsync(client, "/FoodDiary/Foods", "/FoodScanner/ReviewMealIdea", MealIdeaForm("2026-11-01"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var review = await client.GetStringAsync(res.Headers.Location!.ToString());
        Assert.Contains("Cooked rice", review);
        Assert.Contains("Chicken rice bowl", review); // meal name prefilled as the note
    }

    [Fact]
    public async Task Dashboard_MealIdeasWithoutAi_ExplainsInsteadOfFailing()
    {
        var email = Email("ideas-noai");
        await app.CreateUserAsync(email);
        var client = await app.LoginAsync(email);
        await AppFactory.PostFormAsync(client, "/Profile/Setup", "/Profile/Setup", new()
        {
            ["SexForBmrCalculation"] = "Female", ["DateOfBirth"] = "1995-01-01", ["CurrentWeight"] = "65", ["PreferredWeightUnit"] = "Kg",
            ["HeightUnit"] = "Cm", ["HeightCm"] = "168", ["ActivityLevel"] = "LightlyActive", ["WorkoutDaysPerWeek"] = "3",
            ["AverageWorkoutDurationMinutes"] = "45", ["WorkoutIntensity"] = "Moderate", ["PrimaryGoal"] = "Maintain",
            ["PreferredCalorieDeficitPercentage"] = "15", ["PreferredCalorieSurplusPercentage"] = "10", ["ProteinTargetGramsPerKg"] = "1.8",
            ["FatTargetPercentage"] = "30", ["TimeZoneId"] = "UTC",
        });
        Assert.Contains("Meal ideas for what's left", await client.GetStringAsync("/Dashboard"));
        var res = await AppFactory.PostFormAsync(client, "/Dashboard", "/Dashboard/MealIdeas", new() { ["mealType"] = "Dinner" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("AI_API_KEY", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RecentMeals_ShowOnDashboard_AndAddAgainScalesOwnMealsOnly()
    {
        var email = Email("recent");
        var userId = await app.CreateUserAsync(email);
        var otherId = await app.CreateUserAsync(Email("recent-other"));
        var client = await app.LoginAsync(email);
        await AppFactory.PostFormAsync(client, "/Profile/Setup", "/Profile/Setup", new()
        {
            ["SexForBmrCalculation"] = "Male", ["DateOfBirth"] = "1990-01-01", ["CurrentWeight"] = "80", ["PreferredWeightUnit"] = "Kg",
            ["HeightUnit"] = "Cm", ["HeightCm"] = "180", ["ActivityLevel"] = "ModeratelyActive", ["WorkoutDaysPerWeek"] = "3",
            ["AverageWorkoutDurationMinutes"] = "45", ["WorkoutIntensity"] = "Moderate", ["PrimaryGoal"] = "Maintain",
            ["PreferredCalorieDeficitPercentage"] = "15", ["PreferredCalorieSurplusPercentage"] = "10", ["ProteinTargetGramsPerKg"] = "1.8",
            ["FatTargetPercentage"] = "30", ["TimeZoneId"] = "UTC",
        });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (mine, saved, theirs) = await app.WithDbAsync(async db =>
        {
            FoodDiaryEntry Entry(string uid, string name, decimal kcal, decimal p) => new()
            {
                UserId = uid, DiaryDate = today.AddDays(-1), MealType = MealType.Dinner, Notes = name, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
                Items = [new FoodDiaryItem { FoodNameSnapshot = name + " food", Quantity = 1, PortionDescription = "1 plate", WeightGrams = 300, Calories = kcal, ProteinGrams = p, CarbohydratesGrams = 50, FatGrams = 10, NutritionSource = NutritionSource.Manual }],
            };
            var a = Entry(userId, "Yesterday's chicken bowl", 600, 50);
            var b = Entry(otherId, "Someone else's dinner", 600, 50);
            var m = new SavedMeal { UserId = userId, Name = "Usual oats", CreatedAtUtc = DateTime.UtcNow, Items = [new SavedMealItem { FoodNameSnapshot = "Oats", Quantity = 1, Calories = 300, ProteinGrams = 20, CarbohydratesGrams = 40, FatGrams = 6, NutritionSource = NutritionSource.Manual }] };
            db.AddRange(a, b, m);
            await db.SaveChangesAsync();
            return (a.Id, m.Id, b.Id);
        });

        var dash = await client.GetStringAsync("/Dashboard?meal=Dinner");
        Assert.Contains("From your recent meals", dash);
        Assert.Contains("Yesterday&#x27;s chicken bowl", dash);
        Assert.DoesNotContain("Someone else", dash);

        Dictionary<string, string> Form(string idField, int id, string factor) => new()
        {
            ["Date"] = today.ToString("yyyy-MM-dd"), ["MealType"] = "Dinner", ["ClientRequestId"] = Guid.NewGuid().ToString(), [idField] = id.ToString(), ["Factor"] = factor,
        };
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, "/Dashboard", "/FoodDiary/AddRecentMeal", Form("EntryId", mine, "1.5"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, "/Dashboard", "/FoodDiary/AddRecentMeal", Form("SavedMealId", saved, "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AppFactory.PostFormAsync(client, "/Dashboard", "/FoodDiary/AddRecentMeal", Form("EntryId", theirs, "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AppFactory.PostFormAsync(client, "/Dashboard", "/FoodDiary/AddRecentMeal", Form("EntryId", mine, "3"))).StatusCode);

        var added = await app.WithDbAsync(db => db.FoodDiaryEntries.Include(e => e.Items).Where(e => e.UserId == userId && e.DiaryDate == today).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(2, added.Count);
        Assert.Equal("Yesterday's chicken bowl (1.5×)", added[0].Notes);
        Assert.Equal(900m, added[0].Items.Single().Calories);
        Assert.Equal(450m, added[0].Items.Single().WeightGrams);
        Assert.Equal("1.5× 1 plate", added[0].Items.Single().PortionDescription);
        Assert.Equal("Usual oats", added[1].Notes);
        Assert.Equal(300m, added[1].Items.Single().Calories);
    }

    private sealed class SplitOnlyAi : IFoodVisionService
    {
        public bool IsConfigured => true;
        public IReadOnlyList<string> ConfiguredProviders => ["fake"];
        public FoodItemAnalysis? LastDish;
        public Task<FoodVisionOutcome> SplitDishAsync(FoodItemAnalysis dish, CancellationToken ct)
        {
            LastDish = dish;
            FoodItemAnalysis Part(string n, decimal g, decimal kcal, decimal p, decimal c, decimal f) => new(n, $"{g} g", null, g, kcal, p, c, f, null, null, null, 0.8m, null, []);
            var parts = new List<FoodItemAnalysis> { Part("Grilled chicken", 150, 248, 46, 0, 5), Part("White rice", 200, 260, 5, 57, 1), Part("Sesame oil", 7, 62, 0, 0, 7) };
            return Task.FromResult(new FoodVisionOutcome(new FoodAnalysisResult(parts, 570, 51, 57, 13, 0.8m, false, null, []), "fake", "m"));
        }
        public Task<FoodVisionOutcome> AnalyzeAsync(byte[] image, string mimeType, string? userContext, CancellationToken ct) => throw new NotSupportedException();
        public Task<FoodVisionOutcome> EstimateFromTextAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public Task<MealIdeasOutcome> SuggestMealsAsync(MealIdeasRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private Task<int> SeedBowlScanAsync(string userId) => app.WithDbAsync(async db =>
    {
        FoodItemAnalysis I(string n, decimal kcal, decimal p) => new(n, "1", null, 100, kcal, p, 10, 5, null, null, null, 0.7m, "ai note", []);
        var result = new FoodAnalysisResult([I("Iced tea", 90, 0), I("Chicken rice bowl", 560, 38), I("Side salad", 40, 2)], 690, 40, 30, 15, 0.7m, false, null, []);
        var scan = new FoodScan { UserId = userId, CreatedAtUtc = DateTime.UtcNow, Provider = "gemini", Model = "m", MealType = MealType.Lunch, DiaryDate = new DateOnly(2026, 11, 2), Status = FoodScanStatus.Analyzed, ResultJson = System.Text.Json.JsonSerializer.Serialize(result) };
        db.FoodScans.Add(scan);
        await db.SaveChangesAsync();
        return scan.Id;
    });

    [Fact]
    public async Task Review_PutsProteinSourceFirst_WithAdjustTools()
    {
        var email = Email("protein-first");
        var userId = await app.CreateUserAsync(email);
        var scanId = await SeedBowlScanAsync(userId);
        var html = await (await app.LoginAsync(email)).GetStringAsync($"/FoodScanner/Review/{scanId}");
        Assert.True(html.IndexOf("Chicken rice bowl") < html.IndexOf("Iced tea"));
        Assert.Contains("protein source", html);
        Assert.Contains("Set protein to", html);
        Assert.Contains("Split into ingredients", html); // 560 kcal row
    }

    [Fact]
    public async Task SplitItem_ReplacesTheRowWithIngredients_AndKeepsOtherEdits()
    {
        var ai = new SplitOnlyAi();
        using var withAi = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFoodVisionService>(ai)));
        var email = Email("split");
        var userId = await app.CreateUserAsync(email);
        var scanId = await SeedBowlScanAsync(userId);
        var client = withAi.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(client, "/Identity/Account/Login", "/Identity/Account/Login",
            new() { ["Input.Email"] = email, ["Input.Password"] = AppFactory.Password })).StatusCode);

        // As displayed: bowl first (protein source), user changed the bowl's weight, unticked the tea and added a row.
        var form = new Dictionary<string, string>
        {
            ["ScanId"] = scanId.ToString(), ["Date"] = "2026-11-03", ["MealType"] = "Dinner", ["Notes"] = "edited", ["splitIndex"] = "0",
            ["Items[0].OriginalIndex"] = "1", ["Items[0].Include"] = "true", ["Items[0].Name"] = "Chicken rice bowl", ["Items[0].WeightGrams"] = "400",
            ["Items[0].Calories"] = "560", ["Items[0].ProteinGrams"] = "38", ["Items[0].CarbohydratesGrams"] = "62", ["Items[0].FatGrams"] = "16",
            ["Items[1].OriginalIndex"] = "0", ["Items[1].Include"] = "false", ["Items[1].Name"] = "Iced tea", ["Items[1].Calories"] = "90", ["Items[1].ProteinGrams"] = "0", ["Items[1].CarbohydratesGrams"] = "22", ["Items[1].FatGrams"] = "0",
            ["Items[2].OriginalIndex"] = "2", ["Items[2].Include"] = "true", ["Items[2].Name"] = "Side salad", ["Items[2].Calories"] = "55", ["Items[2].ProteinGrams"] = "2", ["Items[2].CarbohydratesGrams"] = "6", ["Items[2].FatGrams"] = "3",
            ["Items[3].OriginalIndex"] = "-1", ["Items[3].Include"] = "true", ["Items[3].Name"] = "Kimchi", ["Items[3].Calories"] = "15", ["Items[3].ProteinGrams"] = "1", ["Items[3].CarbohydratesGrams"] = "2", ["Items[3].FatGrams"] = "0",
        };
        var res = await AppFactory.PostFormAsync(client, $"/FoodScanner/Review/{scanId}", "/FoodScanner/SplitItem", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(400m, ai.LastDish!.EstimatedWeightGrams); // the user's corrected weight went to the AI

        var scan = await app.WithDbAsync(db => db.FoodScans.AsNoTracking().SingleAsync(s => s.Id == scanId));
        var stored = System.Text.Json.JsonSerializer.Deserialize<FoodAnalysisResult>(scan.ResultJson!)!;
        Assert.Equal(["Grilled chicken", "White rice", "Sesame oil", "Side salad", "Kimchi"], stored.Items.Select(i => i.Name));
        Assert.Equal(55m, stored.Items.Single(i => i.Name == "Side salad").Calories); // user's edit kept
        Assert.Equal("ai note", stored.Items.Single(i => i.Name == "Side salad").Notes);
        Assert.Equal(new DateOnly(2026, 11, 3), scan.DiaryDate);
        Assert.Equal(MealType.Dinner, scan.MealType);
        Assert.Equal(640m, stored.TotalCalories);

        // Another user can't split this scan.
        var other = Email("split-other");
        await app.CreateUserAsync(other);
        var otherClient = withAi.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await AppFactory.PostFormAsync(otherClient, "/Identity/Account/Login", "/Identity/Account/Login", new() { ["Input.Email"] = other, ["Input.Password"] = AppFactory.Password });
        Assert.Equal(HttpStatusCode.NotFound, (await AppFactory.PostFormAsync(otherClient, "/FoodScanner/Index", "/FoodScanner/SplitItem", new(form))).StatusCode);
    }

    [Fact]
    public async Task SplitItem_WithoutAi_SavesEditsAndExplains()
    {
        var email = Email("split-noai");
        var userId = await app.CreateUserAsync(email);
        var scanId = await SeedBowlScanAsync(userId);
        var client = await app.LoginAsync(email);
        var res = await AppFactory.PostFormAsync(client, $"/FoodScanner/Review/{scanId}", "/FoodScanner/SplitItem", new()
        {
            ["ScanId"] = scanId.ToString(), ["Date"] = "2026-11-02", ["MealType"] = "Lunch", ["splitIndex"] = "0",
            ["Items[0].OriginalIndex"] = "1", ["Items[0].Include"] = "true", ["Items[0].Name"] = "Chicken rice bowl", ["Items[0].Calories"] = "600",
            ["Items[0].ProteinGrams"] = "40", ["Items[0].CarbohydratesGrams"] = "62", ["Items[0].FatGrams"] = "16",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("isn&#x27;t set up", await client.GetStringAsync(res.Headers.Location!.ToString()));
        var stored = System.Text.Json.JsonSerializer.Deserialize<FoodAnalysisResult>((await app.WithDbAsync(db => db.FoodScans.AsNoTracking().SingleAsync(s => s.Id == scanId))).ResultJson!)!;
        Assert.Equal(600m, stored.Items.Single().Calories);
    }

    [Fact]
    public async Task Errors_DoNotLeakConfiguration()
    {
        var html = await app.NewClient().GetStringAsync("/Home/Error");
        Assert.DoesNotContain("Host=", html);
        Assert.DoesNotContain("Password", html);
    }
}

public class StartupConfigTests
{
    private static IHostEnvironment Env(string name) => new HostingEnvironment { EnvironmentName = name };

    [Fact]
    public void Production_WithoutConnectionOrEmail_GivesClearErrors_WithoutSecretValues()
    {
        var errors = StartupConfigValidator.Validate(Env("Production"), null, new EmailOptions { Provider = "resend", ApiKey = "re_super_secret_value" }, requireConfirmedEmail: true);
        Assert.Contains(errors, e => e.Contains("ConnectionStrings__DefaultConnection"));
        Assert.Contains(errors, e => e.Contains("EMAIL_FROM_ADDRESS"));
        Assert.DoesNotContain(errors, e => e.Contains("re_super_secret_value"));
    }

    [Fact]
    public void Production_CanRunWithoutEmail_WhenConfirmationDisabled() =>
        Assert.Empty(StartupConfigValidator.Validate(Env("Production"), "Host=x", new EmailOptions(), requireConfirmedEmail: false));

    [Fact]
    public void Development_NeedsOnlyConnectionString() =>
        Assert.Empty(StartupConfigValidator.Validate(Env("Development"), "Host=x", new EmailOptions(), requireConfirmedEmail: true));
}
