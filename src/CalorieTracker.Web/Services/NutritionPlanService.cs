using CalorieTracker.Web.Data;
using CalorieTracker.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace CalorieTracker.Web.Services;

public record UserPlan(UserProfile Profile, WeightLog? LatestWeight, DateOnly Today, NutritionPlan? Plan, string? Problem);

public interface INutritionPlanService
{
    DateOnly Today(UserProfile? profile);
    Task<UserPlan?> GetAsync(string userId, CancellationToken ct = default);
    Task RecordGoalSnapshotAsync(string userId, DateOnly today, MacroTargets targets, CancellationToken ct = default);
}

public class NutritionPlanService(ApplicationDbContext db, INutritionCalculatorService calc, TimeProvider clock) : INutritionPlanService
{
    public DateOnly Today(UserProfile? profile) => TodayIn(profile?.TimeZoneId, clock.GetUtcNow().UtcDateTime);

    public static DateOnly TodayIn(string? timeZoneId, DateTime utcNow)
    {
        var tz = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found)) tz = found;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
    }

    public async Task<UserPlan?> GetAsync(string userId, CancellationToken ct = default)
    {
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return null;
        var today = Today(profile);
        var weight = await db.WeightLogs.AsNoTracking().Where(w => w.UserId == userId && w.Date <= today)
            .OrderByDescending(w => w.Date).FirstOrDefaultAsync(ct);
        if (weight is null) return new UserPlan(profile, null, today, null, "Log your current weight to calculate targets.");

        try
        {
            var plan = calc.CalculatePlan(new PlanInput(profile.SexForBmrCalculation, profile.DateOfBirth, today, weight.WeightKg,
                profile.HeightCm, profile.ActivityLevel, profile.PrimaryGoal, profile.CalorieTargetMode,
                profile.PreferredCalorieDeficitPercentage, profile.PreferredCalorieSurplusPercentage, profile.DailyCalorieTargetOverride,
                profile.ProteinTargetGramsPerKg, profile.FatTargetPercentage,
                profile.CustomProteinGrams, profile.CustomCarbohydratesGrams, profile.CustomFatGrams));
            return new UserPlan(profile, weight, today, plan, null);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new UserPlan(profile, weight, today, null, $"Your profile has a value we can't calculate with ({ex.ParamName}). Please review your profile and goals.");
        }
    }

    /// <summary>One snapshot per day; the last save of the day wins.</summary>
    public async Task RecordGoalSnapshotAsync(string userId, DateOnly today, MacroTargets t, CancellationToken ct = default)
    {
        var snap = await db.NutritionGoalSnapshots.SingleOrDefaultAsync(s => s.UserId == userId && s.EffectiveDate == today, ct);
        if (snap is null) db.NutritionGoalSnapshots.Add(snap = new NutritionGoalSnapshot { UserId = userId, EffectiveDate = today });
        snap.Calories = t.Calories;
        snap.ProteinGrams = t.ProteinGrams;
        snap.CarbohydratesGrams = t.CarbohydratesGrams;
        snap.FatGrams = t.FatGrams;
        snap.CreatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}
