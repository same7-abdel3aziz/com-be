using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Services.Seed;

public static class DatabaseSeeder
{

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");

        await db.Database.EnsureCreatedAsync();

        foreach (var role in SystemRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        await EnsureUserAsync(
            userManager,
            configuration["AdminSeed:Email"] ?? "ksgaal@alliancegroup.com",
            configuration["AdminSeed:Password"] ?? "K$g@@1526",
            configuration["AdminSeed:FullName"] ?? "System Admin",
            SystemRoles.SystemAdmin);

        // Idempotent import of Data/Seed/Arabic_names.csv into GenderNameDictionary.
        await GenderDictionarySeeder.SeedAsync(db, env, seedLogger, CancellationToken.None);

        if (configuration.GetValue<bool>("DemoSeed:Enabled"))
        {
            await EnsureUserAsync(userManager, "supervisor@demo.local", "Supervisor@12345", "General Supervisor", SystemRoles.GeneralSupervisor);
            await EnsureUserAsync(userManager, "judge1@demo.local", "Judge@12345", "Judge One", SystemRoles.Judge);
            await EnsureUserAsync(userManager, "judge2@demo.local", "Judge@12345", "Judge Two", SystemRoles.Judge);

            if (!await db.Competitions.AnyAsync())
            {
                db.Competitions.Add(new Competition
                {
                    Name = "Demo Hashtag Competition",
                    Description = "Simple SRS demo competition",
                    Hashtag = "#DemoCompetition",
                    StartDateUtc = DateTime.UtcNow.AddDays(-1),
                    EndDateUtc = DateTime.UtcNow.AddDays(14),
                    IsActive = true,
                    PollingIntervalMinutes = 10
                });
                await db.SaveChangesAsync();
            }
        }
    }

    private static async Task EnsureUserAsync(UserManager<ApplicationUser> userManager, string email, string password, string fullName, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                IsActive = true
            };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        // Backfill: ensure lockout is enabled even for users seeded before lockout config existed.
        if (!await userManager.GetLockoutEnabledAsync(user))
            await userManager.SetLockoutEnabledAsync(user, true);

        if (!await userManager.IsInRoleAsync(user, role))
            await userManager.AddToRoleAsync(user, role);
    }
}