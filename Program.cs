using Library_Management_system.Data;
using Library_Management_system.Models;
using Library_Management_system.Services;
using Library_Management_system.Infrastructure;
using Library_Management_system.Rfid;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// SQL Server
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null);
            sqlOptions.CommandTimeout(30);
        }));



// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Change this from false to true
    options.SignIn.RequireConfirmedAccount = true; 
    options.User.RequireUniqueEmail = true;

    // Optional: password rules (you can adjust)
    options.Password.RequiredLength = 6;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/";
    
    // Explicitly handle the redirect to avoid the ReturnUrl parameter
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.Redirect("/");
        return Task.CompletedTask;
    };
});

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<DbHelper>();
builder.Services.AddTransient<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, Library_Management_system.Services.EmailSender>();
// SMA LMS Phase 4. Both the RFID and manual workflows resolve the same ICirculationService,
// so the business rules cannot drift apart (specification sections 71, 87).
builder.Services.AddScoped<Library_Management_system.Application.Policies.ILibraryPolicyService,
                           Library_Management_system.Application.Policies.LibraryPolicyService>();
builder.Services.AddScoped<Library_Management_system.Application.Circulation.ICirculationService,
                           Library_Management_system.Application.Circulation.CirculationService>();
builder.Services.AddScoped<Library_Management_system.Application.Circulation.IReservationService,
                           Library_Management_system.Application.Circulation.ReservationService>();
builder.Services.AddScoped<Library_Management_system.Application.Rfid.IRfidTagService,
                           Library_Management_system.Application.Rfid.RfidTagService>();
builder.Services.AddScoped<Library_Management_system.Application.Rfid.IRfidScanRecorder,
                           Library_Management_system.Application.Rfid.RfidScanRecorder>();
builder.Services.AddScoped<Library_Management_system.Application.Rfid.IRfidTagImportService,
                           Library_Management_system.Application.Rfid.RfidTagImportService>();
builder.Services.AddScoped<Library_Management_system.Application.Search.IStudentDossierService,
                           Library_Management_system.Application.Search.StudentDossierService>();
builder.Services.AddScoped<Library_Management_system.Application.Search.IGlobalSearchService,
                           Library_Management_system.Application.Search.GlobalSearchService>();
builder.Services.AddScoped<Library_Management_system.Application.Reporting.IReportingService,
                           Library_Management_system.Application.Reporting.ReportingService>();
builder.Services.AddScoped<Library_Management_system.Application.Assistant.ILibraryAssistant,
                           Library_Management_system.Application.Assistant.LibraryAssistant>();

// Self-service station. The store is a singleton because a kiosk is a piece of furniture: the books
// on its antenna outlive any one HTTP request or browser session.
builder.Services.Configure<Library_Management_system.Application.Policies.LibraryHoursOptions>(
    builder.Configuration.GetSection(Library_Management_system.Application.Policies.LibraryHoursOptions.SectionName));

builder.Services.Configure<Library_Management_system.Application.Kiosk.KioskOptions>(
    builder.Configuration.GetSection(Library_Management_system.Application.Kiosk.KioskOptions.SectionName));
builder.Services.AddSingleton<Library_Management_system.Application.Kiosk.KioskStationStore>();
builder.Services.AddScoped<Library_Management_system.Application.Kiosk.IKioskService,
                           Library_Management_system.Application.Kiosk.KioskService>();

// Phase 12. Refuses to start on unsafe production configuration (§59, §62, §78).
Library_Management_system.Infrastructure.ProductionGuards.Validate(
    builder.Configuration, builder.Environment);

// Phase 9. Notifications go through a database outbox so a dead SMTP server can never roll back
// a successful loan (specification sections 51, 53, 87).
builder.Services.AddScoped<Library_Management_system.Application.Notifications.IEmailDispatcher,
                           Library_Management_system.Application.Notifications.EmailDispatcher>();
builder.Services.AddScoped<Library_Management_system.Application.Notifications.INotificationOutbox,
                           Library_Management_system.Application.Notifications.NotificationOutbox>();
builder.Services.AddHostedService<Library_Management_system.Application.Notifications.OverdueBackgroundService>();

// Phase 5. Refuses to start with the RFID simulator in Production.
builder.Services.AddSmaRfid(builder.Configuration, builder.Environment);

// Writes back read counts for bursts that have ended (§4D). Counts accumulate in memory so a
// repeated RF observation never becomes its own database write.
builder.Services.AddHostedService<Library_Management_system.Application.Rfid.RfidBurstFlushService>();

// Password reset links and admin alerts. Sent inline rather than through the notification
// outbox, because the user is waiting on the page and a late reset link is a useless one.
builder.Services.AddScoped<IAccountEmailService, AccountEmailService>();

var seedAdminEmail = builder.Configuration["SeedAdmin:Email"] ?? "admin@library.com";
var seedAdminPassword = builder.Configuration["SeedAdmin:Password"] ?? "Admin@123";
var resetSeedAdminPasswordOnStartup =
    builder.Configuration.GetValue("SeedAdmin:ResetPasswordOnStartup", builder.Environment.IsDevelopment());

// Demo accounts for local development so every role has a usable login.
// Turn off with "SeedDemoUsers:Enabled": false before deploying.
var seedDemoUsers = builder.Configuration.GetValue("SeedDemoUsers:Enabled", false);
var seedDemoPassword = builder.Configuration["SeedDemoUsers:Password"] ?? "Demo@123";

// Sample catalog for a fresh database; ignored once any book exists.
var seedSampleData = builder.Configuration.GetValue("SeedSampleData:Enabled", false);

// Logins for seeded students, so they can reach the portal and the cart-to-kiosk flow.
// Refused in Production by ProductionGuards, same as the demo users.
var seedStudentAccounts = builder.Configuration.GetValue("SeedStudentAccounts:Enabled", false);

// No default. A fallback here would be a password living in source control, and this repository is
// public — so the seeder refuses to run rather than quietly issuing accounts with a known password.
var seedStudentPassword = builder.Configuration["SeedStudentAccounts:Password"];

if (seedStudentAccounts && string.IsNullOrWhiteSpace(seedStudentPassword))
{
    throw new InvalidOperationException(
        "SeedStudentAccounts:Enabled is true but SeedStudentAccounts:Password is not set. "
        + "Set it in appsettings.json or user-secrets; there is deliberately no built-in default.");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error/500");
    app.UseHsts();
}

app.UseSmaSecurityHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

await using (var scope = app.Services.CreateAsyncScope())
{
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<ApplicationDbContext>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    await dbContext.Database.MigrateAsync();

    await EnsureRoleExistsAsync(roleManager, "Admin");
    await EnsureRoleExistsAsync(roleManager, "Librarian");
    await EnsureRoleExistsAsync(roleManager, "User");

    // Seed Admin User
    var adminUser = await userManager.FindByEmailAsync(seedAdminEmail);
    if (adminUser == null)
    {
        adminUser = new ApplicationUser
        {
            UserName = seedAdminEmail,
            Email = seedAdminEmail,
            FullName = "System Admin",
            EmailConfirmed = true
        };

        var createAdminResult = await userManager.CreateAsync(adminUser, seedAdminPassword);
        if (!createAdminResult.Succeeded)
        {
            var errors = string.Join("; ", createAdminResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create admin user '{seedAdminEmail}': {errors}");
        }
    }

    if (adminUser == null)
    {
        throw new InvalidOperationException($"Seed admin user '{seedAdminEmail}' could not be loaded.");
    }

    if (!adminUser.EmailConfirmed)
    {
        adminUser.EmailConfirmed = true;
        var confirmEmailResult = await userManager.UpdateAsync(adminUser);
        if (!confirmEmailResult.Succeeded)
        {
            var errors = string.Join("; ", confirmEmailResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to confirm seed admin email '{seedAdminEmail}': {errors}");
        }
    }

    if (resetSeedAdminPasswordOnStartup)
    {
        var hasExpectedPassword = await userManager.CheckPasswordAsync(adminUser, seedAdminPassword);
        if (!hasExpectedPassword)
        {
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(adminUser);
            var resetPasswordResult = await userManager.ResetPasswordAsync(adminUser, resetToken, seedAdminPassword);
            if (!resetPasswordResult.Succeeded)
            {
                var errors = string.Join("; ", resetPasswordResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to reset seed admin password '{seedAdminEmail}': {errors}");
            }
        }
    }

    if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
    {
        var addRoleResult = await userManager.AddToRoleAsync(adminUser, "Admin");
        if (!addRoleResult.Succeeded)
        {
            var errors = string.Join("; ", addRoleResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to assign Admin role to '{seedAdminEmail}': {errors}");
        }
    }

    if (seedDemoUsers)
    {
        await EnsureDemoUserAsync(userManager, "librarian@library.com", "Demo Librarian", "Librarian", "+85512000002", seedDemoPassword);
        await EnsureDemoUserAsync(userManager, "student@library.com", "Demo Student", "User", "+85512000003", seedDemoPassword);

        // Without this link the demo student can sign in but is not a borrower, so the kiosk cannot
        // identify them from their login and the cart-to-self-checkout flow dead-ends.
        var demoStudentAccount = await userManager.FindByEmailAsync("student@library.com");
        if (demoStudentAccount is not null)
        {
            await StudentDemoSeeder.LinkAccountAsync(
                dbContext, "student@library.com", demoStudentAccount.Id, "Demo Student");
        }
    }

    var rfidOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<
        Library_Management_system.Rfid.RfidOptions>>().Value;

    if (seedSampleData)
    {
        await SampleDataSeeder.SeedAsync(dbContext, "System Admin");

        // Students to issue books to. The catalogue seeder creates titles only, and a Student is a
        // university identity rather than something a signup produces (§35).
        await StudentDemoSeeder.SeedAsync(dbContext);

        // Accession numbers, demo readers, and — only when no real reader is configured — simulated
        // tags, so the circulation desk works without hardware. Development only, same switch as the
        // sample catalogue.
        await RfidDemoSeeder.SeedAsync(dbContext, simulatedTags: rfidOptions.IsSimulator);
    }

    if (seedStudentAccounts)
    {
        // Non-null here: startup already refused to get this far with the flag on and no password.
        var accounts = await StudentAccountSeeder.SeedAsync(dbContext, userManager, seedStudentPassword!);

        app.Logger.LogInformation(
            "Student logins ready for {Count} student(s); {New} newly created.",
            accounts.Count, accounts.Count(a => a.Created));
    }

    // Library policies always seed: the circulation engine needs them, and an admin edits the
    // rows afterwards rather than a developer editing constants (specification section 22).
    await LibraryPolicySeeder.SeedAsync(dbContext);

    // The configured reader always gets a row, sample data or not: the host service dials readers
    // listed in the database, so without this a physical reader on the LAN is never contacted.
    await RfidReaderSeeder.SeedAsync(dbContext, rfidOptions);

    // Operator-triggered account re-keying. Off unless CredentialReset:Enabled is true. The
    // addresses and passwords come from configuration, never from source: this repository is
    // public and appsettings.json is not in it.
    var credentialReset = builder.Configuration
        .GetSection(Library_Management_system.Data.CredentialResetOptions.SectionName)
        .Get<Library_Management_system.Data.CredentialResetOptions>();

    if (credentialReset is { Enabled: true, Accounts.Count: > 0 })
    {
        var results = await Library_Management_system.Data.CredentialResetSeeder.RunAsync(
            userManager, dbContext, credentialReset);

        foreach (var r in results)
        {
            Console.WriteLine(r.Succeeded
                ? $"[credentials] {r.CurrentEmail} -> {r.NewEmail}"
                : $"[credentials] FAILED {r.CurrentEmail}: {r.Error}");
        }

        Console.WriteLine(
            $"[credentials] {results.Count(r => r.Succeeded)}/{results.Count} account(s) updated.");
    }

    // Destructive, operator-triggered catalogue rebuild. Off unless Catalogue:FreshImport is
    // explicitly true, because it deletes every book, copy and tag in the database. Turn it off
    // again once it has run, or the next restart wipes the catalogue a second time.
    if (builder.Configuration.GetValue<bool>("Catalogue:FreshImport"))
    {
        var csv = Path.Combine(
            builder.Environment.ContentRootPath,
            builder.Configuration["Catalogue:FreshImportFile"] ?? "Data/Seed/rfid-book-tags.csv");

        var outcome = await FreshCatalogueSeeder.RunAsync(dbContext, csv);

        Console.WriteLine(
            $"[catalogue] Rebuilt from {csv}: {outcome.BooksCreated} books, "
            + $"{outcome.CopiesCreated} copies, {outcome.TagsCreated} tags. "
            + $"Removed {outcome.BooksDeleted} books / {outcome.CopiesDeleted} copies / "
            + $"{outcome.TagsDeleted} tags, closed {outcome.LoansClosed} open loan(s).");

        if (outcome.UnreadableEpcStockCodes.Count > 0)
        {
            Console.WriteLine(
                $"[catalogue] {outcome.UnreadableEpcStockCodes.Count} EPC(s) are not hexadecimal and "
                + "can never be reported by a reader. Those copies exist but will not scan: "
                + string.Join(", ", outcome.UnreadableEpcStockCodes));
        }
    }
}

app.Run();

// Creates (or repairs) a ready-to-use account: confirmed, approved, unlocked and in its role.
static async Task EnsureDemoUserAsync(
    UserManager<ApplicationUser> userManager,
    string email,
    string fullName,
    string roleName,
    string phoneNumber,
    string password)
{
    var user = await userManager.FindByEmailAsync(email);

    if (user == null)
    {
        // The demo account is found by email, but Identity keys uniqueness on the username too,
        // and the username here is derived from the display name. An admin who edits the demo
        // account's email leaves the username behind, so the email lookup misses while the
        // username is still taken — and creation then fails on a duplicate username.
        //
        // Adopt that account rather than failing: it is the same demo account, renamed.
        var derivedUserName = new string(fullName.Where(char.IsLetterOrDigit).ToArray());
        var byUserName = await userManager.FindByNameAsync(derivedUserName);

        if (byUserName is not null)
        {
            user = byUserName;
        }
    }

    if (user == null)
    {
        user = new ApplicationUser
        {
            UserName = new string(fullName.Where(char.IsLetterOrDigit).ToArray()),
            Email = email,
            FullName = fullName,
            EmailConfirmed = true,
            PhoneNumber = phoneNumber,
            PhoneNumberConfirmed = true,
            CreatedBy = "Demo Seed",
            CreatedDate = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));

            // A demo account is a convenience. It is not worth refusing to start the library over,
            // which is what throwing here did.
            Console.Error.WriteLine(
                $"[seed] Skipping demo user '{email}': {errors}");
            return;
        }
    }

    // Registration locks new accounts until an admin approves them; demo
    // accounts skip that so they can sign in immediately.
    await userManager.SetLockoutEndDateAsync(user, null);

    var claims = await userManager.GetClaimsAsync(user);
    foreach (var stale in claims.Where(c => c.Type == AccountApproval.ClaimType))
    {
        await userManager.RemoveClaimAsync(user, stale);
    }

    await userManager.AddClaimAsync(user, new Claim(AccountApproval.ClaimType, AccountApproval.Approved));

    if (!await userManager.IsInRoleAsync(user, roleName))
    {
        await userManager.AddToRoleAsync(user, roleName);
    }
}

static async Task EnsureRoleExistsAsync(RoleManager<IdentityRole> roleManager, string roleName)
{
    if (await roleManager.RoleExistsAsync(roleName))
    {
        return;
    }

    var createRoleResult = await roleManager.CreateAsync(new IdentityRole(roleName));
    if (!createRoleResult.Succeeded)
    {
        var errors = string.Join("; ", createRoleResult.Errors.Select(e => e.Description));
        throw new InvalidOperationException($"Failed to create role '{roleName}': {errors}");
    }
}
