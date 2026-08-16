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
builder.Services.AddScoped<Library_Management_system.Application.Search.IGlobalSearchService,
                           Library_Management_system.Application.Search.GlobalSearchService>();
builder.Services.AddScoped<Library_Management_system.Application.Reporting.IReportingService,
                           Library_Management_system.Application.Reporting.ReportingService>();
builder.Services.AddScoped<Library_Management_system.Application.Assistant.ILibraryAssistant,
                           Library_Management_system.Application.Assistant.LibraryAssistant>();

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
    }

    if (seedSampleData)
    {
        await SampleDataSeeder.SeedAsync(dbContext, "System Admin");

        // Accession numbers, simulated tags and demo readers, so the circulation desk works
        // without hardware. Development only, same switch as the sample catalogue.
        await RfidDemoSeeder.SeedAsync(dbContext);
    }

    // Library policies always seed: the circulation engine needs them, and an admin edits the
    // rows afterwards rather than a developer editing constants (specification section 22).
    await LibraryPolicySeeder.SeedAsync(dbContext);
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
            throw new InvalidOperationException($"Failed to create demo user '{email}': {errors}");
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
