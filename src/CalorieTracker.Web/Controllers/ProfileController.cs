using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Controllers;

public class ProfileController(ApplicationDbContext db, INutritionCalculatorService calc, INutritionPlanService plans, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        if (await db.UserProfiles.AnyAsync(p => p.UserId == this.UserId(), ct)) return RedirectToAction(nameof(Index));
        return View(new ProfileSetupViewModel { IsNew = true });
    }

    [HttpPost]
    public Task<IActionResult> Setup(ProfileSetupViewModel vm, CancellationToken ct) => Save(vm, isNew: true, ct);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = this.UserId();
        var p = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (p is null) return RedirectToAction(nameof(Setup));
        var weight = await db.WeightLogs.AsNoTracking().Where(w => w.UserId == userId).OrderByDescending(w => w.Date).FirstOrDefaultAsync(ct);
        var (ft, inch) = calc.ToFeetInches(p.HeightCm);
        return View(new ProfileSetupViewModel
        {
            SexForBmrCalculation = p.SexForBmrCalculation,
            DateOfBirth = p.DateOfBirth,
            CurrentWeight = weight is null ? null : Math.Round(calc.FromKg(weight.WeightKg, p.PreferredWeightUnit), 1),
            PreferredWeightUnit = p.PreferredWeightUnit,
            HeightUnit = p.HeightUnit,
            HeightCm = Math.Round(p.HeightCm, 1),
            HeightFeet = ft,
            HeightInches = inch,
            ActivityLevel = p.ActivityLevel,
            WorkoutDaysPerWeek = p.WorkoutDaysPerWeek,
            AverageWorkoutDurationMinutes = p.AverageWorkoutDurationMinutes,
            WorkoutIntensity = p.WorkoutIntensity,
            PrimaryGoal = p.PrimaryGoal,
            TargetWeight = p.TargetWeightKg is null ? null : Math.Round(calc.FromKg(p.TargetWeightKg.Value, p.PreferredWeightUnit), 1),
            TargetBodyFatPercentage = p.TargetBodyFatPercentage.Normalize(),
            PreferredCalorieDeficitPercentage = p.PreferredCalorieDeficitPercentage.Normalize(),
            PreferredCalorieSurplusPercentage = p.PreferredCalorieSurplusPercentage.Normalize(),
            ProteinTargetGramsPerKg = p.ProteinTargetGramsPerKg.Normalize(),
            FatTargetPercentage = p.FatTargetPercentage.Normalize(),
            TimeZoneId = p.TimeZoneId,
        });
    }

    [HttpPost]
    public Task<IActionResult> Index(ProfileSetupViewModel vm, CancellationToken ct) => Save(vm, isNew: false, ct);

    private async Task<IActionResult> Save(ProfileSetupViewModel vm, bool isNew, CancellationToken ct)
    {
        vm.IsNew = isNew;
        if (!ModelState.IsValid) return View(isNew ? nameof(Setup) : nameof(Index), vm);

        var userId = this.UserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var p = await db.UserProfiles.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (p is null) db.UserProfiles.Add(p = new UserProfile { UserId = userId, CreatedAtUtc = now });

        p.SexForBmrCalculation = vm.SexForBmrCalculation;
        p.DateOfBirth = vm.DateOfBirth!.Value;
        p.HeightUnit = vm.HeightUnit;
        p.HeightCm = vm.HeightUnit == HeightUnit.Cm ? vm.HeightCm!.Value : calc.ToCm(vm.HeightFeet!.Value, vm.HeightInches ?? 0);
        p.PreferredWeightUnit = vm.PreferredWeightUnit;
        p.ActivityLevel = vm.ActivityLevel;
        p.WorkoutDaysPerWeek = vm.WorkoutDaysPerWeek;
        p.AverageWorkoutDurationMinutes = vm.AverageWorkoutDurationMinutes;
        p.WorkoutIntensity = vm.WorkoutIntensity;
        p.PrimaryGoal = vm.PrimaryGoal;
        p.TargetWeightKg = vm.TargetWeight is null ? null : calc.ToKg(vm.TargetWeight.Value, vm.PreferredWeightUnit);
        p.TargetBodyFatPercentage = vm.TargetBodyFatPercentage;
        p.PreferredCalorieDeficitPercentage = vm.PreferredCalorieDeficitPercentage;
        p.PreferredCalorieSurplusPercentage = vm.PreferredCalorieSurplusPercentage;
        p.ProteinTargetGramsPerKg = vm.ProteinTargetGramsPerKg;
        p.FatTargetPercentage = vm.FatTargetPercentage;
        p.TimeZoneId = vm.TimeZoneId;
        p.UpdatedAtUtc = now;

        if (p.HeightCm is < 90 or > 250)
        {
            ModelState.AddModelError("", "Height must be between 90 and 250 cm (about 3'0\" to 8'2\").");
            return View(isNew ? nameof(Setup) : nameof(Index), vm);
        }

        // The current weight is stored as today's weight log entry, the single source of truth for weight.
        var today = plans.Today(p);
        var weightKg = calc.ToKg(vm.CurrentWeight!.Value, vm.PreferredWeightUnit);
        var latest = await db.WeightLogs.Where(w => w.UserId == userId).OrderByDescending(w => w.Date).FirstOrDefaultAsync(ct);
        if (latest is null || Math.Abs(calc.FromKg(latest.WeightKg, vm.PreferredWeightUnit) - vm.CurrentWeight.Value) >= 0.05m)
        {
            var todayLog = latest?.Date == today ? latest : null;
            if (todayLog is null) db.WeightLogs.Add(todayLog = new WeightLog { UserId = userId, Date = today, CreatedAtUtc = now });
            todayLog.WeightKg = weightKg;
        }
        await db.SaveChangesAsync(ct);

        var plan = await plans.GetAsync(userId, ct);
        if (plan?.Plan?.Macros.Targets is { } targets) await plans.RecordGoalSnapshotAsync(userId, plan.Today, targets, ct);

        this.Flash(isNew ? "Profile saved. Here are your estimated targets." : "Profile updated. Your targets have been recalculated.");
        return isNew ? RedirectToAction("Index", "NutritionGoals") : RedirectToAction(nameof(Index));
    }
}
