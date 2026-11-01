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
    public async Task<IActionResult> Index(MealType? meal, CancellationToken ct)
    {
        var vm = await dashboard.BuildAsync(this.UserId(), ct);
        if (vm is null) return RedirectToAction("Setup", "Profile");
        await PrepareIdeasAsync(vm, meal ?? vm.IdeasMealType, ct);
        return View(vm);
    }

    /// <summary>Asks the AI for meals that fit what's left today. A POST so it only runs when the user asks (it uses AI quota).</summary>
    [HttpPost, EnableRateLimiting(RateLimits.Ai)]
    public async Task<IActionResult> MealIdeas(MealType mealType, string? preferences, CancellationToken ct)
    {
        var vm = await dashboard.BuildAsync(this.UserId(), ct);
        if (vm is null) return RedirectToAction("Setup", "Profile");
        vm.IdeasPreferences = preferences?.Trim() is { Length: > 0 } p ? p[..Math.Min(p.Length, 300)] : null;
        var budget = await PrepareIdeasAsync(vm, mealType, ct);

        if (!ai.IsConfigured || budget is null)
        {
            vm.IdeasMessage ??= ai.IsConfigured ? "Set up your goals first so there's something to plan around." : "AI isn't set up, so meal ideas aren't available.";
            return View(nameof(Index), vm);
        }
        try
        {
            var b = budget.Value;
            var outcome = await ai.SuggestMealsAsync(new MealIdeasRequest(vm.IdeasMealType, b.Calories, b.Protein, b.Carbs, b.Fat, vm.IdeasPreferences), ct);
            var calTarget = new RemainingAmount(b.Calories, 0);
            var proteinTarget = new RemainingAmount(b.Protein, 0);
            vm.MealIdeas = outcome.Meals.Select(m =>
            {
                var (fits, note) = DashboardService.MealIdeaFit(m, calTarget, proteinTarget);
                return new MealIdeaView(m, fits, note);
            }).OrderByDescending(m => m.Fits).ToList();
            vm.IdeasProvider = outcome.Provider;
        }
        catch (FoodVisionException ex)
        {
            log.LogWarning("Meal ideas failed: {Kind}", ex.Kind);
            vm.IdeasMessage = ex.Message;
        }
        return View(nameof(Index), vm);
    }

    /// <summary>
    /// Sets the meal, its share of what's left, and the recent meals that fit it (no AI, so it runs on every load).
    /// Returns null when there's nothing to plan for.
    /// </summary>
    private async Task<(decimal Calories, decimal Protein, decimal Carbs, decimal Fat, int MealsLeft)?> PrepareIdeasAsync(DashboardViewModel vm, MealType meal, CancellationToken ct)
    {
        vm.AiConfigured = ai.IsConfigured;
        vm.IdeasMealType = meal;
        if (vm.Calories is null || vm.Protein is null) return null;
        if (vm.Calories.Remaining < 50)
        {
            // Never push food (or restriction) once the budget is used up.
            vm.IdeasMessage = "You've reached today's calorie target, so there's nothing to plan. One day over doesn't undo your progress; your weekly average matters more.";
            return null;
        }
        if (vm.Calories.Remaining < 200) vm.IdeasMealType = MealType.Snack;
        // Plan this meal against its share of the day, not the whole remainder, so breakfast isn't asked to cover dinner too.
        var budget = DashboardService.MealBudget(vm.IdeasMealType, vm.Meals, vm.Calories, vm.Protein, vm.Carbs, vm.Fat);
        vm.IdeasBudget = (budget.Calories, budget.Protein, budget.MealsLeft);
        var candidates = await dashboard.GetRecentMealCandidatesAsync(this.UserId(), vm.Today, ct: ct);
        vm.RecentMeals = DashboardService.RankRecentMeals(candidates, budget.Calories, budget.Protein);
        return budget;
    }
}
