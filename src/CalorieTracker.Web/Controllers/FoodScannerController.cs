using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.Services.Ai;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Controllers;

public class FoodScannerController(
    ApplicationDbContext db, IFoodVisionService vision, INutritionPlanService plans, TimeProvider clock, ILogger<FoodScannerController> log) : Controller
{
    public async Task<IActionResult> Index(DateOnly? date, MealType? meal, CancellationToken ct) =>
        View(await BuildIndex(date, meal ?? MealType.Lunch, null, ct));

    [HttpPost, EnableRateLimiting(RateLimits.Ai), RequestSizeLimit(ImageUploadValidator.MaxBytes + 1024 * 1024)]
    public async Task<IActionResult> Analyze(IFormFile? photo, DateOnly date, MealType mealType, string? context, CancellationToken ct)
    {
        context = context?.Trim() is { Length: > 0 } c ? c[..Math.Min(c.Length, 1000)] : null;
        var vm = await BuildIndex(date, mealType, context, ct);
        if (!vision.IsConfigured) return View(nameof(Index), vm);

        var (image, error) = await ImageUploadValidator.ValidateAsync(photo, ct);
        if (image is null)
        {
            ModelState.AddModelError("photo", error!);
            return View(nameof(Index), vm);
        }

        FoodVisionOutcome outcome;
        try
        {
            outcome = await vision.AnalyzeAsync(image.Bytes, image.MimeType, context, ct);
        }
        catch (FoodVisionException ex)
        {
            log.LogWarning("Food scan failed: {Kind}", ex.Kind);
            ModelState.AddModelError("", ex.Message);
            return View(nameof(Index), vm);
        }
        // The image bytes go out of scope here; only the validated estimate is stored.

        var scan = new FoodScan
        {
            UserId = this.UserId(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime, Provider = outcome.Provider, Model = outcome.Model,
            MealType = mealType, DiaryDate = date, UserContext = context, Status = FoodScanStatus.Analyzed,
            ResultJson = JsonSerializer.Serialize(outcome.Result), OverallConfidence = outcome.Result.OverallConfidence,
        };
        db.FoodScans.Add(scan);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Review), new { id = scan.Id });
    }

    public async Task<IActionResult> Review(int id, CancellationToken ct)
    {
        var scan = await db.FoodScans.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.UserId == this.UserId(), ct);
        if (scan?.ResultJson is null) return NotFound();
        var analysis = JsonSerializer.Deserialize<FoodAnalysisResult>(scan.ResultJson)!;
        ViewData["AlreadySaved"] = scan.Status == FoodScanStatus.Saved;
        return View(new FoodAnalysisResultViewModel
        {
            ScanId = scan.Id, Date = scan.DiaryDate, MealType = scan.MealType, Notes = scan.UserContext,
            Analysis = analysis, Provider = scan.Provider, Model = scan.Model,
            Items = analysis.Items.Select((i, idx) => new ReviewItemInput
            {
                OriginalIndex = idx, Name = i.Name, PortionDescription = i.PortionDescription, WeightGrams = i.EstimatedWeightGrams,
                Calories = i.Calories, ProteinGrams = i.ProteinGrams, CarbohydratesGrams = i.CarbsGrams, FatGrams = i.FatGrams,
                FiberGrams = i.FiberGrams, SugarGrams = i.SugarGrams,
            }).ToList(),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Confirm(FoodAnalysisResultViewModel vm, CancellationToken ct)
    {
        var userId = this.UserId();
        var scan = await db.FoodScans.SingleOrDefaultAsync(s => s.Id == vm.ScanId && s.UserId == userId, ct);
        if (scan?.ResultJson is null) return NotFound();
        if (scan.Status == FoodScanStatus.Saved) return RedirectToAction("Index", "FoodDiary", new { date = scan.DiaryDate.ToString("yyyy-MM-dd") });

        var analysis = JsonSerializer.Deserialize<FoodAnalysisResult>(scan.ResultJson)!;
        var included = vm.Items.Where(i => i.Include).ToList();
        // Excluded rows aren't saved, so their validation errors don't matter.
        for (var i = 0; i < vm.Items.Count; i++)
            if (!vm.Items[i].Include)
                foreach (var key in ModelState.Keys.Where(k => k.StartsWith($"Items[{i}].")).ToList()) ModelState.Remove(key);
        if (included.Count == 0) ModelState.AddModelError("", "Keep at least one item, or discard this scan.");
        if (!ModelState.IsValid)
        {
            vm.Analysis = analysis; vm.Provider = scan.Provider; vm.Model = scan.Model;
            return View(nameof(Review), vm);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var entry = new FoodDiaryEntry
        {
            UserId = userId, DiaryDate = vm.Date, MealType = vm.MealType, Notes = vm.Notes?.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now,
            ClientRequestId = ScanRequestId(scan.Id),
            Items = included.Select(i =>
            {
                var fromAi = i.OriginalIndex >= 0 && i.OriginalIndex < analysis.Items.Count;
                return new FoodDiaryItem
                {
                    FoodNameSnapshot = i.Name.Trim(), Quantity = 1, PortionDescription = i.PortionDescription?.Trim(), WeightGrams = i.WeightGrams,
                    Calories = i.Calories, ProteinGrams = i.ProteinGrams, CarbohydratesGrams = i.CarbohydratesGrams, FatGrams = i.FatGrams,
                    FiberGrams = i.FiberGrams, SugarGrams = i.SugarGrams,
                    NutritionSource = fromAi ? NutritionSource.AiEstimate : NutritionSource.Manual,
                    IsAiEstimated = fromAi, AiConfidence = fromAi ? analysis.Items[i.OriginalIndex].Confidence : null,
                };
            }).ToList(),
        };
        db.FoodDiaryEntries.Add(entry);
        scan.Status = FoodScanStatus.Saved;
        try
        {
            await db.SaveChangesAsync(ct);
            scan.FoodDiaryEntryId = entry.Id;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A second click on Save hits the unique request id; the first one already saved the meal.
            var requestId = ScanRequestId(scan.Id);
            if (!await db.FoodDiaryEntries.AsNoTracking().AnyAsync(e => e.UserId == userId && e.ClientRequestId == requestId, ct)) throw;
            log.LogInformation(ex, "Ignored duplicate scan confirmation for scan {ScanId}", scan.Id);
        }
        this.Flash($"Saved {included.Count} item(s) to {vm.MealType.ToString().ToLowerInvariant()}.");
        return RedirectToAction("Index", "FoodDiary", new { date = vm.Date.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var scan = await db.FoodScans.SingleOrDefaultAsync(s => s.Id == id && s.UserId == this.UserId(), ct);
        if (scan is null) return NotFound();
        db.FoodScans.Remove(scan);
        await db.SaveChangesAsync(ct);
        this.Flash("Scan record deleted. Any meal you saved from it stays in your diary.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<FoodScannerViewModel> BuildIndex(DateOnly? date, MealType meal, string? context, CancellationToken ct)
    {
        var userId = this.UserId();
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        return new FoodScannerViewModel
        {
            IsConfigured = vision.IsConfigured,
            Providers = vision.ConfiguredProviders,
            Date = date ?? plans.Today(profile),
            MealType = meal,
            Context = context,
            RecentScans = await db.FoodScans.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedAtUtc).Take(10).ToListAsync(ct),
        };
    }

    /// <summary>Deterministic idempotency key per scan, so a scan can only ever create one diary entry.</summary>
    private static Guid ScanRequestId(int scanId) => new(MD5.HashData(Encoding.UTF8.GetBytes($"food-scan:{scanId}")));
}
