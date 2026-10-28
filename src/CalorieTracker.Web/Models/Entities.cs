using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace CalorieTracker.Web.Models;

public class ApplicationUser : IdentityUser
{
    public UserProfile? Profile { get; set; }
}

/// <summary>Nutrient amounts shared by foods, diary items and saved meal items.</summary>
public interface INutrients
{
    decimal Calories { get; }
    decimal ProteinGrams { get; }
    decimal CarbohydratesGrams { get; }
    decimal FatGrams { get; }
    decimal? FiberGrams { get; }
    decimal? SugarGrams { get; }
}

public class UserProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public BiologicalSex SexForBmrCalculation { get; set; }
    public DateOnly DateOfBirth { get; set; }
    /// <summary>Canonical height; HeightUnit is only the display preference.</summary>
    public decimal HeightCm { get; set; }
    public HeightUnit HeightUnit { get; set; }
    public WeightUnit PreferredWeightUnit { get; set; }

    public ActivityLevel ActivityLevel { get; set; } = ActivityLevel.LightlyActive;
    public int WorkoutDaysPerWeek { get; set; }
    public int AverageWorkoutDurationMinutes { get; set; }
    public WorkoutIntensity WorkoutIntensity { get; set; } = WorkoutIntensity.Moderate;

    public PrimaryGoal PrimaryGoal { get; set; } = PrimaryGoal.Maintain;
    public decimal? TargetWeightKg { get; set; }
    public decimal? TargetBodyFatPercentage { get; set; }

    public CalorieTargetMode CalorieTargetMode { get; set; } = CalorieTargetMode.Percentage;
    public decimal PreferredCalorieDeficitPercentage { get; set; } = 15;
    public decimal PreferredCalorieSurplusPercentage { get; set; } = 10;
    public int? DailyCalorieTargetOverride { get; set; }

    public decimal ProteinTargetGramsPerKg { get; set; } = 1.8m;
    public decimal FatTargetPercentage { get; set; } = 30;
    /// <summary>Advanced: explicit gram targets replace the computed ones when all three are set.</summary>
    public decimal? CustomProteinGrams { get; set; }
    public decimal? CustomCarbohydratesGrams { get; set; }
    public decimal? CustomFatGrams { get; set; }

    /// <summary>IANA time zone used to decide which diary day "today" is.</summary>
    [MaxLength(64)] public string TimeZoneId { get; set; } = "UTC";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Targets in effect from a date onward; used for adherence history.</summary>
public class NutritionGoalSnapshot
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly EffectiveDate { get; set; }
    public int Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public decimal FatGrams { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class Food : INutrients
{
    public int Id { get; set; }
    /// <summary>Foods are per-user: custom foods and USDA foods the user has saved.</summary>
    public string UserId { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? Brand { get; set; }
    [MaxLength(100)] public string ServingDescription { get; set; } = "1 serving";
    public decimal? ServingWeightGrams { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal? FiberGrams { get; set; }
    public decimal? SugarGrams { get; set; }
    public decimal? SodiumMilligrams { get; set; }
    public NutritionSource DataSource { get; set; }
    [MaxLength(50)] public string? ExternalFoodId { get; set; }
    public bool IsCustomFood { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class FoodDiaryEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    /// <summary>The user's local calendar day; stored as a plain date so it never shifts with time zones.</summary>
    public DateOnly DiaryDate { get; set; }
    public MealType MealType { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    /// <summary>Idempotency key from the form, so a double-clicked Save creates one entry.</summary>
    public Guid? ClientRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<FoodDiaryItem> Items { get; set; } = [];
}

public class FoodDiaryItem : INutrients
{
    public int Id { get; set; }
    public int FoodDiaryEntryId { get; set; }
    public FoodDiaryEntry? Entry { get; set; }
    public int? FoodId { get; set; }
    public Food? Food { get; set; }

    // Snapshot of the nutrition at logging time, so later food edits never rewrite history.
    [MaxLength(200)] public string FoodNameSnapshot { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    [MaxLength(100)] public string? PortionDescription { get; set; }
    public decimal? WeightGrams { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal? FiberGrams { get; set; }
    public decimal? SugarGrams { get; set; }
    public NutritionSource NutritionSource { get; set; }
    public bool IsAiEstimated { get; set; }
    public decimal? AiConfidence { get; set; }
}

public class WeightLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal WeightKg { get; set; }
    public decimal? BodyFatPercentage { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class SavedMeal
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public List<SavedMealItem> Items { get; set; } = [];
}

public class SavedMealItem : INutrients
{
    public int Id { get; set; }
    public int SavedMealId { get; set; }
    public int? FoodId { get; set; }
    [MaxLength(200)] public string FoodNameSnapshot { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    [MaxLength(100)] public string? PortionDescription { get; set; }
    public decimal? WeightGrams { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal? FiberGrams { get; set; }
    public decimal? SugarGrams { get; set; }
    public NutritionSource NutritionSource { get; set; }
    public bool IsAiEstimated { get; set; }
}

/// <summary>AI analysis record. Stores the validated result JSON only, never the image.</summary>
public class FoodScan
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    [MaxLength(30)] public string Provider { get; set; } = "";
    [MaxLength(100)] public string Model { get; set; } = "";
    public MealType MealType { get; set; }
    public DateOnly DiaryDate { get; set; }
    [MaxLength(1000)] public string? UserContext { get; set; }
    public FoodScanStatus Status { get; set; }
    public string? ResultJson { get; set; }
    public decimal? OverallConfidence { get; set; }
    public int? FoodDiaryEntryId { get; set; }
}
