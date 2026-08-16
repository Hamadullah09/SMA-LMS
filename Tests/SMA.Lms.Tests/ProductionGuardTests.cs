using Library_Management_system.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Production configuration guards (specification sections 59, 62, 78).
///
/// The Phase 1 audit found a hardcoded default admin password. These prove a misconfigured
/// production deploy cannot start rather than starting insecurely.
/// </summary>
public class ProductionGuardTests
{
    private sealed class Env : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "SMA.Lms";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A configuration that should pass, so each test can break exactly one thing.</summary>
    private static Dictionary<string, string?> Safe() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=sql.university.edu;Database=SMA;User ID=app;Password=x;",
        ["SeedAdmin:Password"] = "a-genuinely-unique-secret",
        ["SeedDemoUsers:Enabled"] = "false",
        ["SeedSampleData:Enabled"] = "false",
        // All three are required. Email is the only password reset channel, so a deploy that can
        // name a server but cannot authenticate to it is not a working deploy.
        ["EmailSettings:SmtpServer"] = "smtp.university.edu",
        ["EmailSettings:SenderEmail"] = "library@university.edu",
        ["EmailSettings:Password"] = "an-smtp-app-password"
    };

    [Fact]
    public void A_correctly_configured_production_deploy_starts()
    {
        ProductionGuards.Validate(Config(Safe()), new Env());
    }

    [Fact]
    public void Development_is_never_blocked()
    {
        // Local work uses LocalDB, demo seeders and the documented password by design.
        var dev = new Env { EnvironmentName = "Development" };
        var unsafeConfig = Config(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = @"Server=(localdb)\MSSQLLocalDB;Database=LIBRARY_DB;",
            ["SeedAdmin:Password"] = "Admin@123",
            ["SeedDemoUsers:Enabled"] = "true",
            ["SeedSampleData:Enabled"] = "true"
        });

        ProductionGuards.Validate(unsafeConfig, dev);
    }

    [Fact]
    public void The_documented_default_admin_password_is_refused()
    {
        var config = Safe();
        config["SeedAdmin:Password"] = "Admin@123";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProductionGuards.Validate(Config(config), new Env()));

        Assert.Contains("development defaults", ex.Message);
    }

    [Fact]
    public void A_missing_admin_password_is_refused()
    {
        var config = Safe();
        config["SeedAdmin:Password"] = "";

        Assert.Throws<InvalidOperationException>(() => ProductionGuards.Validate(Config(config), new Env()));
    }

    [Fact]
    public void LocalDb_in_production_is_refused()
    {
        var config = Safe();
        config["ConnectionStrings:DefaultConnection"] = @"Server=(localdb)\MSSQLLocalDB;Database=LIBRARY_DB;";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProductionGuards.Validate(Config(config), new Env()));

        Assert.Contains("LocalDB", ex.Message);
    }

    [Fact]
    public void Demo_user_seeding_in_production_is_refused()
    {
        var config = Safe();
        config["SeedDemoUsers:Enabled"] = "true";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProductionGuards.Validate(Config(config), new Env()));

        Assert.Contains("known", ex.Message);
    }

    [Fact]
    public void Sample_catalogue_seeding_in_production_is_refused()
    {
        var config = Safe();
        config["SeedSampleData:Enabled"] = "true";

        Assert.Throws<InvalidOperationException>(() => ProductionGuards.Validate(Config(config), new Env()));
    }

    /// <summary>
    /// appsettings.json ships "smtp.gmail.com" as a default, so the presence of a server name says
    /// nothing about whether mail can actually be sent. Checking only SmtpServer would let a
    /// deploy with no credentials through — and with Telegram gone, email is the only way a
    /// student can reset a password.
    /// </summary>
    [Fact]
    public void Smtp_credentials_are_required_even_when_the_server_is_named()
    {
        var config = Safe();
        config["EmailSettings:SenderEmail"] = "";
        config["EmailSettings:Password"] = "";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProductionGuards.Validate(Config(config), new Env()));

        Assert.Contains("SenderEmail", ex.Message);
        Assert.Contains("Password", ex.Message);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_one_per_restart()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProductionGuards.Validate(Config(new Dictionary<string, string?>()), new Env()));

        // Connection string, admin password and SMTP should all be named in one message.
        Assert.Contains("DefaultConnection", ex.Message);
        Assert.Contains("SeedAdmin:Password", ex.Message);
        Assert.Contains("SmtpServer", ex.Message);
    }
}
