using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.Services.Ai;
using CalorieTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CalorieTracker.Web.Controllers;

public class DashboardController(IDashboardService dashboard, IFoodVisionService ai, ILogger<DashboardController> log) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await dashboard.BuildAsync(this.UserId(), ct);
        if (vm is null) return RedirectToAction("Setup", "Profile");
        vm.AiConfigured = ai.IsConfigured;
        return View(vm);
    }

    /// <summary>Asks the AI for meals that fit what's left today. A POST so it only runs when the user asks (it uses AI quota).</summary>
    [HttpPost, EnableRateLimiting(RateLimits.Ai)]
    public async Task<IActionResult> MealIdeas(MealType mealType, string? preferences, CancellationToken ct)
    {
        var vm = await dashboard.BuildAsync(this.UserId(), ct);
        if (vm is null) return RedirectToAction("Setup", "Profile");
        vm.AiConfigured = ai.IsConfigured;
        vm.IdeasMealType = mealType;
        vm.IdeasPreferences = preferences?.Trim() is { Length: > 0 } p ? p[..Math.Min(p.Length, 300)] : null;

        if (!ai.IsConfigured || vm.Calories is null || vm.Protein is null)
        {
            vm.IdeasMessage = ai.IsConfigured ? "Set up your goals first so there's something to plan around." : "AI isn't set up, so meal ideas aren't available.";
            return View(nameof(Index), vm);
        }
        if (vm.Calories.Remaining < 50)
        {
            // Never push food (or restriction) once the budget is used up.
            vm.IdeasMessage = "You've reached today's calorie target, so there's nothing to plan. One day over doesn't undo your progress; your weekly average matters more.";
            return View(nameof(Index), vm);
        }

        var meal = vm.Calories.Remaining < 200 ? MealType.Snack : mealType;
        // Plan this meal against its share of the day, not the whole remainder, so breakfast isn't asked to cover dinner too.
        var budget = DashboardService.MealBudget(meal, vm.Meals, vm.Calories, vm.Protein, vm.Carbs, vm.Fat);
        vm.IdeasBudget = (budget.Calories, budget.Protein, budget.MealsLeft);
        try
        {
            var outcome = await ai.SuggestMealsAsync(new MealIdeasRequest(meal, budget.Calories, budget.Protein, budget.Carbs, budget.Fat, vm.IdeasPreferences), ct);
            var calTarget = new RemainingAmount(budget.Calories, 0);
            var proteinTarget = new RemainingAmount(budget.Protein, 0);
            vm.MealIdeas = outcome.Meals.Select(m =>
            {
                var (fits, note) = DashboardService.MealIdeaFit(m, calTarget, proteinTarget);
                return new MealIdeaView(m, fits, note);
            }).OrderByDescending(m => m.Fits).ToList();
            vm.IdeasProvider = outcome.Provider;
            vm.IdeasMealType = meal;
        }
        catch (FoodVisionException ex)
        {
            log.LogWarning("Meal ideas failed: {Kind}", ex.Kind);
            vm.IdeasMessage = ex.Message;
        }
        return View(nameof(Index), vm);
    }
}
