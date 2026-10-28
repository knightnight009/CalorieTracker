using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Controllers;

public class NutritionGoalsController(ApplicationDbContext db, INutritionCalculatorService calc, INutritionPlanService plans, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var plan = await plans.GetAsync(this.UserId(), ct);
        if (plan is null) return RedirectToAction("Setup", "Profile");
        var p = plan.Profile;
        return View(Build(plan, new GoalSettingsInput
        {
            PrimaryGoal = p.PrimaryGoal,
            CalorieTargetMode = p.CalorieTargetMode,
            DeficitPercentage = p.PreferredCalorieDeficitPercentage.Normalize(),
            SurplusPercentage = p.PreferredCalorieSurplusPercentage.Normalize(),
            ManualCalories = p.DailyCalorieTargetOverride,
            ProteinGramsPerKg = p.ProteinTargetGramsPerKg.Normalize(),
            FatPercentage = p.FatTargetPercentage.Normalize(),
            CustomProteinGrams = p.CustomProteinGrams.Normalize(),
            CustomCarbohydratesGrams = p.CustomCarbohydratesGrams.Normalize(),
            CustomFatGrams = p.CustomFatGrams.Normalize(),
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Index([Bind(Prefix = "Input")] GoalSettingsInput input, CancellationToken ct)
    {
        var userId = this.UserId();
        var current = await plans.GetAsync(userId, ct);
        if (current is null) return RedirectToAction("Setup", "Profile");
        if (current.LatestWeight is null) return RedirectToAction("Index", "Progress");

        if (ModelState.IsValid)
        {
            // Validate the proposed settings with the real calculator before saving anything.
            var p = current.Profile;
            var proposed = calc.CalculatePlan(new PlanInput(p.SexForBmrCalculation, p.DateOfBirth, current.Today, current.LatestWeight.WeightKg,
                p.HeightCm, p.ActivityLevel, input.PrimaryGoal, input.CalorieTargetMode, input.DeficitPercentage, input.SurplusPercentage,
                input.ManualCalories, input.ProteinGramsPerKg, input.FatPercentage,
                input.CustomProteinGrams, input.CustomCarbohydratesGrams, input.CustomFatGrams));
            foreach (var e in proposed.Macros.Errors) ModelState.AddModelError("", e);

            if (proposed.Macros.Targets is { } targets)
            {
                var entity = await db.UserProfiles.SingleAsync(x => x.UserId == userId, ct);
                entity.PrimaryGoal = input.PrimaryGoal;
                entity.CalorieTargetMode = input.CalorieTargetMode;
                entity.PreferredCalorieDeficitPercentage = input.DeficitPercentage;
                entity.PreferredCalorieSurplusPercentage = input.SurplusPercentage;
                entity.DailyCalorieTargetOverride = input.CalorieTargetMode == CalorieTargetMode.Manual ? input.ManualCalories : null;
                entity.ProteinTargetGramsPerKg = input.ProteinGramsPerKg;
                entity.FatTargetPercentage = input.FatPercentage;
                entity.CustomProteinGrams = input.CustomProteinGrams;
                entity.CustomCarbohydratesGrams = input.CustomCarbohydratesGrams;
                entity.CustomFatGrams = input.CustomFatGrams;
                entity.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                await plans.RecordGoalSnapshotAsync(userId, current.Today, targets, ct);
                this.Flash("Goals saved.");
                return RedirectToAction(nameof(Index));
            }
        }
        return View(Build(current, input));
    }

    private NutritionGoalsViewModel Build(UserPlan plan, GoalSettingsInput input)
    {
        var vm = new NutritionGoalsViewModel
        {
            UserPlan = plan,
            Input = input,
            ActivityFactors = Enum.GetValues<ActivityLevel>().Select(l => (l, calc.ActivityFactor(l))).ToList(),
        };
        if (plan.Plan is { } np)
            vm.DeficitOptions = new[] { 10m, 15m, 20m, 25m }.Select(pct =>
            {
                var cals = (int)Math.Round(np.Tdee * (1 - pct / 100));
                return new DeficitOption(pct, cals, np.Tdee - cals, calc.EstimatedWeeklyWeightChangeKg(cals - np.Tdee));
            }).ToList();
        return vm;
    }
}
