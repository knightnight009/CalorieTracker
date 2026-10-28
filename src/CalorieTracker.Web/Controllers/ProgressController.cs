using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Controllers;

public class ProgressController(ApplicationDbContext db, IDashboardService dashboard, INutritionPlanService plans,
    INutritionCalculatorService calc, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index(string? range, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var userId = this.UserId();
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var today = plans.Today(profile);
        var unit = profile?.PreferredWeightUnit ?? WeightUnit.Kg;

        range = range is "7" or "30" or "90" or "custom" ? range : "30";
        DateOnly start, end;
        if (range == "custom" && from is not null && to is not null && from <= to)
        {
            start = from.Value;
            end = to.Value.DayNumber - start.DayNumber > 366 ? start.AddDays(366) : to.Value;
        }
        else
        {
            if (range == "custom") range = "30";
            end = today;
            start = today.AddDays(-int.Parse(range) + 1);
        }

        var totals = (await dashboard.GetDailyTotalsAsync(userId, start, end, ct)).ToDictionary(d => d.Date, d => d.Totals);
        var snapshots = await db.NutritionGoalSnapshots.AsNoTracking().Where(s => s.UserId == userId && s.EffectiveDate <= end)
            .OrderBy(s => s.EffectiveDate).ToListAsync(ct);
        var weights = await db.WeightLogs.AsNoTracking().Where(w => w.UserId == userId && w.Date >= start && w.Date <= end)
            .OrderBy(w => w.Date).ToListAsync(ct);

        var vm = new ProgressViewModel { Range = range, From = start, To = end, WeightUnit = unit, TotalDays = end.DayNumber - start.DayNumber + 1 };
        var adherenceDays = 0;
        var eligible = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            totals.TryGetValue(d, out var t);
            var goal = snapshots.LastOrDefault(s => s.EffectiveDate <= d);
            vm.Labels.Add(d.ToString("MMM d"));
            // Days with nothing logged are gaps (null), not zero intake.
            vm.Calories.Add(t is null ? null : Math.Round(t.Calories));
            vm.Protein.Add(t is null ? null : Math.Round(t.ProteinGrams, 1));
            vm.CalorieGoal.Add(goal?.Calories);
            vm.ProteinGoal.Add(goal?.ProteinGrams);
            if (t is not null && goal is not null)
            {
                eligible++;
                if (Math.Abs(t.Calories - goal.Calories) <= goal.Calories * 0.10m && t.ProteinGrams >= goal.ProteinGrams * 0.9m) adherenceDays++;
            }
        }
        vm.LoggedDays = totals.Count;
        vm.AdherenceDays = adherenceDays;
        vm.AdherenceEligibleDays = eligible;
        vm.AvgCalories = calc.AverageOfLoggedDays(vm.Calories);
        vm.AvgProtein = calc.AverageOfLoggedDays(vm.Protein);

        if (totals.Count > 0)
        {
            var sum = totals.Values.Aggregate(NutrientTotals.Zero, (a, b) => a + b);
            var kcal = sum.ProteinGrams * 4 + sum.CarbohydratesGrams * 4 + sum.FatGrams * 9;
            if (kcal > 0) vm.MacroKcalShare = (sum.ProteinGrams * 4 / kcal * 100, sum.CarbohydratesGrams * 4 / kcal * 100, sum.FatGrams * 9 / kcal * 100);
        }

        vm.Weeks = Enumerable.Range(0, vm.TotalDays).Select(i => start.AddDays(i))
            .GroupBy(d => d.AddDays(-(((int)d.DayOfWeek + 6) % 7))) // Monday-start weeks
            .Select(g => new WeekSummary(g.Key,
                calc.AverageOfLoggedDays(g.Select(d => totals.TryGetValue(d, out var t) ? t.Calories : (decimal?)null)),
                calc.AverageOfLoggedDays(g.Select(d => totals.TryGetValue(d, out var t) ? t.ProteinGrams : (decimal?)null)),
                g.Count(totals.ContainsKey)))
            .ToList();

        vm.WeightLabels = weights.Select(w => w.Date.ToString("MMM d")).ToList();
        vm.Weights = weights.Select(w => Math.Round(calc.FromKg(w.WeightKg, unit), 1)).ToList();
        vm.BodyFat = weights.Select(w => w.BodyFatPercentage).ToList();
        if (weights.Count >= 2) vm.WeightChange = calc.FromKg(weights[^1].WeightKg - weights[0].WeightKg, unit);

        vm.WeightLogs = await db.WeightLogs.AsNoTracking().Where(w => w.UserId == userId).OrderByDescending(w => w.Date).Take(30).ToListAsync(ct);
        vm.WeightInput = new WeightLogInput { Date = today };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> LogWeight([Bind(Prefix = "WeightInput")] WeightLogInput input, CancellationToken ct)
    {
        var userId = this.UserId();
        if (!ModelState.IsValid)
        {
            this.Flash("Please enter a valid weight and date.", "warning");
            return RedirectToAction(nameof(Index));
        }
        var unit = (await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct))?.PreferredWeightUnit ?? WeightUnit.Kg;
        var log = await db.WeightLogs.SingleOrDefaultAsync(w => w.UserId == userId && w.Date == input.Date, ct);
        if (log is null) db.WeightLogs.Add(log = new WeightLog { UserId = userId, Date = input.Date, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        log.WeightKg = calc.ToKg(input.Weight!.Value, unit);
        log.BodyFatPercentage = input.BodyFatPercentage;
        log.Notes = input.Notes?.Trim();
        await db.SaveChangesAsync(ct);

        // Weight drives the protein target and BMR, so refresh today's goal snapshot.
        var plan = await plans.GetAsync(userId, ct);
        if (plan?.Plan?.Macros.Targets is { } targets) await plans.RecordGoalSnapshotAsync(userId, plan.Today, targets, ct);

        this.Flash("Weight saved. Day-to-day changes are mostly water and food volume; look at the trend over weeks.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteWeight(int id, CancellationToken ct)
    {
        var log = await db.WeightLogs.SingleOrDefaultAsync(w => w.Id == id && w.UserId == this.UserId(), ct);
        if (log is null) return NotFound();
        db.WeightLogs.Remove(log);
        await db.SaveChangesAsync(ct);
        this.Flash("Weight entry deleted.");
        return RedirectToAction(nameof(Index));
    }
}
