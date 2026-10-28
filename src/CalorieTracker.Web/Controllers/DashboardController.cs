using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CalorieTracker.Web.Controllers;

public class DashboardController(IDashboardService dashboard) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await dashboard.BuildAsync(this.UserId(), ct);
        return vm is null ? RedirectToAction("Setup", "Profile") : View(vm);
    }
}
