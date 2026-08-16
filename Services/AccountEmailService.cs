using Microsoft.AspNetCore.Identity.UI.Services;

namespace Library_Management_system.Services;

/// <summary>
/// Account email that has to be sent while the user is waiting on a page — the password reset
/// link, and the alerts that tell an administrator someone needs approving.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>NotificationOutbox</c>. The outbox is the right home for
/// circulation reminders: they are queued, retried and can arrive minutes later. A reset link is
/// the opposite — it is worthless if it arrives late, and the page has to tell the user now
/// whether it went out. So these are sent inline, and failure is reported rather than retried.
/// </remarks>
public interface IAccountEmailService
{
    /// <summary>
    /// False when no SMTP server is configured. Callers use this to avoid promising a user an
    /// email that cannot physically be sent.
    /// </summary>
    bool IsConfigured { get; }

    Task<bool> SendPasswordResetLinkAsync(
        string emailAddress,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default);

    Task SendAdminAlertAsync(
        string subject,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default);
}

public sealed class AccountEmailService : IAccountEmailService
{
    private readonly IEmailSender _sender;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountEmailService> _logger;

    public AccountEmailService(
        IEmailSender sender,
        IConfiguration configuration,
        ILogger<AccountEmailService> logger)
    {
        _sender = sender;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["EmailSettings:SmtpServer"]) &&
        !string.IsNullOrWhiteSpace(_configuration["EmailSettings:SenderEmail"]) &&
        !string.IsNullOrWhiteSpace(_configuration["EmailSettings:Password"]);

    public async Task<bool> SendPasswordResetLinkAsync(
        string emailAddress,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            // Logged at Warning with the link so a developer or an administrator running without
            // SMTP can still complete a reset. Never shown in the browser: the link is equivalent
            // to a password for the next few minutes.
            _logger.LogWarning(
                "EmailSettings is not configured, so no reset email was sent to {Email}. "
                + "Reset link (valid until used): {ResetUrl}",
                emailAddress,
                resetUrl);
            return false;
        }

        var body = BuildResetEmailBody(displayName, resetUrl);

        try
        {
            await _sender.SendEmailAsync(emailAddress, "Reset your SMA Library password", body);
            _logger.LogInformation("Password reset link sent to {Email}.", emailAddress);
            return true;
        }
        catch (Exception ex)
        {
            // The address may be undeliverable, or the SMTP credentials wrong. Either way the
            // user must not see the exception, and must not be told the mail is on its way.
            _logger.LogError(ex, "Failed to send the password reset email to {Email}.", emailAddress);
            return false;
        }
    }

    public async Task SendAdminAlertAsync(
        string subject,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        var recipient = ResolveAdminRecipient();
        if (string.IsNullOrWhiteSpace(recipient) || !IsConfigured)
        {
            _logger.LogInformation("Admin alert not emailed ({Subject}): {Detail}",
                subject,
                string.Join(" | ", lines));
            return;
        }

        var body = BuildAdminAlertBody(subject, lines);

        try
        {
            await _sender.SendEmailAsync(recipient, $"SMA Library — {subject}", body);
        }
        catch (Exception ex)
        {
            // An alert is informational. Losing one must never fail the registration or approval
            // that triggered it, so this is swallowed after logging.
            _logger.LogError(ex, "Failed to send the admin alert email ({Subject}).", subject);
        }
    }

    private string? ResolveAdminRecipient()
    {
        var configured = _configuration["EmailSettings:AdminEmail"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : _configuration["SeedAdmin:Email"];
    }

    private static string BuildResetEmailBody(string displayName, string resetUrl)
    {
        var greeting = string.IsNullOrWhiteSpace(displayName) ? "Hello," : $"Hello {Escape(displayName)},";

        // Inline styles only: mail clients strip <style> blocks. The raw URL is repeated under the
        // button because some clients do not render the button as a link.
        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#1a1614;line-height:1.6">
              <p style="font-size:18px;font-weight:600;color:#b85c0a;margin:0 0 16px">SMA Library</p>
              <p style="margin:0 0 12px">{greeting}</p>
              <p style="margin:0 0 20px">
                We received a request to reset your SMA Library password. Choose a new one here:
              </p>
              <p style="margin:0 0 20px">
                <a href="{Escape(resetUrl)}"
                   style="background:#b85c0a;color:#ffffff;text-decoration:none;padding:12px 22px;
                          border-radius:8px;display:inline-block;font-weight:600">Reset my password</a>
              </p>
              <p style="margin:0 0 20px;font-size:13px;color:#6b625c">
                If the button does not work, copy this address into your browser:<br />
                <span style="word-break:break-all">{Escape(resetUrl)}</span>
              </p>
              <p style="margin:0 0 12px">
                The link can be used once. If you did not ask for a reset you can ignore this email —
                your password will not change.
              </p>
              <p style="margin:24px 0 0;font-size:13px;color:#6b625c">
                Need help? Ask at the circulation desk.
              </p>
            </div>
            """;
    }

    private static string BuildAdminAlertBody(string subject, IReadOnlyList<string> lines)
    {
        var rows = string.Join(
            "",
            lines.Select(line =>
                $"<li style=\"margin:0 0 6px\">{Escape(line)}</li>"));

        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#1a1614;line-height:1.6">
              <p style="font-size:18px;font-weight:600;color:#b85c0a;margin:0 0 16px">SMA Library</p>
              <p style="margin:0 0 12px;font-weight:600">{Escape(subject)}</p>
              <ul style="margin:0;padding-left:18px">{rows}</ul>
            </div>
            """;
    }

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
