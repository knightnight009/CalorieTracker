using System.Net;
using System.Text.RegularExpressions;
using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
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
