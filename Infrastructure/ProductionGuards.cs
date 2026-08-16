namespace Library_Management_system.Infrastructure;

/// <summary>
/// Fail-fast configuration checks (specification sections 59, 62, 78).
///
/// The Phase 1 audit found the seed admin password defaulting to a value hardcoded in source. A
/// deployment that forgets to set it would silently create a known-credential administrator on a
/// live library system. These guards make that impossible: the application refuses to start rather
/// than starting insecurely.
///
/// Development is unaffected — the point is to catch a misconfigured production deploy at startup,
/// not to make local work harder.
/// </summary>
public static class ProductionGuards
{
    /// <summary>Values that must never reach production, whatever the configuration says.</summary>
    private static readonly string[] KnownWeakPasswords =
    [
        "Admin@123", "Demo@123", "Password1", "password", "changeme"
    ];

    public static void Validate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (!environment.IsProduction())
        {
            return;
        }

        var failures = new List<string>();

        // ---- Connection string ----
        var connection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
        {
            failures.Add("ConnectionStrings:DefaultConnection is not set.");
        }
        else if (connection.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("ConnectionStrings:DefaultConnection still points at LocalDB, "
                         + "which is a development-only database engine.");
        }

        // ---- Seed administrator ----
        var adminPassword = configuration["SeedAdmin:Password"];
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            failures.Add("SeedAdmin:Password is not set. Set it through the hosting control panel "
                         + "rather than in appsettings.json.");
        }
        else if (KnownWeakPasswords.Contains(adminPassword, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("SeedAdmin:Password is one of the documented development defaults. "
                         + "It is public knowledge and must be changed.");
        }

        // ---- Development seeders ----
        if (configuration.GetValue("SeedDemoUsers:Enabled", false))
        {
            failures.Add("SeedDemoUsers:Enabled is true. This creates accounts with known "
                         + "passwords and must be false in production.");
        }

        if (configuration.GetValue("SeedSampleData:Enabled", false))
        {
            failures.Add("SeedSampleData:Enabled is true. This inserts fictional books and "
                         + "simulated RFID tags into a live catalogue.");
        }

        // ---- Email ----
        // Every field is checked, not just the server. SmtpServer now has a sensible default
        // ("smtp.gmail.com") in appsettings, so on its own it proves nothing — a deploy with a
        // server but no credentials would have passed the old check and still sent nothing.
        //
        // This matters more than it used to. Email is no longer only for reminders: it is the
        // sole password reset channel since the Telegram OTP was removed. A production library
        // with unusable email is one where a student who forgets a password cannot recover it
        // without visiting the desk.
        foreach (var (key, consequence) in new[]
                 {
                     ("EmailSettings:SmtpServer", "there is no server to send through"),
                     ("EmailSettings:SenderEmail", "there is no address to send from"),
                     ("EmailSettings:Password", "the SMTP login will be rejected")
                 })
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
            {
                failures.Add($"{key} is not set, so {consequence}. Password reset emails and "
                             + "overdue reminders will not be delivered.");
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        var message = "SMA LMS refused to start in Production because of unsafe configuration:"
                      + Environment.NewLine
                      + string.Join(Environment.NewLine, failures.Select(f => "  - " + f))
                      + Environment.NewLine
                      + "See DEPLOYMENT.md section 4.";

        throw new InvalidOperationException(message);
    }
}

/// <summary>
/// Security response headers (specification section 43).
///
/// Set in middleware rather than web.config so they travel with the application and cannot be
/// lost by a hosting-panel change.
/// </summary>
public static class SecurityHeaderMiddleware
{
    public static IApplicationBuilder UseSmaSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stop MIME sniffing turning an uploaded file into executable content.
            headers["X-Content-Type-Options"] = "nosniff";

            // The circulation desk should never be embeddable in another site's frame.
            headers["X-Frame-Options"] = "DENY";

            // Do not leak the page a student came from — URLs can carry book and student context.
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // The application needs none of these.
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            await next();
        });
    }
}
