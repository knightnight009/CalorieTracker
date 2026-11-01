using CalorieTracker.Web.Data;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services.Ai;
using CalorieTracker.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Services;

public record DailyTotals(DateOnly Date, NutrientTotals Totals);

/// <summary>A past diary entry or saved meal that could be eaten again. Exactly one of the ids is set.</summary>
public record RecentMealCandidate(string Name, string Source, int? EntryId, int? SavedMealId, NutrientTotals Totals);

/// <summary>A candidate at the portion factor that fits the budget (or at 1× with a note when none does).</summary>
public record RecentMealSuggestion(RecentMealCandidate Meal, decimal Factor, bool Fits, string? Note)
{
    public NutrientTotals Scaled => new(Meal.Totals.Calories * Factor, Meal.Totals.ProteinGrams * Factor,
        Meal.Totals.CarbohydratesGrams * Factor, Meal.Totals.FatGrams * Factor);
}

public interface IDashboardService
{
    Task<List<FoodDiaryEntry>> GetDayEntriesAsync(string userId, DateOnly date, CancellationToken ct = default);
    Task<DashboardViewModel?> BuildAsync(string userId, CancellationToken ct = default);
    /// <summary>Totals for days that have at least one logged item; days without food are absent, not zero.</summary>
    Task<List<DailyTotals>> GetDailyTotalsAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<List<RecentMealCandidate>> GetRecentMealCandidatesAsync(string userId, DateOnly today, int days = 30, CancellationToken ct = default);
}

public class DashboardService(ApplicationDbContext db, INutritionPlanService plans, INutritionCalculatorService calc) : IDashboardService
{
    public Task<List<FoodDiaryEntry>> GetDayEntriesAsync(string userId, DateOnly date, CancellationToken ct = default) =>
        db.FoodDiaryEntries.AsNoTracking().Include(e => e.Items.OrderBy(i => i.Id))
            .Where(e => e.UserId == userId && e.DiaryDate == date)
            .OrderBy(e => e.MealType).ThenBy(e => e.CreatedAtUtc).ToListAsync(ct);

    public async Task<DashboardViewModel?> BuildAsync(string userId, CancellationToken ct = default)
    {
        var plan = await plans.GetAsync(userId, ct);
        if (plan is null) return null;

        var entries = await GetDayEntriesAsync(userId, plan.Today, ct);
        var consumed = calc.Sum(entries.SelectMany(e => e.Items));
        var targets = plan.Plan?.Macros.Targets;

        var meals = Enum.GetValues<MealType>().Select(m =>
        {
            var items = entries.Where(e => e.MealType == m).SelectMany(e => e.Items).ToList();
            return new MealSummary(m, calc.Sum(items), items.Select(i => i.FoodNameSnapshot).ToList());
        }).ToList();

        var vm = new DashboardViewModel
        {
            Today = plan.Today,
            Problem = plan.Problem ?? (plan.Plan is { Macros.IsValid: false } ? string.Join(" ", plan.Plan.Macros.Errors) : null),
            Targets = targets,
            Consumed = consumed,
            Meals = meals,
            LatestWeightKg = plan.LatestWeight?.WeightKg,
            LatestWeightDate = plan.LatestWeight?.Date,
            WeightUnit = plan.Profile.PreferredWeightUnit,
        };
        vm.IdeasMealType = DefaultIdeaMeal(meals);
        if (targets is not null)
        {
            vm.Calories = calc.Remaining(targets.Calories, consumed.Calories);
            vm.Protein = calc.Remaining(targets.ProteinGrams, consumed.ProteinGrams);
            vm.Carbs = calc.Remaining(targets.CarbohydratesGrams, consumed.CarbohydratesGrams);
            vm.Fat = calc.Remaining(targets.FatGrams, consumed.FatGrams);
            vm.Suggestions = GapSuggestions(vm.Calories, vm.Protein, vm.Carbs, vm.Fat);
        }
        return vm;
    }

    /// <summary>
    /// Whether a suggested meal fits what's left: calories within the remaining budget (3% allowance for rounding)
    /// and at least 90% of the remaining protein. Totals come from the meal's own items, never the AI's arithmetic.
    /// </summary>
    public static (bool Fits, string? Note) MealIdeaFit(MealIdea idea, RemainingAmount calories, RemainingAmount protein) =>
        Fit(idea.TotalCalories, idea.TotalProteinGrams, calories, protein);

    public static (bool Fits, string? Note) Fit(decimal kcal, decimal proteinGrams, RemainingAmount calories, RemainingAmount protein)
    {
        var notes = new List<string>();
        var over = kcal - calories.Remaining;
        if (over > calories.Remaining * 0.03m) notes.Add($"{over:0} kcal over");
        var shortBy = protein.Remaining - proteinGrams;
        if (protein.Remaining > 0 && proteinGrams < protein.Remaining * 0.9m) notes.Add($"{shortBy:0} g protein short");
        return (notes.Count == 0, notes.Count == 0 ? null : string.Join(", ", notes));
    }

    private static readonly decimal[] PortionFactors = [1m, 1.5m, 0.5m, 2m]; // tried in this order: as logged first

    /// <summary>
    /// Picks up to <paramref name="take"/> past meals for a meal budget. Each meal is tried as logged, then at 1.5×, 0.5× and 2×;
    /// the first portion that fits wins. Fitting meals come first (as-logged before scaled, then closest to the calorie budget),
    /// followed by close near-misses at their logged size with an honest note.
    /// </summary>
    public static List<RecentMealSuggestion> RankRecentMeals(IEnumerable<RecentMealCandidate> candidates, decimal budgetKcal, decimal budgetProtein, int take = 3)
    {
        var cal = new RemainingAmount(budgetKcal, 0);
        var protein = new RemainingAmount(budgetProtein, 0);
        var fitting = new List<RecentMealSuggestion>();
        var misses = new List<(RecentMealSuggestion S, decimal Distance)>();
        foreach (var c in candidates)
        {
            if (c.Totals.Calories <= 0) continue;
            var fitFactor = PortionFactors.FirstOrDefault(f => Fit(c.Totals.Calories * f, c.Totals.ProteinGrams * f, cal, protein).Fits);
            if (fitFactor > 0)
            {
                fitting.Add(new RecentMealSuggestion(c, fitFactor, true, null));
                continue;
            }
            var (_, note) = Fit(c.Totals.Calories, c.Totals.ProteinGrams, cal, protein);
            var overShare = Math.Max(0, c.Totals.Calories - budgetKcal) / budgetKcal;
            var shortShare = budgetProtein > 0 ? Math.Max(0, budgetProtein - c.Totals.ProteinGrams) / budgetProtein : 0;
            misses.Add((new RecentMealSuggestion(c, 1m, false, note), overShare + shortShare));
        }
        return fitting
            .OrderByDescending(s => s.Factor == 1m)
            .ThenBy(s => Math.Abs(s.Meal.Totals.Calories * s.Factor - budgetKcal))
            .Concat(misses.Where(m => m.Distance <= 0.35m).OrderBy(m => m.Distance).Select(m => m.S))
            .Take(take).ToList();
    }

    public async Task<List<RecentMealCandidate>> GetRecentMealCandidatesAsync(string userId, DateOnly today, int days = 30, CancellationToken ct = default)
    {
        var from = today.AddDays(-days);
        // Past days only: today's meals are what you're planning around, not ideas for it.
        var entries = await db.FoodDiaryEntries.AsNoTracking().Include(e => e.Items)
            .Where(e => e.UserId == userId && e.DiaryDate >= from && e.DiaryDate < today && e.Items.Any())
            .OrderByDescending(e => e.DiaryDate).ThenByDescending(e => e.CreatedAtUtc).Take(200).ToListAsync(ct);
        var saved = await db.SavedMeals.AsNoTracking().Include(s => s.Items)
            .Where(s => s.UserId == userId && s.Items.Any()).ToListAsync(ct);

        static string Key(IEnumerable<string> names) => string.Join("|", names.Select(n => n.Trim().ToLowerInvariant()).Order());
        static string Describe(IReadOnlyList<string> names) =>
            names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

        var result = new List<RecentMealCandidate>();
        var seen = new HashSet<string>();
        // Saved meals win over a logged copy of the same foods; otherwise the most recent log of a meal is kept.
        foreach (var s in saved)
            if (seen.Add(Key(s.Items.Select(i => i.FoodNameSnapshot))))
                result.Add(new RecentMealCandidate(s.Name, $"Saved meal · {s.Items.Count} foods", null, s.Id, calc.Sum(s.Items)));
        foreach (var e in entries)
        {
            var names = e.Items.Select(i => i.FoodNameSnapshot).ToList();
            if (!seen.Add(Key(names))) continue;
            result.Add(new RecentMealCandidate(e.Notes ?? Describe(names),
                $"Logged {e.DiaryDate:ddd, MMM d} · {e.MealType.ToString().ToLowerInvariant()} · {names.Count} food{(names.Count == 1 ? "" : "s")}",
                e.Id, null, calc.Sum(e.Items)));
        }
        return result;
    }

    /// <summary>
    /// The share of what's left that one meal should use: the remainder split across the main meals still to eat
    /// (this one included). A snack counts as one extra share. The last meal of the day gets everything left.
    /// </summary>
    public static (decimal Calories, decimal Protein, decimal Carbs, decimal Fat, int MealsLeft) MealBudget(
        MealType meal, IEnumerable<MealSummary> meals, RemainingAmount calories, RemainingAmount protein, RemainingAmount? carbs, RemainingAmount? fat)
    {
        var unloggedMain = meals.Count(m => m.MealType != MealType.Snack && m.ItemNames.Count == 0);
        var chosenAlreadyCounted = meal != MealType.Snack && meals.Any(m => m.MealType == meal && m.ItemNames.Count == 0);
        var shares = Math.Max(1, unloggedMain + (chosenAlreadyCounted ? 0 : 1));
        return (calories.Remaining / shares, protein.Remaining / shares, (carbs?.Remaining ?? 0) / shares, (fat?.Remaining ?? 0) / shares, shares);
    }

    /// <summary>The first main meal with nothing logged yet, otherwise a snack.</summary>
    public static MealType DefaultIdeaMeal(IEnumerable<MealSummary> meals)
    {
        var empty = meals.Where(m => m.ItemNames.Count == 0).Select(m => m.MealType).ToHashSet();
        return new[] { MealType.Breakfast, MealType.Lunch, MealType.Dinner }.FirstOrDefault(empty.Contains, MealType.Snack);
    }

    /// <summary>Plain-language hints built only from the user's actual remaining targets.</summary>
    public static List<string> GapSuggestions(RemainingAmount cal, RemainingAmount protein, RemainingAmount carbs, RemainingAmount fat)
    {
        var tips = new List<string>();
        if (cal.Consumed == 0)
        {
            tips.Add($"Nothing logged yet today. Your budget is {cal.Target:0} kcal with {protein.Target:0} g protein.");
            return tips;
        }
        if (cal.IsOver)
        {
            tips.Add($"You're {cal.Excess:0} kcal over today's target. One day over doesn't undo your progress; your weekly average matters more than any single day.");
            return tips;
        }
        if (protein.Remaining > 0)
        {
            var proteinKcal = protein.Remaining * 4;
            if (proteinKcal <= cal.Remaining)
            {
                var left = cal.Remaining - proteinKcal;
                tips.Add($"Your remaining {protein.Remaining:0} g protein is about {proteinKcal:0} kcal. That leaves about {left:0} kcal for carbs and fat (you have {carbs.Remaining:0} g carbs and {fat.Remaining:0} g fat left).");
            }
            else
            {
                tips.Add($"Your remaining {protein.Remaining:0} g protein (≈{proteinKcal:0} kcal) is more than your {cal.Remaining:0} kcal left. Lean protein foods fit more protein into fewer calories. It's fine if today doesn't hit every number.");
            }
        }
        else
        {
            tips.Add($"Protein target reached. You have {cal.Remaining:0} kcal left for the rest of the day.");
        }
        return tips;
    }

    public async Task<List<DailyTotals>> GetDailyTotalsAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.FoodDiaryItems.AsNoTracking()
            .Where(i => i.Entry!.UserId == userId && i.Entry.DiaryDate >= from && i.Entry.DiaryDate <= to)
            .GroupBy(i => i.Entry!.DiaryDate)
            .Select(g => new
            {
                Date = g.Key,
                Calories = g.Sum(i => i.Calories),
                Protein = g.Sum(i => i.ProteinGrams),
                Carbs = g.Sum(i => i.CarbohydratesGrams),
                Fat = g.Sum(i => i.FatGrams),
            })
            .OrderBy(r => r.Date).ToListAsync(ct);
        return rows.Select(r => new DailyTotals(r.Date, new NutrientTotals(r.Calories, r.Protein, r.Carbs, r.Fat))).ToList();
    }
}
