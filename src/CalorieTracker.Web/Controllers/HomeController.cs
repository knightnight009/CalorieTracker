using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CalorieTracker.Web.Controllers;

[AllowAnonymous]
public class HomeController : Controller
{
    public IActionResult Index() =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public IActionResult Error() => View("Error", Activity.Current?.Id ?? HttpContext.TraceIdentifier);

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public IActionResult Status(int id)
    {
        ViewData["StatusCode"] = id;
        return View();
    }
}
