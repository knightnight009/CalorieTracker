using CalorieTracker.Web.Data;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Services;

public record DailyTotals(DateOnly Date, NutrientTotals Totals);

public interface IDashboardService
{
    Task<List<FoodDiaryEntry>> GetDayEntriesAsync(string userId, DateOnly date, CancellationToken ct = default);
    Task<DashboardViewModel?> BuildAsync(string userId, CancellationToken ct = default);
    /// <summary>Totals for days that have at least one logged item; days without food are absent, not zero.</summary>
    Task<List<DailyTotals>> GetDailyTotalsAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default);
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
