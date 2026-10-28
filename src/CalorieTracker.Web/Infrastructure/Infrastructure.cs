using System.Security.Claims;
using CalorieTracker.Web.Services.Email;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CalorieTracker.Web.Infrastructure;

public static class RateLimits
{
    public const string Ai = "ai";
}

public static class ControllerExtensions
{
    /// <summary>The authenticated user's id from the auth cookie. Never taken from request data.</summary>
    public static string UserId(this ControllerBase c) =>
        c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No authenticated user.");
}

public static class PostgresConnectionString
{
    /// <summary>Accepts either an Npgsql key/value string or the postgresql:// URL that Neon shows in its dashboard.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (kv[0].ToLowerInvariant())
            {
                case "sslmode": b.SslMode = Enum.Parse<SslMode>(v.Replace("-", ""), ignoreCase: true); break;
                case "channel_binding": b.ChannelBinding = Enum.Parse<ChannelBinding>(v, ignoreCase: true); break;
            }
        }
        return b.ConnectionString;
    }
}

public static class StartupConfigValidator
{
    /// <summary>Returns readable problems. Names the missing setting, never its value.</summary>
    public static List<string> Validate(IHostEnvironment env, string? connectionString, EmailOptions email, bool requireConfirmedEmail)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(connectionString))
            errors.Add("ConnectionStrings__DefaultConnection is not set. Set it to your PostgreSQL connection string.");
        if (!string.IsNullOrWhiteSpace(email.Provider) && !email.IsConfigured)
            errors.Add("EMAIL_PROVIDER is set but EMAIL_API_KEY or EMAIL_FROM_ADDRESS is missing (supported provider: resend).");
        if (env.IsProduction() && requireConfirmedEmail && !email.IsConfigured)
            errors.Add("Email confirmation is required but no email provider is configured. Set EMAIL_PROVIDER=resend, EMAIL_API_KEY and EMAIL_FROM_ADDRESS, or set Auth__RequireConfirmedEmail=false.");
        return errors;
    }
}

/// <summary>Adds the redirect-safe TempData flash messages used across the app.</summary>
public static class FlashExtensions
{
    public static void Flash(this Controller c, string message, string kind = "success")
    {
        c.TempData["FlashMessage"] = message;
        c.TempData["FlashKind"] = kind;
    }
}

public static class EnumText
{
    /// <summary>The [Display(Name)] of an enum value, falling back to its identifier.</summary>
    public static string Name(Enum value) => Attr(value)?.GetName() ?? value.ToString();
    public static string? Description(Enum value) => Attr(value)?.GetDescription();

    private static System.ComponentModel.DataAnnotations.DisplayAttribute? Attr(Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), false)
            .OfType<System.ComponentModel.DataAnnotations.DisplayAttribute>().FirstOrDefault();
}

public static class DecimalExtensions
{
    /// <summary>Drops trailing zeros from database decimals (15.0000 → 15) so forms show and match plain values.</summary>
    public static decimal Normalize(this decimal d) => d / 1.000000000000000000000000000000000m;
    public static decimal? Normalize(this decimal? d) => d?.Normalize();
}
