using System.ComponentModel.DataAnnotations;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.Services.Ai;

namespace CalorieTracker.Web.ViewModels;

// ---------- Dashboard ----------

public record MealSummary(MealType MealType, NutrientTotals Totals, List<string> ItemNames);

public class DashboardViewModel
{
    public DateOnly Today { get; set; }
    public string? Problem { get; set; }
    public MacroTargets? Targets { get; set; }
    public NutrientTotals Consumed { get; set; } = NutrientTotals.Zero;
    public RemainingAmount? Calories { get; set; }
    public RemainingAmount? Protein { get; set; }
    public RemainingAmount? Carbs { get; set; }
    public RemainingAmount? Fat { get; set; }
    public List<MealSummary> Meals { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];
    public decimal? LatestWeightKg { get; set; }
    public DateOnly? LatestWeightDate { get; set; }
    public WeightUnit WeightUnit { get; set; }
}

// ---------- Profile ----------

public class ProfileSetupViewModel : IValidatableObject
{
    public bool IsNew { get; set; }

    [Display(Name = "Sex used for the BMR formula")]
    public BiologicalSex SexForBmrCalculation { get; set; }

    [Required, DataType(DataType.Date), Display(Name = "Date of birth")]
    public DateOnly? DateOfBirth { get; set; }

    [Required, Range(20, 700), Display(Name = "Current weight")]
    public decimal? CurrentWeight { get; set; }

    [Display(Name = "Weight unit")]
    public WeightUnit PreferredWeightUnit { get; set; }

    [Display(Name = "Height unit")]
    public HeightUnit HeightUnit { get; set; }

    [Range(90, 250), Display(Name = "Height (cm)")]
    public decimal? HeightCm { get; set; }

    [Range(3, 8), Display(Name = "Feet")]
    public int? HeightFeet { get; set; }

    [Range(0, 11.9), Display(Name = "Inches")]
    public decimal? HeightInches { get; set; }

    [Display(Name = "Activity level (used for TDEE)")]
    public ActivityLevel ActivityLevel { get; set; } = ActivityLevel.LightlyActive;

    [Range(0, 14), Display(Name = "Workouts per week")]
    public int WorkoutDaysPerWeek { get; set; } = 3;

    [Range(0, 600), Display(Name = "Average workout length (minutes)")]
    public int AverageWorkoutDurationMinutes { get; set; } = 45;

    [Display(Name = "Typical workout intensity")]
    public WorkoutIntensity WorkoutIntensity { get; set; } = WorkoutIntensity.Moderate;

    [Display(Name = "Primary goal")]
    public PrimaryGoal PrimaryGoal { get; set; } = PrimaryGoal.Maintain;

    [Range(20, 700), Display(Name = "Target weight (optional)")]
    public decimal? TargetWeight { get; set; }

    [Range(3, 60), Display(Name = "Target body fat % (optional)")]
    public decimal? TargetBodyFatPercentage { get; set; }

    [Range(5, 30), Display(Name = "Calorie deficit (% below maintenance)")]
    public decimal PreferredCalorieDeficitPercentage { get; set; } = 15;

    [Range(0, 20), Display(Name = "Calorie surplus (% above maintenance)")]
    public decimal PreferredCalorieSurplusPercentage { get; set; } = 10;

    [Range(0.8, 3.5), Display(Name = "Protein (g per kg body weight)")]
    public decimal ProteinTargetGramsPerKg { get; set; } = 1.8m;

    [Range(15, 60), Display(Name = "Fat (% of calories)")]
    public decimal FatTargetPercentage { get; set; } = 30;

    [Required, MaxLength(64), Display(Name = "Time zone")]
    public string TimeZoneId { get; set; } = "UTC";

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (HeightUnit == HeightUnit.Cm && HeightCm is null)
            yield return new("Enter your height in centimeters.", [nameof(HeightCm)]);
        if (HeightUnit == HeightUnit.FtIn && HeightFeet is null)
            yield return new("Enter your height in feet and inches.", [nameof(HeightFeet)]);
        if (DateOfBirth is { } dob)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (dob > today.AddYears(-18))
                yield return new("This app's formulas and targets are designed for adults (18+).", [nameof(DateOfBirth)]);
            if (dob < today.AddYears(-110))
                yield return new("Please check the date of birth.", [nameof(DateOfBirth)]);
        }
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _))
            yield return new("Unknown time zone.", [nameof(TimeZoneId)]);
    }
}

// ---------- Nutrition goals ----------

public class GoalSettingsInput : IValidatableObject
{
    [Display(Name = "Primary goal")] public PrimaryGoal PrimaryGoal { get; set; }
    [Display(Name = "How to set calories")] public CalorieTargetMode CalorieTargetMode { get; set; }
    [Range(5, 30), Display(Name = "Deficit %")] public decimal DeficitPercentage { get; set; } = 15;
    [Range(0, 20), Display(Name = "Surplus %")] public decimal SurplusPercentage { get; set; } = 10;
    [Range(800, 8000), Display(Name = "Manual daily calories")] public int? ManualCalories { get; set; }
    [Range(0.8, 3.5), Display(Name = "Protein (g/kg)")] public decimal ProteinGramsPerKg { get; set; } = 1.8m;
    [Range(15, 60), Display(Name = "Fat (% of calories)")] public decimal FatPercentage { get; set; } = 30;
    [Range(0, 600), Display(Name = "Protein (g)")] public decimal? CustomProteinGrams { get; set; }
    [Range(0, 1200), Display(Name = "Carbs (g)")] public decimal? CustomCarbohydratesGrams { get; set; }
    [Range(0, 400), Display(Name = "Fat (g)")] public decimal? CustomFatGrams { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (CalorieTargetMode == CalorieTargetMode.Manual && ManualCalories is null)
            yield return new("Enter a daily calorie target, or switch back to a percentage.", [nameof(ManualCalories)]);
    }
}

public record DeficitOption(decimal Percentage, int Calories, decimal DailyDeficit, decimal WeeklyChangeKg);

public class NutritionGoalsViewModel
{
    public UserPlan UserPlan { get; set; } = null!;
    public NutritionPlan? Plan => UserPlan.Plan;
    public GoalSettingsInput Input { get; set; } = new();
    public List<DeficitOption> DeficitOptions { get; set; } = [];
    public List<(ActivityLevel Level, decimal Factor)> ActivityFactors { get; set; } = [];
}

// ---------- Food diary ----------

public record DiaryMeal(MealType MealType, List<FoodDiaryEntry> Entries, NutrientTotals Totals);

public class FoodDiaryViewModel
{
    public DateOnly Date { get; set; }
    public DateOnly Today { get; set; }
    public List<DiaryMeal> Meals { get; set; } = [];
    public NutrientTotals DayTotals { get; set; } = NutrientTotals.Zero;
    public MacroTargets? Targets { get; set; }
    public List<SavedMeal> SavedMeals { get; set; } = [];
}

public abstract class AddToDiaryInput
{
    [Required] public DateOnly Date { get; set; }
    public MealType MealType { get; set; }
    /// <summary>Generated per form render; a repeated submit with the same id is ignored.</summary>
    public Guid ClientRequestId { get; set; }
}

public class NutrientFields
{
    [Required, Range(0, 10000)] public decimal Calories { get; set; }
    [Required, Range(0, 1000), Display(Name = "Protein (g)")] public decimal ProteinGrams { get; set; }
    [Required, Range(0, 2000), Display(Name = "Carbs (g)")] public decimal CarbohydratesGrams { get; set; }
    [Required, Range(0, 1000), Display(Name = "Fat (g)")] public decimal FatGrams { get; set; }
    [Range(0, 500), Display(Name = "Fiber (g)")] public decimal? FiberGrams { get; set; }
    [Range(0, 1000), Display(Name = "Sugar (g)")] public decimal? SugarGrams { get; set; }
}

public class ManualFoodInput : AddToDiaryInput
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(100), Display(Name = "Portion")] public string? PortionDescription { get; set; }
    [Range(0.1, 5000), Display(Name = "Weight (g)")] public decimal? WeightGrams { get; set; }
    public NutrientFields Nutrients { get; set; } = new();
    [Display(Name = "Save as a custom food for next time")] public bool SaveAsCustomFood { get; set; }
}

public class AddFromFoodInput : AddToDiaryInput
{
    public int FoodId { get; set; }
    [Range(0.01, 100)] public decimal Quantity { get; set; } = 1;
}

public class AddUsdaInput : AddToDiaryInput
{
    [Required, RegularExpression(@"^\d{1,12}$")] public string FdcId { get; set; } = "";
    [Range(1, 5000), Display(Name = "Grams")] public decimal Grams { get; set; } = 100;
}

public class AddRecentInput : AddToDiaryInput
{
    public int DiaryItemId { get; set; }
}

public class AddSavedMealInput : AddToDiaryInput
{
    public int SavedMealId { get; set; }
}

public class AddFoodViewModel
{
    public DateOnly Date { get; set; }
    public MealType MealType { get; set; }
    public Guid ClientRequestId { get; set; } = Guid.NewGuid();
    public string? Query { get; set; }
    public string Tab { get; set; } = "search";
    public List<Food> MyFoods { get; set; } = [];
    public List<UsdaFood> UsdaResults { get; set; } = [];
    public bool UsdaConfigured { get; set; }
    public string? UsdaError { get; set; }
    public List<FoodDiaryItem> RecentItems { get; set; } = [];
    public List<SavedMeal> SavedMeals { get; set; } = [];
    public ManualFoodInput Manual { get; set; } = new();
}

public class EditDiaryItemInput
{
    public int Id { get; set; }
    public string FoodName { get; set; } = "";
    public DateOnly Date { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Range(0.01, 100)] public decimal Quantity { get; set; } = 1;
    [StringLength(100), Display(Name = "Portion")] public string? PortionDescription { get; set; }
    [Range(0.1, 5000), Display(Name = "Weight (g)")] public decimal? WeightGrams { get; set; }
    /// <summary>When true the server rescales the logged nutrition by the quantity change and ignores the nutrient fields.</summary>
    [Display(Name = "Recalculate nutrition from the new quantity")] public bool RescaleFromQuantity { get; set; } = true;
    public NutrientFields Nutrients { get; set; } = new();
    public MealType MealType { get; set; }
}

public class FoodInput
{
    public int? Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(200)] public string? Brand { get; set; }
    [Required, StringLength(100), Display(Name = "Serving description")] public string ServingDescription { get; set; } = "1 serving";
    [Range(0.1, 5000), Display(Name = "Serving weight (g)")] public decimal? ServingWeightGrams { get; set; }
    public NutrientFields Nutrients { get; set; } = new();
    [Range(0, 50000), Display(Name = "Sodium (mg)")] public decimal? SodiumMilligrams { get; set; }
    [Display(Name = "Favorite")] public bool IsFavorite { get; set; }
}

public class CopyEntryInput
{
    public int EntryId { get; set; }
    [Required] public DateOnly TargetDate { get; set; }
    public MealType TargetMealType { get; set; }
}

public class SaveMealInput
{
    public int EntryId { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
}

// ---------- AI scanner ----------

public class FoodScannerViewModel
{
    public bool IsConfigured { get; set; }
    public IReadOnlyList<string> Providers { get; set; } = [];
    public DateOnly Date { get; set; }
    public MealType MealType { get; set; }
    [StringLength(1000)] public string? Context { get; set; }
    public List<FoodScan> RecentScans { get; set; } = [];
}

public class ReviewItemInput
{
    public bool Include { get; set; } = true;
    /// <summary>Index into the stored AI result, or -1 for a row the user added.</summary>
    public int OriginalIndex { get; set; } = -1;
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(100), Display(Name = "Portion")] public string? PortionDescription { get; set; }
    [Range(0.1, 5000), Display(Name = "Weight (g)")] public decimal? WeightGrams { get; set; }
    [Range(0, 10000)] public decimal Calories { get; set; }
    [Range(0, 1000)] public decimal ProteinGrams { get; set; }
    [Range(0, 2000)] public decimal CarbohydratesGrams { get; set; }
    [Range(0, 1000)] public decimal FatGrams { get; set; }
    [Range(0, 500)] public decimal? FiberGrams { get; set; }
    [Range(0, 1000)] public decimal? SugarGrams { get; set; }
}

public class FoodAnalysisResultViewModel
{
    public int ScanId { get; set; }
    [Required] public DateOnly Date { get; set; }
    public MealType MealType { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
    public List<ReviewItemInput> Items { get; set; } = [];

    // Display only (re-read from the stored scan, never posted back).
    public FoodAnalysisResult? Analysis { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

// ---------- Progress ----------

public class WeightLogInput
{
    [Required] public DateOnly Date { get; set; }
    [Required, Range(20, 700)] public decimal? Weight { get; set; }
    [Range(3, 70), Display(Name = "Body fat %")] public decimal? BodyFatPercentage { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
}

public record WeekSummary(DateOnly WeekStart, decimal? AvgCalories, decimal? AvgProtein, int DaysLogged);

public class ProgressViewModel
{
    public string Range { get; set; } = "30";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int TotalDays { get; set; }
    public int LoggedDays { get; set; }
    public WeightUnit WeightUnit { get; set; }

    public List<string> Labels { get; set; } = [];
    public List<decimal?> Calories { get; set; } = [];
    public List<decimal?> Protein { get; set; } = [];
    public List<decimal?> CalorieGoal { get; set; } = [];
    public List<decimal?> ProteinGoal { get; set; } = [];
    public List<string> WeightLabels { get; set; } = [];
    public List<decimal> Weights { get; set; } = [];
    public List<decimal?> BodyFat { get; set; } = [];

    public decimal? AvgCalories { get; set; }
    public decimal? AvgProtein { get; set; }
    public (decimal Protein, decimal Carbs, decimal Fat)? MacroKcalShare { get; set; }
    public List<WeekSummary> Weeks { get; set; } = [];
    public int AdherenceDays { get; set; }
    public int AdherenceEligibleDays { get; set; }
    public decimal? WeightChange { get; set; }

    public List<WeightLog> WeightLogs { get; set; } = [];
    public WeightLogInput WeightInput { get; set; } = new();
}

// ---------- Shared partial models ----------

/// <summary>One progress bar: consumed vs target with remaining or exceeded amount.</summary>
public record MacroBarModel(string Label, string ColorClass, RemainingAmount Amount, string Unit, bool Large = false);
