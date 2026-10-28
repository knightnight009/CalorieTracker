using System.ComponentModel.DataAnnotations;

namespace CalorieTracker.Web.Models;

public enum BiologicalSex { Male, Female }

public enum WeightUnit { [Display(Name = "Kilograms (kg)")] Kg, [Display(Name = "Pounds (lb)")] Lb }

public enum HeightUnit { [Display(Name = "Centimeters")] Cm, [Display(Name = "Feet / inches")] FtIn }

public enum ActivityLevel
{
    [Display(Name = "Sedentary", Description = "Desk job, little intentional exercise, under ~5,000 steps a day.")]
    Sedentary,
    [Display(Name = "Lightly active", Description = "Light exercise 1–3 days a week, or a job with some walking.")]
    LightlyActive,
    [Display(Name = "Moderately active", Description = "Moderate training 3–5 days a week, or an on-your-feet job.")]
    ModeratelyActive,
    [Display(Name = "Very active", Description = "Hard training 6–7 days a week, or a physically demanding job.")]
    VeryActive,
    [Display(Name = "Extra active", Description = "Hard daily training plus a physical job, or twice-a-day training.")]
    ExtraActive
}

public enum WorkoutIntensity { Light, Moderate, Vigorous }

public enum PrimaryGoal
{
    [Display(Name = "Lose body fat")] LoseFat,
    [Display(Name = "Maintain weight")] Maintain,
    [Display(Name = "Gain muscle")] GainMuscle,
    [Display(Name = "Body recomposition")] Recomposition
}

public enum CalorieTargetMode { [Display(Name = "Percentage of TDEE")] Percentage, [Display(Name = "Manual daily target")] Manual }

public enum MealType { Breakfast, Lunch, Dinner, Snack }

public enum NutritionSource
{
    Manual,
    [Display(Name = "Custom food")] CustomFood,
    [Display(Name = "USDA FoodData Central")] Usda,
    [Display(Name = "AI estimate")] AiEstimate,
    [Display(Name = "Saved meal")] SavedMeal
}

public enum FoodScanStatus { Analyzed, Failed, Saved }
