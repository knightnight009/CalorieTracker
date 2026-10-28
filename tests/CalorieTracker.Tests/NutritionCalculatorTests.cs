using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;

namespace CalorieTracker.Tests;

public class NutritionCalculatorTests
{
    private readonly NutritionCalculatorService _calc = new(new NutritionOptions());

    [Fact]
    public void Bmr_Male_MatchesMifflinStJeor() =>
        Assert.Equal(1770m, _calc.CalculateBmr(BiologicalSex.Male, 82, 180, 36)); // 820 + 1125 - 180 + 5

    [Fact]
    public void Bmr_Female_MatchesMifflinStJeor() =>
        Assert.Equal(1320.25m, _calc.CalculateBmr(BiologicalSex.Female, 60, 165, 30)); // 600 + 1031.25 - 150 - 161

    [Theory]
    [InlineData(0, 180, 30)]
    [InlineData(-5, 180, 30)]
    [InlineData(80, 0, 30)]
    [InlineData(80, 180, -1)]
    [InlineData(80, 180, 121)]
    public void Bmr_RejectsInvalidInputs(decimal kg, decimal cm, int age) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _calc.CalculateBmr(BiologicalSex.Male, kg, cm, age));

    [Theory]
    [InlineData(ActivityLevel.Sedentary, 1.2)]
    [InlineData(ActivityLevel.LightlyActive, 1.375)]
    [InlineData(ActivityLevel.ModeratelyActive, 1.55)]
    [InlineData(ActivityLevel.VeryActive, 1.725)]
    [InlineData(ActivityLevel.ExtraActive, 1.9)]
    public void Tdee_IsBmrTimesActivityFactor(ActivityLevel level, double factor) =>
        Assert.Equal(1770m * (decimal)factor, _calc.CalculateTdee(1770, level));

    [Fact]
    public void ActivityFactors_AreConfigurable()
    {
        var calc = new NutritionCalculatorService(new NutritionOptions { ActivityFactors = new() { [ActivityLevel.Sedentary] = 1.1m } });
        Assert.Equal(1100m, calc.CalculateTdee(1000, ActivityLevel.Sedentary));
    }

    [Theory]
    [InlineData("1990-05-15", "2026-05-14", 35)] // day before birthday
    [InlineData("1990-05-15", "2026-05-15", 36)] // on birthday
    [InlineData("2000-02-29", "2025-02-28", 24)] // leap-day birthday, non-leap year
    [InlineData("2000-02-29", "2025-03-01", 25)]
    public void Age_CountsCompletedYears(string dob, string today, int expected) =>
        Assert.Equal(expected, _calc.AgeInYears(DateOnly.Parse(dob), DateOnly.Parse(today)));

    [Fact]
    public void Age_RejectsFutureBirthDate() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _calc.AgeInYears(new DateOnly(2030, 1, 1), new DateOnly(2026, 1, 1)));

    [Fact]
    public void Units_ConvertExactly()
    {
        Assert.Equal(45.359237m, _calc.ToKg(100, WeightUnit.Lb));
        Assert.Equal(80m, _calc.ToKg(80, WeightUnit.Kg));
        Assert.Equal(100m, Math.Round(_calc.FromKg(45.359237m, WeightUnit.Lb), 10));
        Assert.Equal(180.34m, _calc.ToCm(5, 11)); // 71 in × 2.54
        Assert.Equal((5, 11.0m), _calc.ToFeetInches(180.34m));
    }

    [Fact]
    public void CalorieGoal_FatLoss_AppliesPercentDeficit()
    {
        var r = _calc.CalculateCalorieGoal(2743.5m, 1770, PrimaryGoal.LoseFat, CalorieTargetMode.Percentage, 15, 10, null, 82);
        Assert.Equal(2332, r.DailyCalorieGoal); // 2743.5 × 0.85 = 2331.975
        Assert.Equal(2332 - 2743.5m, r.AdjustmentCalories);
        Assert.Equal(Math.Round(r.AdjustmentCalories * 7 / 7700, 6), Math.Round(r.EstimatedWeeklyWeightChangeKg, 6));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void CalorieGoal_Maintain_EqualsTdee()
    {
        var r = _calc.CalculateCalorieGoal(2500, 1700, PrimaryGoal.Maintain, CalorieTargetMode.Percentage, 15, 10, null, 80);
        Assert.Equal(2500, r.DailyCalorieGoal);
        Assert.Equal(0, r.AdjustmentCalories);
    }

    [Fact]
    public void CalorieGoal_Gain_AppliesSurplus()
    {
        var r = _calc.CalculateCalorieGoal(2500, 1700, PrimaryGoal.GainMuscle, CalorieTargetMode.Percentage, 15, 10, null, 80);
        Assert.Equal(2750, r.DailyCalorieGoal);
        Assert.True(r.EstimatedWeeklyWeightChangeKg > 0);
    }

    [Fact]
    public void CalorieGoal_Manual_BelowBmr_IsFlaggedNotBlocked()
    {
        var r = _calc.CalculateCalorieGoal(2500, 1700, PrimaryGoal.LoseFat, CalorieTargetMode.Manual, 15, 10, 1400, 80);
        Assert.Equal(1400, r.DailyCalorieGoal);
        Assert.Contains(r.Warnings, w => w.Contains("below your estimated BMR"));
        Assert.Contains(r.Warnings, w => w.Contains("large deficit"));
    }

    [Fact]
    public void CalorieGoal_Manual_RequiresPositiveTarget() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _calc.CalculateCalorieGoal(2500, 1700, PrimaryGoal.LoseFat, CalorieTargetMode.Manual, 15, 10, null, 80));

    [Fact]
    public void Macros_ProteinThenFatThenCarbs()
    {
        var r = _calc.CalculateMacros(2332, 82, 1.8m, 30);
        Assert.True(r.IsValid);
        var t = r.Targets!;
        Assert.Equal(148, t.ProteinGrams);           // 147.6
        Assert.Equal(78, t.FatGrams);                // 2332 × 0.30 / 9 = 77.73
        Assert.Equal(260, t.CarbohydratesGrams);     // (2332 − 592 − 702) / 4 = 259.5
        Assert.Equal(2334, t.MacroCalories);
        Assert.Equal(2, t.RoundingDifference);
    }

    [Fact]
    public void Macros_NegativeCarbs_ReturnsValidationError()
    {
        // 150 kg × 3.0 g/kg = 450 g protein = 1800 kcal, plus 40% fat of 1500 kcal > 1500.
        var r = _calc.CalculateMacros(1500, 150, 3.0m, 40);
        Assert.False(r.IsValid);
        Assert.Contains("more than your 1500 kcal goal", r.Errors.Single());
    }

    [Theory]
    [InlineData(0.5, 30)]
    [InlineData(4.0, 30)]
    [InlineData(1.8, 10)]
    [InlineData(1.8, 70)]
    public void Macros_OutOfRangeSettings_AreRejected(decimal proteinPerKg, decimal fatPct) =>
        Assert.False(_calc.CalculateMacros(2000, 80, proteinPerKg, fatPct).IsValid);

    [Fact]
    public void Macros_CustomGrams_MustMatchCalories()
    {
        Assert.True(_calc.CalculateMacros(2000, 80, 1.8m, 30, 150, 200, 67).IsValid);   // 600+800+603 = 2003
        Assert.False(_calc.CalculateMacros(2000, 80, 1.8m, 30, 150, 300, 67).IsValid);  // 2403
        Assert.False(_calc.CalculateMacros(2000, 80, 1.8m, 30, 150, null, 67).IsValid); // partial
    }

    [Fact]
    public void Plan_EndToEnd()
    {
        var p = _calc.CalculatePlan(new PlanInput(BiologicalSex.Male, new DateOnly(1990, 5, 15), new DateOnly(2026, 10, 27), 82, 180,
            ActivityLevel.ModeratelyActive, PrimaryGoal.LoseFat, CalorieTargetMode.Percentage, 15, 10, null, 1.8m, 30));
        Assert.Equal(36, p.AgeYears);
        Assert.Equal(1770m, p.Bmr);
        Assert.Equal(2743.5m, p.Tdee);
        Assert.Equal(2332, p.Goal.DailyCalorieGoal);
        Assert.Equal(148, p.Macros.Targets!.ProteinGrams);
    }

    [Fact]
    public void Scale_UsesReferenceQuantity()
    {
        var per100g = new Food { Calories = 165, ProteinGrams = 31, CarbohydratesGrams = 0, FatGrams = 3.6m, FiberGrams = 0 };
        var n = _calc.Scale(per100g, 100, 150);
        Assert.Equal(247.5m, n.Calories);
        Assert.Equal(46.5m, n.ProteinGrams);
        Assert.Equal(5.4m, n.FatGrams);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -2)]
    public void Scale_RejectsInvalidQuantities(decimal reference, decimal actual) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _calc.Scale(new Food(), reference, actual));

    [Fact]
    public void Sum_AddsItems()
    {
        var items = new[]
        {
            new FoodDiaryItem { Calories = 205, ProteinGrams = 4.3m, CarbohydratesGrams = 44.5m, FatGrams = 0.4m },
            new FoodDiaryItem { Calories = 198, ProteinGrams = 37, CarbohydratesGrams = 0, FatGrams = 4.3m, FiberGrams = 1 },
        };
        var t = _calc.Sum(items);
        Assert.Equal(403, t.Calories);
        Assert.Equal(41.3m, t.ProteinGrams);
        Assert.Equal(4.7m, t.FatGrams);
        Assert.Equal(1, t.FiberGrams);
    }

    [Fact]
    public void Remaining_NeverNegative_ShowsExcess()
    {
        var under = _calc.Remaining(140, 95);
        Assert.Equal(45, under.Remaining);
        Assert.Equal(0, under.Excess);
        Assert.False(under.IsOver);

        var over = _calc.Remaining(2000, 2350);
        Assert.Equal(0, over.Remaining);
        Assert.Equal(350, over.Excess);
        Assert.True(over.IsOver);
        Assert.Equal(117.5m, over.PercentConsumed);
    }

    [Fact]
    public void Average_IgnoresUnloggedDays()
    {
        Assert.Equal(2000m, _calc.AverageOfLoggedDays([1800, null, 2200, null]));
        Assert.Null(_calc.AverageOfLoggedDays([null, null]));
    }

    [Fact]
    public void GapSuggestions_OverTarget_DoesNotShame()
    {
        var tips = DashboardService.GapSuggestions(new(2000, 2300), new(140, 150), new(200, 250), new(70, 80));
        Assert.Contains("300 kcal over", tips.Single());
        Assert.Contains("weekly average", tips.Single());
    }

    [Fact]
    public void GapSuggestions_UsesActualRemaining()
    {
        var tips = DashboardService.GapSuggestions(new(2000, 1480), new(140, 95), new(200, 120), new(70, 50));
        Assert.Contains("45 g protein is about 180 kcal", tips.Single());
        Assert.Contains("340 kcal for carbs and fat", tips.Single());
    }
}
