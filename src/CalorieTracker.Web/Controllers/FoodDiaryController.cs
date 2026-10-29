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

/// <summary>Every query here filters on the signed-in user's id; ids from the request only ever narrow that set.</summary>
public class FoodDiaryController(
    ApplicationDbContext db, IDashboardService dashboard, INutritionPlanService plans,
    INutritionCalculatorService calc, IFoodNutritionService usda, IFoodVisionService ai, TimeProvider clock, ILogger<FoodDiaryController> log) : Controller
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IActionResult> Index(DateOnly? date, CancellationToken ct)
    {
        var userId = this.UserId();
        var plan = await plans.GetAsync(userId, ct);
        var today = plan?.Today ?? plans.Today(null);
        var day = date ?? today;
        var entries = await dashboard.GetDayEntriesAsync(userId, day, ct);
        return View(new FoodDiaryViewModel
        {
            Date = day,
            Today = today,
            Meals = Enum.GetValues<MealType>().Select(m =>
            {
                var es = entries.Where(e => e.MealType == m).ToList();
                return new DiaryMeal(m, es, calc.Sum(es.SelectMany(e => e.Items)));
            }).ToList(),
            DayTotals = calc.Sum(entries.SelectMany(e => e.Items)),
            Targets = plan?.Plan?.Macros.Targets,
            SavedMeals = await db.SavedMeals.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Name).ToListAsync(ct),
        });
    }

    // ---------- Adding food ----------

    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Add(DateOnly? date, MealType meal, string? q, string? tab, CancellationToken ct) =>
        View(nameof(Add), await BuildAddAsync(date, meal, q, tab, searchUsda: true, ct));

    private async Task<AddFoodViewModel> BuildAddAsync(DateOnly? date, MealType meal, string? q, string? tab, bool searchUsda, CancellationToken ct)
    {
        var userId = this.UserId();
        var day = date ?? plans.Today(await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct));
        var vm = new AddFoodViewModel { Date = day, MealType = meal, Query = q?.Trim(), Tab = tab ?? "search", UsdaConfigured = usda.IsConfigured, AiConfigured = ai.IsConfigured };
        vm.Manual = new ManualFoodInput { Date = day, MealType = meal, ClientRequestId = vm.ClientRequestId };

        var foods = db.Foods.AsNoTracking().Where(f => f.UserId == userId);
        if (!string.IsNullOrWhiteSpace(vm.Query))
        {
            var pattern = $"%{EscapeLike(vm.Query)}%";
            foods = foods.Where(f => EF.Functions.ILike(f.Name, pattern, "\\") || (f.Brand != null && EF.Functions.ILike(f.Brand, pattern, "\\")));
            if (searchUsda && usda.IsConfigured)
            {
                try { vm.UsdaResults = (await usda.SearchAsync(vm.Query, ct)).ToList(); }
                catch (FoodDatabaseUnavailableException) { vm.UsdaError = "The USDA database couldn't be reached. Your own foods and manual entry still work."; }
            }
        }
        vm.MyFoods = await foods.OrderByDescending(f => f.IsFavorite).ThenBy(f => f.Name).Take(30).ToListAsync(ct);

        var recent = await db.FoodDiaryItems.AsNoTracking().Where(i => i.Entry!.UserId == userId)
            .OrderByDescending(i => i.Id).Take(100).ToListAsync(ct);
        vm.RecentItems = recent.DistinctBy(i => i.FoodNameSnapshot.ToLowerInvariant()).Take(15).ToList();
        vm.SavedMeals = await db.SavedMeals.AsNoTracking().Include(s => s.Items).Where(s => s.UserId == userId).OrderBy(s => s.Name).ToListAsync(ct);
        return vm;
    }

    /// <summary>Search results estimated by the AI provider instead of a database. Rate-limited like photo scans.</summary>
    [EnableRateLimiting(RateLimits.Ai), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> AiSearch(DateOnly? date, MealType meal, string? q, CancellationToken ct)
    {
        var vm = await BuildAddAsync(date, meal, q, "search", searchUsda: false, ct);
        if (string.IsNullOrWhiteSpace(vm.Query) || !ai.IsConfigured) return View(nameof(Add), vm);
        try
        {
            var outcome = await ai.EstimateFromTextAsync(vm.Query, ct);
            vm.AiResults = outcome.Result.Items.ToList();
            vm.AiProvider = outcome.Provider;
        }
        catch (FoodVisionException ex)
        {
            log.LogWarning("AI food search failed: {Kind}", ex.Kind);
            vm.AiError = ex.Message;
        }
        vm.AiSearched = true;
        return View(nameof(Add), vm);
    }

    [HttpPost]
    public async Task<IActionResult> AddAiEstimate(AddAiEstimateInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "search", ct);
        // Values come from an AI estimate the user saw; they're validated like manual entry and labelled as estimates.
        var n = calc.Scale(new Food
        {
            Calories = input.Nutrients.Calories, ProteinGrams = input.Nutrients.ProteinGrams, CarbohydratesGrams = input.Nutrients.CarbohydratesGrams,
            FatGrams = input.Nutrients.FatGrams, FiberGrams = input.Nutrients.FiberGrams, SugarGrams = input.Nutrients.SugarGrams,
        }, 1, input.Quantity);
        var item = new FoodDiaryItem
        {
            FoodNameSnapshot = input.Name.Trim(), Quantity = input.Quantity,
            PortionDescription = input.Quantity == 1 ? input.PortionDescription?.Trim() : $"{input.Quantity:0.##} × {input.PortionDescription?.Trim()}",
            WeightGrams = input.WeightGrams * input.Quantity,
            Calories = n.Calories, ProteinGrams = n.ProteinGrams, CarbohydratesGrams = n.CarbohydratesGrams, FatGrams = n.FatGrams,
            FiberGrams = input.Nutrients.FiberGrams is null ? null : n.FiberGrams, SugarGrams = input.Nutrients.SugarGrams is null ? null : n.SugarGrams,
            NutritionSource = NutritionSource.AiEstimate, IsAiEstimated = true, AiConfidence = input.Confidence,
        };
        return await CreateEntry(input, [item], null, $"Added {item.FoodNameSnapshot} (AI estimate).", ct);
    }

    [HttpPost]
    public async Task<IActionResult> AddManual([Bind(Prefix = "Manual")] ManualFoodInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "manual", ct);
        var userId = this.UserId();
        var n = input.Nutrients;
        Food? food = null;
        if (input.SaveAsCustomFood)
        {
            food = new Food
            {
                UserId = userId, Name = input.Name.Trim(), ServingDescription = string.IsNullOrWhiteSpace(input.PortionDescription) ? "1 serving" : input.PortionDescription.Trim(),
                ServingWeightGrams = input.WeightGrams, Calories = n.Calories, ProteinGrams = n.ProteinGrams, CarbohydratesGrams = n.CarbohydratesGrams,
                FatGrams = n.FatGrams, FiberGrams = n.FiberGrams, SugarGrams = n.SugarGrams, DataSource = NutritionSource.CustomFood,
                IsCustomFood = true, CreatedAtUtc = Now, UpdatedAtUtc = Now,
            };
            db.Foods.Add(food);
        }
        var item = new FoodDiaryItem
        {
            Food = food, FoodNameSnapshot = input.Name.Trim(), Quantity = 1, PortionDescription = input.PortionDescription?.Trim(), WeightGrams = input.WeightGrams,
            Calories = n.Calories, ProteinGrams = n.ProteinGrams, CarbohydratesGrams = n.CarbohydratesGrams, FatGrams = n.FatGrams,
            FiberGrams = n.FiberGrams, SugarGrams = n.SugarGrams, NutritionSource = food is null ? NutritionSource.Manual : NutritionSource.CustomFood,
        };
        return await CreateEntry(input, [item], null, $"Added {item.FoodNameSnapshot}.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> AddFromFood(AddFromFoodInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "search", ct);
        var food = await db.Foods.SingleOrDefaultAsync(f => f.Id == input.FoodId && f.UserId == this.UserId(), ct);
        if (food is null) return NotFound();
        return await CreateEntry(input, [ItemFromFood(food, input.Quantity, $"{input.Quantity:0.##} × {food.ServingDescription}")], null, $"Added {food.Name}.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> AddUsda(AddUsdaInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "search", ct);
        var userId = this.UserId();
        // Re-fetch from USDA on the server rather than trusting nutrient values posted by the browser.
        var food = await db.Foods.FirstOrDefaultAsync(f => f.UserId == userId && f.DataSource == NutritionSource.Usda && f.ExternalFoodId == input.FdcId, ct);
        if (food is null)
        {
            UsdaFood? u;
            try { u = await usda.GetAsync(input.FdcId, ct); }
            catch (FoodDatabaseUnavailableException)
            {
                this.Flash("The USDA database couldn't be reached. Try again, or use manual entry.", "warning");
                return RedirectToAction(nameof(Add), new { date = input.Date.ToString("yyyy-MM-dd"), meal = input.MealType });
            }
            if (u is null) return NotFound();
            food = new Food
            {
                UserId = userId, Name = u.Description, Brand = u.Brand, ServingDescription = "100 g", ServingWeightGrams = 100,
                Calories = u.Calories, ProteinGrams = u.ProteinGrams, CarbohydratesGrams = u.CarbohydratesGrams, FatGrams = u.FatGrams,
                FiberGrams = u.FiberGrams, SugarGrams = u.SugarGrams, SodiumMilligrams = u.SodiumMilligrams,
                DataSource = NutritionSource.Usda, ExternalFoodId = u.FdcId, CreatedAtUtc = Now, UpdatedAtUtc = Now,
            };
            db.Foods.Add(food);
        }
        return await CreateEntry(input, [ItemFromFood(food, input.Grams / 100, $"{input.Grams:0.#} g")], null, $"Added {food.Name}.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> AddRecent(AddRecentInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "recent", ct);
        var src = await db.FoodDiaryItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == input.DiaryItemId && i.Entry!.UserId == this.UserId(), ct);
        if (src is null) return NotFound();
        return await CreateEntry(input, [CopyItem(src)], null, $"Added {src.FoodNameSnapshot}.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> AddSavedMeal(AddSavedMealInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await ReshowAdd(input, "saved", ct);
        var meal = await db.SavedMeals.AsNoTracking().Include(s => s.Items).SingleOrDefaultAsync(s => s.Id == input.SavedMealId && s.UserId == this.UserId(), ct);
        if (meal is null) return NotFound();
        var items = meal.Items.Select(i => new FoodDiaryItem
        {
            FoodId = i.FoodId, FoodNameSnapshot = i.FoodNameSnapshot, Quantity = i.Quantity, PortionDescription = i.PortionDescription, WeightGrams = i.WeightGrams,
            Calories = i.Calories, ProteinGrams = i.ProteinGrams, CarbohydratesGrams = i.CarbohydratesGrams, FatGrams = i.FatGrams,
            FiberGrams = i.FiberGrams, SugarGrams = i.SugarGrams, NutritionSource = i.NutritionSource, IsAiEstimated = i.IsAiEstimated,
        }).ToList();
        return await CreateEntry(input, items, meal.Name, $"Added saved meal \"{meal.Name}\".", ct);
    }

    private async Task<IActionResult> ReshowAdd(AddToDiaryInput input, string tab, CancellationToken ct)
    {
        this.Flash("Please check the highlighted values and try again.", "warning");
        if (input is ManualFoodInput manual)
        {
            var result = (ViewResult)await Add(input.Date, input.MealType, null, tab, ct);
            var vm = (AddFoodViewModel)result.Model!;
            vm.Manual = manual;
            vm.ClientRequestId = manual.ClientRequestId;
            return result;
        }
        return RedirectToAction(nameof(Add), new { date = input.Date.ToString("yyyy-MM-dd"), meal = input.MealType, tab });
    }

    private FoodDiaryItem ItemFromFood(Food food, decimal quantity, string portion)
    {
        var n = calc.Scale(food, 1, quantity);
        return new FoodDiaryItem
        {
            Food = food, FoodNameSnapshot = food.Name, Quantity = quantity, PortionDescription = portion,
            WeightGrams = food.ServingWeightGrams * quantity,
            Calories = n.Calories, ProteinGrams = n.ProteinGrams, CarbohydratesGrams = n.CarbohydratesGrams, FatGrams = n.FatGrams,
            FiberGrams = food.FiberGrams is null ? null : n.FiberGrams, SugarGrams = food.SugarGrams is null ? null : n.SugarGrams,
            NutritionSource = food.DataSource,
        };
    }

    private static FoodDiaryItem CopyItem(FoodDiaryItem s) => new()
    {
        FoodId = s.FoodId, FoodNameSnapshot = s.FoodNameSnapshot, Quantity = s.Quantity, PortionDescription = s.PortionDescription, WeightGrams = s.WeightGrams,
        Calories = s.Calories, ProteinGrams = s.ProteinGrams, CarbohydratesGrams = s.CarbohydratesGrams, FatGrams = s.FatGrams,
        FiberGrams = s.FiberGrams, SugarGrams = s.SugarGrams, NutritionSource = s.NutritionSource, IsAiEstimated = s.IsAiEstimated, AiConfidence = s.AiConfidence,
    };

    /// <summary>Creates a diary entry once per ClientRequestId, so double-clicking Save doesn't log the meal twice.</summary>
    private async Task<IActionResult> CreateEntry(AddToDiaryInput input, List<FoodDiaryItem> items, string? notes, string message, CancellationToken ct)
    {
        var userId = this.UserId();
        var requestId = input.ClientRequestId == Guid.Empty ? Guid.NewGuid() : input.ClientRequestId;
        if (await db.FoodDiaryEntries.AnyAsync(e => e.UserId == userId && e.ClientRequestId == requestId, ct))
            return RedirectToAction(nameof(Index), new { date = input.Date.ToString("yyyy-MM-dd") });

        db.FoodDiaryEntries.Add(new FoodDiaryEntry
        {
            UserId = userId, DiaryDate = input.Date, MealType = input.MealType, Notes = notes, ClientRequestId = requestId,
            CreatedAtUtc = Now, UpdatedAtUtc = Now, Items = items,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            // A concurrent duplicate submit lost the race on the unique request id; anything else is a real failure.
            if (!await db.FoodDiaryEntries.AsNoTracking().AnyAsync(e => e.UserId == userId && e.ClientRequestId == requestId, ct)) throw;
            log.LogInformation(ex, "Ignored duplicate diary submission");
        }
        this.Flash(message);
        return RedirectToAction(nameof(Index), new { date = input.Date.ToString("yyyy-MM-dd") });
    }

    // ---------- Editing ----------

    public async Task<IActionResult> EditItem(int id, CancellationToken ct)
    {
        var item = await db.FoodDiaryItems.AsNoTracking().Include(i => i.Entry).SingleOrDefaultAsync(i => i.Id == id && i.Entry!.UserId == this.UserId(), ct);
        if (item is null) return NotFound();
        return View(new EditDiaryItemInput
        {
            Id = item.Id, FoodName = item.FoodNameSnapshot, Name = item.FoodNameSnapshot, Date = item.Entry!.DiaryDate, MealType = item.Entry.MealType,
            Quantity = Math.Round(item.Quantity, 2), PortionDescription = item.PortionDescription, WeightGrams = item.WeightGrams is null ? null : Math.Round(item.WeightGrams.Value, 1),
            Nutrients = new NutrientFields
            {
                Calories = Math.Round(item.Calories, 1), ProteinGrams = Math.Round(item.ProteinGrams, 1), CarbohydratesGrams = Math.Round(item.CarbohydratesGrams, 1),
                FatGrams = Math.Round(item.FatGrams, 1), FiberGrams = item.FiberGrams is null ? null : Math.Round(item.FiberGrams.Value, 1),
                SugarGrams = item.SugarGrams is null ? null : Math.Round(item.SugarGrams.Value, 1),
            },
        });
    }

    [HttpPost]
    public async Task<IActionResult> EditItem(EditDiaryItemInput input, CancellationToken ct)
    {
        var userId = this.UserId();
        var item = await db.FoodDiaryItems.Include(i => i.Entry).SingleOrDefaultAsync(i => i.Id == input.Id && i.Entry!.UserId == userId, ct);
        if (item is null) return NotFound();
        if (input.RescaleFromQuantity) // nutrient fields are ignored in this mode
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith(nameof(input.Nutrients))).ToList()) ModelState.Remove(key);
        if (!ModelState.IsValid) { input.FoodName = item.FoodNameSnapshot; input.Date = item.Entry!.DiaryDate; return View(input); }

        if (input.RescaleFromQuantity)
        {
            // A changed weight drives the rescale (servings follow); otherwise a changed servings count does (weight follows).
            var oldWeight = item.WeightGrams;
            var weightChanged = oldWeight is > 0 && input.WeightGrams is > 0 && input.WeightGrams != Math.Round(oldWeight.Value, 1);
            var newQuantity = weightChanged ? item.Quantity * input.WeightGrams!.Value / oldWeight!.Value : input.Quantity;
            if (newQuantity != item.Quantity)
            {
                var n = calc.Scale(item, item.Quantity, newQuantity);
                item.Calories = n.Calories; item.ProteinGrams = n.ProteinGrams; item.CarbohydratesGrams = n.CarbohydratesGrams; item.FatGrams = n.FatGrams;
                if (item.FiberGrams is not null) item.FiberGrams = n.FiberGrams;
                if (item.SugarGrams is not null) item.SugarGrams = n.SugarGrams;
                if (!weightChanged && oldWeight is not null) input.WeightGrams = oldWeight * newQuantity / item.Quantity;
                input.Quantity = newQuantity;
            }
        }
        else
        {
            var nf = input.Nutrients;
            item.Calories = nf.Calories; item.ProteinGrams = nf.ProteinGrams; item.CarbohydratesGrams = nf.CarbohydratesGrams; item.FatGrams = nf.FatGrams;
            item.FiberGrams = nf.FiberGrams; item.SugarGrams = nf.SugarGrams;
            if (item.NutritionSource != NutritionSource.Manual) item.NutritionSource = NutritionSource.Manual;
        }
        item.FoodNameSnapshot = input.Name.Trim();
        item.Quantity = input.Quantity;
        item.PortionDescription = input.PortionDescription?.Trim();
        item.WeightGrams = input.WeightGrams;
        item.Entry!.MealType = input.MealType;
        item.Entry.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        this.Flash($"Updated {item.FoodNameSnapshot}.");
        return RedirectToAction(nameof(Index), new { date = item.Entry.DiaryDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteItem(int id, CancellationToken ct)
    {
        var item = await db.FoodDiaryItems.Include(i => i.Entry!).ThenInclude(e => e.Items).SingleOrDefaultAsync(i => i.Id == id && i.Entry!.UserId == this.UserId(), ct);
        if (item is null) return NotFound();
        var entry = item.Entry!;
        // Remove the whole entry when its last item goes, so empty meals don't linger.
        if (entry.Items.Count == 1) db.FoodDiaryEntries.Remove(entry); else db.FoodDiaryItems.Remove(item);
        await db.SaveChangesAsync(ct);
        this.Flash($"Removed {item.FoodNameSnapshot}.");
        return RedirectToAction(nameof(Index), new { date = entry.DiaryDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteEntry(int id, CancellationToken ct)
    {
        var entry = await db.FoodDiaryEntries.SingleOrDefaultAsync(e => e.Id == id && e.UserId == this.UserId(), ct);
        if (entry is null) return NotFound();
        db.FoodDiaryEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        this.Flash("Meal removed.");
        return RedirectToAction(nameof(Index), new { date = entry.DiaryDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> CopyEntry(CopyEntryInput input, CancellationToken ct)
    {
        var userId = this.UserId();
        var src = await db.FoodDiaryEntries.AsNoTracking().Include(e => e.Items).SingleOrDefaultAsync(e => e.Id == input.EntryId && e.UserId == userId, ct);
        if (src is null) return NotFound();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index), new { date = src.DiaryDate.ToString("yyyy-MM-dd") });
        db.FoodDiaryEntries.Add(new FoodDiaryEntry
        {
            UserId = userId, DiaryDate = input.TargetDate, MealType = input.TargetMealType, Notes = src.Notes,
            CreatedAtUtc = Now, UpdatedAtUtc = Now, Items = src.Items.Select(CopyItem).ToList(),
        });
        await db.SaveChangesAsync(ct);
        this.Flash($"Copied to {input.TargetMealType.ToString().ToLowerInvariant()} on {input.TargetDate:MMM d}.");
        return RedirectToAction(nameof(Index), new { date = input.TargetDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> SaveAsMeal(SaveMealInput input, CancellationToken ct)
    {
        var userId = this.UserId();
        var src = await db.FoodDiaryEntries.AsNoTracking().Include(e => e.Items).SingleOrDefaultAsync(e => e.Id == input.EntryId && e.UserId == userId, ct);
        if (src is null) return NotFound();
        if (!ModelState.IsValid)
        {
            this.Flash("Please give the saved meal a name.", "warning");
            return RedirectToAction(nameof(Index), new { date = src.DiaryDate.ToString("yyyy-MM-dd") });
        }
        db.SavedMeals.Add(new SavedMeal
        {
            UserId = userId, Name = input.Name.Trim(), CreatedAtUtc = Now,
            Items = src.Items.Select(s => new SavedMealItem
            {
                FoodId = s.FoodId, FoodNameSnapshot = s.FoodNameSnapshot, Quantity = s.Quantity, PortionDescription = s.PortionDescription, WeightGrams = s.WeightGrams,
                Calories = s.Calories, ProteinGrams = s.ProteinGrams, CarbohydratesGrams = s.CarbohydratesGrams, FatGrams = s.FatGrams,
                FiberGrams = s.FiberGrams, SugarGrams = s.SugarGrams, NutritionSource = s.NutritionSource, IsAiEstimated = s.IsAiEstimated,
            }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        this.Flash($"Saved \"{input.Name.Trim()}\" for quick reuse.");
        return RedirectToAction(nameof(Index), new { date = src.DiaryDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSavedMeal(int id, CancellationToken ct)
    {
        var meal = await db.SavedMeals.SingleOrDefaultAsync(s => s.Id == id && s.UserId == this.UserId(), ct);
        if (meal is null) return NotFound();
        db.SavedMeals.Remove(meal);
        await db.SaveChangesAsync(ct);
        this.Flash($"Deleted saved meal \"{meal.Name}\".");
        return RedirectToAction(nameof(Foods));
    }

    // ---------- My foods ----------

    public async Task<IActionResult> Foods(CancellationToken ct)
    {
        var userId = this.UserId();
        ViewData["SavedMeals"] = await db.SavedMeals.AsNoTracking().Include(s => s.Items).Where(s => s.UserId == userId).OrderBy(s => s.Name).ToListAsync(ct);
        return View(await db.Foods.AsNoTracking().Where(f => f.UserId == userId).OrderByDescending(f => f.IsFavorite).ThenBy(f => f.Name).ToListAsync(ct));
    }

    public async Task<IActionResult> FoodForm(int? id, CancellationToken ct)
    {
        if (id is null) return View(new FoodInput());
        var f = await db.Foods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == this.UserId(), ct);
        if (f is null) return NotFound();
        return View(new FoodInput
        {
            Id = f.Id, Name = f.Name, Brand = f.Brand, ServingDescription = f.ServingDescription, ServingWeightGrams = f.ServingWeightGrams,
            SodiumMilligrams = f.SodiumMilligrams, IsFavorite = f.IsFavorite,
            Nutrients = new NutrientFields { Calories = f.Calories, ProteinGrams = f.ProteinGrams, CarbohydratesGrams = f.CarbohydratesGrams, FatGrams = f.FatGrams, FiberGrams = f.FiberGrams, SugarGrams = f.SugarGrams },
        });
    }

    [HttpPost]
    public async Task<IActionResult> FoodForm(FoodInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);
        var userId = this.UserId();
        Food? f;
        if (input.Id is null)
        {
            db.Foods.Add(f = new Food { UserId = userId, DataSource = NutritionSource.CustomFood, IsCustomFood = true, CreatedAtUtc = Now });
        }
        else
        {
            f = await db.Foods.SingleOrDefaultAsync(x => x.Id == input.Id && x.UserId == userId, ct);
            if (f is null) return NotFound();
        }
        var n = input.Nutrients;
        f.Name = input.Name.Trim(); f.Brand = input.Brand?.Trim(); f.ServingDescription = input.ServingDescription.Trim(); f.ServingWeightGrams = input.ServingWeightGrams;
        f.Calories = n.Calories; f.ProteinGrams = n.ProteinGrams; f.CarbohydratesGrams = n.CarbohydratesGrams; f.FatGrams = n.FatGrams;
        f.FiberGrams = n.FiberGrams; f.SugarGrams = n.SugarGrams; f.SodiumMilligrams = input.SodiumMilligrams; f.IsFavorite = input.IsFavorite;
        f.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        // Diary items keep their own snapshot, so editing a food never changes past days.
        this.Flash(input.Id is null ? $"Created {f.Name}." : $"Updated {f.Name}. Past diary entries are unchanged.");
        return RedirectToAction(nameof(Foods));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleFavorite(int id, CancellationToken ct)
    {
        var f = await db.Foods.SingleOrDefaultAsync(x => x.Id == id && x.UserId == this.UserId(), ct);
        if (f is null) return NotFound();
        f.IsFavorite = !f.IsFavorite;
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Foods));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFood(int id, CancellationToken ct)
    {
        var f = await db.Foods.SingleOrDefaultAsync(x => x.Id == id && x.UserId == this.UserId(), ct);
        if (f is null) return NotFound();
        db.Foods.Remove(f);
        await db.SaveChangesAsync(ct);
        this.Flash($"Deleted {f.Name}. Past diary entries are unchanged.");
        return RedirectToAction(nameof(Foods));
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
