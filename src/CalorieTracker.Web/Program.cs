using System.Security.Claims;
using System.Threading.RateLimiting;
using CalorieTracker.Web.Data;
using CalorieTracker.Web.Infrastructure;
using CalorieTracker.Web.Models;
using CalorieTracker.Web.Services;
using CalorieTracker.Web.Services.Ai;
using CalorieTracker.Web.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// One fixed culture so numbers in forms always use "." and server parsing matches.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
    System.Globalization.CultureInfo.GetCultureInfo("en-US");

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Render (and most container hosts) tell the app which port to listen on.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

if (!builder.Environment.IsDevelopment())
    builder.Logging.ClearProviders().AddJsonConsole();

// ---- Configuration (secrets come from environment variables or user secrets, never from appsettings) ----
var connectionString = PostgresConnectionString.Normalize(config.GetConnectionString("DefaultConnection"));
var aiOptions = new AiOptions
{
    Provider = config["AI_PROVIDER"],
    ApiKey = config["AI_API_KEY"],
    Model = config["AI_MODEL"],
    OllamaBaseUrl = config["OLLAMA_BASE_URL"],
    OllamaModel = config["OLLAMA_MODEL"],
    TimeoutSeconds = config.GetValue("AI_TIMEOUT_SECONDS", 60),
};
var emailOptions = new EmailOptions { Provider = config["EMAIL_PROVIDER"], ApiKey = config["EMAIL_API_KEY"], FromAddress = config["EMAIL_FROM_ADDRESS"] };
var requireConfirmedEmail = config.GetValue("Auth:RequireConfirmedEmail", true);

var configErrors = StartupConfigValidator.Validate(builder.Environment, connectionString, emailOptions, requireConfirmedEmail);
// `dotnet ef` builds this host only to read the DbContext, so it needs nothing but the connection string.
if (EF.IsDesignTime) configErrors.RemoveAll(e => !e.StartsWith("ConnectionStrings"));
if (configErrors.Count > 0)
{
    // Exit cleanly instead of throwing: as PID 1 in a container, an unhandled-exception abort can hang instead of exiting.
    Console.Error.WriteLine("Configuration is incomplete:\n - " + string.Join("\n - ", configErrors));
    Environment.Exit(1);
}

// ---- Data ----
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(connectionString,
    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>().SetApplicationName("CalorieTracker");

// ---- Identity ----
builder.Services.AddDefaultIdentity<ApplicationUser>(o =>
    {
        o.SignIn.RequireConfirmedAccount = requireConfirmedEmail;
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
});
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
// Every page requires login unless it explicitly opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

if (emailOptions.IsConfigured)
{
    builder.Services.AddSingleton(emailOptions);
    builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
}
else if (builder.Environment.IsDevelopment()) builder.Services.AddTransient<IEmailSender, DevelopmentLogEmailSender>();
else builder.Services.AddTransient<IEmailSender, DisabledEmailSender>();

// ---- App services ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(config.GetSection("Nutrition").Get<NutritionOptions>() ?? new NutritionOptions());
builder.Services.AddSingleton<INutritionCalculatorService, NutritionCalculatorService>();
builder.Services.AddScoped<INutritionPlanService, NutritionPlanService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddSingleton(aiOptions);
builder.Services.AddHttpClient<GeminiFoodVisionProvider>(c => c.Timeout = TimeSpan.FromSeconds(aiOptions.TimeoutSeconds));
builder.Services.AddHttpClient<OllamaFoodVisionProvider>(c => c.Timeout = TimeSpan.FromSeconds(aiOptions.TimeoutSeconds));
builder.Services.AddTransient<IFoodVisionProvider>(sp => sp.GetRequiredService<GeminiFoodVisionProvider>());
builder.Services.AddTransient<IFoodVisionProvider>(sp => sp.GetRequiredService<OllamaFoodVisionProvider>());
builder.Services.AddTransient<IFoodVisionService, FoodVisionService>();
builder.Services.AddSingleton(new UsdaOptions { ApiKey = config["USDA_API_KEY"] });
builder.Services.AddHttpClient<IFoodNutritionService, UsdaFoodNutritionService>(c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database", tags: ["ready"]);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = ImageUploadValidator.MaxBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ImageUploadValidator.MaxBytes + 1024 * 1024);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The hosting proxy's address isn't fixed, so trust the forwarded headers it sets.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "text/plain";
        await ctx.HttpContext.Response.WriteAsync("Too many requests. Please wait a minute and try again.", ct);
    };
    // Expensive AI calls: per signed-in user.
    o.AddPolicy(RateLimits.Ai, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("AI_SCANS_PER_MINUTE", 6), Window = TimeSpan.FromMinutes(1) }));
    // Login, registration, password reset and confirmation-resend posts: per client IP.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/Identity/Account")
            ? RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("AUTH_POSTS_PER_MINUTE", 10), Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter("none"));
});

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", false))
{
    // Development convenience only; production applies migrations with an explicit step (see README).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Optional: close public sign-up once your accounts exist (Auth__RegistrationEnabled=false).
if (!config.GetValue("Auth:RegistrationEnabled", true))
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/Identity/Account/Register")) { ctx.Response.StatusCode = 404; return; }
        await next();
    });

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

public partial class Program;
