using CalorieTracker.Web.Models;

namespace CalorieTracker.Web.Services;

public class NutritionOptions
{
    public Dictionary<ActivityLevel, decimal> ActivityFactors { get; set; } = new()
    {
        [ActivityLevel.Sedentary] = 1.2m,
        [ActivityLevel.LightlyActive] = 1.375m,
        [ActivityLevel.ModeratelyActive] = 1.55m,
        [ActivityLevel.VeryActive] = 1.725m,
        [ActivityLevel.ExtraActive] = 1.9m,
    };
    /// <summary>Rough energy content of body-weight change. An approximation, not a law of physiology.</summary>
    public decimal KcalPerKgBodyWeight { get; set; } = 7700m;
}

public record NutrientTotals(decimal Calories, decimal ProteinGrams, decimal CarbohydratesGrams, decimal FatGrams,
    decimal FiberGrams = 0, decimal SugarGrams = 0)
{
    public static readonly NutrientTotals Zero = new(0, 0, 0, 0);
    public static NutrientTotals From(INutrients n) =>
        new(n.Calories, n.ProteinGrams, n.CarbohydratesGrams, n.FatGrams, n.FiberGrams ?? 0, n.SugarGrams ?? 0);
    public static NutrientTotals operator +(NutrientTotals a, NutrientTotals b) =>
        new(a.Calories + b.Calories, a.ProteinGrams + b.ProteinGrams, a.CarbohydratesGrams + b.CarbohydratesGrams,
            a.FatGrams + b.FatGrams, a.FiberGrams + b.FiberGrams, a.SugarGrams + b.SugarGrams);
}

/// <summary>Remaining vs. exceeded, never a negative "remaining".</summary>
public record RemainingAmount(decimal Target, decimal Consumed)
{
    public decimal Remaining => Math.Max(0, Target - Consumed);
    public decimal Excess => Math.Max(0, Consumed - Target);
    public bool IsOver => Consumed > Target;
    public decimal PercentConsumed => Target <= 0 ? 0 : Consumed / Target * 100;
}

public record CalorieGoalResult(
    decimal Tdee,
    int DailyCalorieGoal,
    /// <summary>Negative for a deficit, positive for a surplus.</summary>
    decimal AdjustmentCalories,
    decimal AdjustmentPercentage,
    decimal EstimatedWeeklyWeightChangeKg,
    IReadOnlyList<string> Warnings);

public record MacroTargets(int Calories, int ProteinGrams, int CarbohydratesGrams, int FatGrams)
{
    public int MacroCalories => ProteinGrams * 4 + CarbohydratesGrams * 4 + FatGrams * 9;
    /// <summary>Calories the rounded gram targets add up to, minus the calorie goal.</summary>
    public int RoundingDifference => MacroCalories - Calories;
}

public record MacroResult(MacroTargets? Targets, IReadOnlyList<string> Errors)
{
    public bool IsValid => Targets is not null;
}

public record PlanInput(
    BiologicalSex Sex, DateOnly DateOfBirth, DateOnly Today, decimal WeightKg, decimal HeightCm,
    ActivityLevel ActivityLevel, PrimaryGoal Goal, CalorieTargetMode Mode,
    decimal DeficitPercentage, decimal SurplusPercentage, int? ManualCalories,
    decimal ProteinGramsPerKg, decimal FatPercentage,
    decimal? CustomProteinGrams = null, decimal? CustomCarbohydratesGrams = null, decimal? CustomFatGrams = null);

public record NutritionPlan(int AgeYears, decimal Bmr, ActivityLevel ActivityLevel, decimal ActivityFactor, decimal Tdee,
    CalorieGoalResult Goal, MacroResult Macros);

public interface INutritionCalculatorService
{
    decimal ToKg(decimal value, WeightUnit unit);
    decimal FromKg(decimal kg, WeightUnit unit);
    decimal ToCm(decimal feet, decimal inches);
    (int Feet, decimal Inches) ToFeetInches(decimal cm);
    int AgeInYears(DateOnly dateOfBirth, DateOnly today);
    decimal CalculateBmr(BiologicalSex sex, decimal weightKg, decimal heightCm, int ageYears);
    decimal ActivityFactor(ActivityLevel level);
    decimal CalculateTdee(decimal bmr, ActivityLevel level);
    CalorieGoalResult CalculateCalorieGoal(decimal tdee, decimal bmr, PrimaryGoal goal, CalorieTargetMode mode,
        decimal deficitPercentage, decimal surplusPercentage, int? manualCalories, decimal weightKg);
    decimal EstimatedWeeklyWeightChangeKg(decimal dailyCalorieAdjustment);
    MacroResult CalculateMacros(int calories, decimal weightKg, decimal proteinGramsPerKg, decimal fatPercentage,
        decimal? customProtein = null, decimal? customCarbs = null, decimal? customFat = null);
    NutritionPlan CalculatePlan(PlanInput input);
    NutrientTotals Scale(INutrients perReference, decimal referenceQuantity, decimal actualQuantity);
    NutrientTotals Sum(IEnumerable<INutrients> items);
    RemainingAmount Remaining(decimal target, decimal consumed);
    decimal? AverageOfLoggedDays(IEnumerable<decimal?> dailyValues);
}

public class NutritionCalculatorService(NutritionOptions options) : INutritionCalculatorService
{
    public const decimal KgPerLb = 0.45359237m;
    public const decimal CmPerInch = 2.54m;
    public const decimal ProteinKcalPerGram = 4, CarbKcalPerGram = 4, FatKcalPerGram = 9;

    public decimal ToKg(decimal value, WeightUnit unit) => unit == WeightUnit.Kg ? value : value * KgPerLb;
    public decimal FromKg(decimal kg, WeightUnit unit) => unit == WeightUnit.Kg ? kg : kg / KgPerLb;
    public decimal ToCm(decimal feet, decimal inches) => (feet * 12 + inches) * CmPerInch;

    public (int Feet, decimal Inches) ToFeetInches(decimal cm)
    {
        var totalInches = cm / CmPerInch;
        var feet = (int)Math.Floor(totalInches / 12);
        return (feet, Math.Round(totalInches - feet * 12, 1));
    }

    public int AgeInYears(DateOnly dateOfBirth, DateOnly today)
    {
        if (dateOfBirth > today) throw new ArgumentOutOfRangeException(nameof(dateOfBirth), "Date of birth is in the future.");
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age)) age--;
        return age;
    }

    /// <summary>Mifflin-St Jeor. An estimate; individual BMR can differ noticeably.</summary>
    public decimal CalculateBmr(BiologicalSex sex, decimal weightKg, decimal heightCm, int ageYears)
    {
        if (weightKg <= 0) throw new ArgumentOutOfRangeException(nameof(weightKg), "Weight must be positive.");
        if (heightCm <= 0) throw new ArgumentOutOfRangeException(nameof(heightCm), "Height must be positive.");
        if (ageYears is < 0 or > 120) throw new ArgumentOutOfRangeException(nameof(ageYears), "Age is out of range.");
        var bmr = 10 * weightKg + 6.25m * heightCm - 5 * ageYears;
        return sex == BiologicalSex.Male ? bmr + 5 : bmr - 161;
    }

    public decimal ActivityFactor(ActivityLevel level) =>
        options.ActivityFactors.TryGetValue(level, out var f) ? f : throw new ArgumentOutOfRangeException(nameof(level));

    // Workout days are informational only: the activity factor already covers exercise, so nothing is added on top.
    public decimal CalculateTdee(decimal bmr, ActivityLevel level) => bmr * ActivityFactor(level);

    public decimal EstimatedWeeklyWeightChangeKg(decimal dailyCalorieAdjustment) =>
        dailyCalorieAdjustment * 7 / options.KcalPerKgBodyWeight;

    public CalorieGoalResult CalculateCalorieGoal(decimal tdee, decimal bmr, PrimaryGoal goal, CalorieTargetMode mode,
        decimal deficitPercentage, decimal surplusPercentage, int? manualCalories, decimal weightKg)
    {
        if (tdee <= 0) throw new ArgumentOutOfRangeException(nameof(tdee));
        var warnings = new List<string>();
        decimal target;

        if (mode == CalorieTargetMode.Manual)
        {
            if (manualCalories is null or <= 0)
                throw new ArgumentOutOfRangeException(nameof(manualCalories), "A manual calorie target must be positive.");
            target = manualCalories.Value;
        }
        else
        {
            if (deficitPercentage is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(deficitPercentage));
            if (surplusPercentage is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(surplusPercentage));
            target = goal switch
            {
                PrimaryGoal.LoseFat or PrimaryGoal.Recomposition => tdee * (1 - deficitPercentage / 100),
                PrimaryGoal.GainMuscle => tdee * (1 + surplusPercentage / 100),
                _ => tdee,
            };
        }

        var goalCalories = (int)Math.Round(target, MidpointRounding.AwayFromZero);
        var adjustment = goalCalories - tdee;
        var adjustmentPct = adjustment / tdee * 100;
        var weekly = EstimatedWeeklyWeightChangeKg(adjustment);

        if (adjustmentPct < -25)
            warnings.Add("This is a large deficit (over 25% below estimated maintenance). Faster loss is harder to sustain and makes it harder to keep muscle. Consider a smaller deficit.");
        if (goalCalories < bmr)
            warnings.Add("This target is below your estimated BMR. Please review it. If you have medical conditions or need individual guidance, talk to a qualified healthcare professional.");
        if (weightKg > 0 && weekly < 0 && -weekly > weightKg * 0.01m)
            warnings.Add("The estimated loss is more than about 1% of body weight per week, which many people find hard to sustain.");
        if (adjustmentPct > 20)
            warnings.Add("This is a large surplus. Faster gains usually include more fat.");

        return new CalorieGoalResult(tdee, goalCalories, adjustment, adjustmentPct, weekly, warnings);
    }

    public MacroResult CalculateMacros(int calories, decimal weightKg, decimal proteinGramsPerKg, decimal fatPercentage,
        decimal? customProtein = null, decimal? customCarbs = null, decimal? customFat = null)
    {
        var errors = new List<string>();
        if (calories <= 0) errors.Add("The calorie target must be positive.");
        if (weightKg <= 0) errors.Add("Body weight must be positive to calculate protein.");

        var customCount = new[] { customProtein, customCarbs, customFat }.Count(x => x is not null);
        if (customCount is > 0 and < 3) errors.Add("To set custom macros, enter protein, carbohydrate and fat grams together.");
        if (errors.Count > 0) return new MacroResult(null, errors);

        if (customCount == 3)
        {
            if (customProtein < 0 || customCarbs < 0 || customFat < 0)
                return new MacroResult(null, ["Macro grams cannot be negative."]);
            var custom = new MacroTargets(calories, R(customProtein!.Value), R(customCarbs!.Value), R(customFat!.Value));
            // Custom grams must describe roughly the same energy as the calorie goal, otherwise the targets contradict each other.
            if (Math.Abs(custom.RoundingDifference) > calories * 0.05m)
                return new MacroResult(null, [$"Your custom macros add up to {custom.MacroCalories} kcal, but your calorie goal is {calories} kcal. Adjust the grams or the calorie goal so they are within 5% of each other."]);
            return new MacroResult(custom, []);
        }

        if (proteinGramsPerKg is < 0.8m or > 3.5m) errors.Add("Protein must be between 0.8 and 3.5 g per kg of body weight.");
        if (fatPercentage is < 15 or > 60) errors.Add("Fat must be between 15% and 60% of calories.");
        if (errors.Count > 0) return new MacroResult(null, errors);

        var protein = R(weightKg * proteinGramsPerKg);
        var fat = R(calories * fatPercentage / 100 / FatKcalPerGram);
        var carbCalories = calories - protein * ProteinKcalPerGram - fat * FatKcalPerGram;
        if (carbCalories < 0)
            return new MacroResult(null, [$"Protein ({protein} g) and fat ({fat} g) already use {protein * 4 + fat * 9} kcal, more than your {calories} kcal goal. Lower the protein or fat setting, or raise the calorie goal."]);

        return new MacroResult(new MacroTargets(calories, protein, R(carbCalories / CarbKcalPerGram), fat), []);
    }

    public NutritionPlan CalculatePlan(PlanInput i)
    {
        var age = AgeInYears(i.DateOfBirth, i.Today);
        var bmr = CalculateBmr(i.Sex, i.WeightKg, i.HeightCm, age);
        var tdee = CalculateTdee(bmr, i.ActivityLevel);
        var goal = CalculateCalorieGoal(tdee, bmr, i.Goal, i.Mode, i.DeficitPercentage, i.SurplusPercentage, i.ManualCalories, i.WeightKg);
        var macros = CalculateMacros(goal.DailyCalorieGoal, i.WeightKg, i.ProteinGramsPerKg, i.FatPercentage,
            i.CustomProteinGrams, i.CustomCarbohydratesGrams, i.CustomFatGrams);
        return new NutritionPlan(age, bmr, i.ActivityLevel, ActivityFactor(i.ActivityLevel), tdee, goal, macros);
    }

    /// <summary>Scaled value = value per reference × actual quantity / reference quantity.</summary>
    public NutrientTotals Scale(INutrients perReference, decimal referenceQuantity, decimal actualQuantity)
    {
        if (referenceQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(referenceQuantity), "Reference quantity must be positive.");
        if (actualQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(actualQuantity), "Quantity must be positive.");
        var f = actualQuantity / referenceQuantity;
        return new NutrientTotals(perReference.Calories * f, perReference.ProteinGrams * f, perReference.CarbohydratesGrams * f,
            perReference.FatGrams * f, (perReference.FiberGrams ?? 0) * f, (perReference.SugarGrams ?? 0) * f);
    }

    public NutrientTotals Sum(IEnumerable<INutrients> items) =>
        items.Aggregate(NutrientTotals.Zero, (acc, i) => acc + NutrientTotals.From(i));

    public RemainingAmount Remaining(decimal target, decimal consumed) => new(target, consumed);

    /// <summary>Averages only days that have data, so unlogged days don't read as zero intake.</summary>
    public decimal? AverageOfLoggedDays(IEnumerable<decimal?> dailyValues)
    {
        var logged = dailyValues.Where(v => v is not null).Select(v => v!.Value).ToList();
        return logged.Count == 0 ? null : logged.Average();
    }

    private static int R(decimal v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
