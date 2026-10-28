using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace CalorieTracker.Web.Services.Email;

public class EmailOptions
{
    /// <summary>"resend" sends real email. Anything else uses the development log sender.</summary>
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? FromAddress { get; set; }
    public bool IsConfigured => string.Equals(Provider, "resend", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>Sends through Resend's HTTPS API (Render's free plan blocks outbound SMTP ports).</summary>
public class ResendEmailSender(HttpClient http, EmailOptions options, ILogger<ResendEmailSender> log) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new { from = options.FromAddress, to = new[] { email }, subject, html = htmlMessage }),
        };
        req.Headers.Authorization = new("Bearer", options.ApiKey);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            log.LogError("Email provider returned {Status} for subject {Subject}", (int)res.StatusCode, subject);
            throw new InvalidOperationException("Email could not be sent.");
        }
    }
}

/// <summary>Production without an email provider: drops the message and never logs its body (it may contain reset tokens).</summary>
public class DisabledEmailSender(ILogger<DisabledEmailSender> log) : IEmailSender
{
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        log.LogWarning("Email delivery is not configured; message '{Subject}' was not sent.", subject);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Development only: writes the email (including confirmation/reset links) to the server log so you can test
/// account flows locally. Never registered in Production.
/// </summary>
public class DevelopmentLogEmailSender(ILogger<DevelopmentLogEmailSender> log) : IEmailSender
{
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        log.LogWarning("DEV EMAIL to {Email} | {Subject}\n{Body}", email, subject, htmlMessage);
        return Task.CompletedTask;
    }
}
