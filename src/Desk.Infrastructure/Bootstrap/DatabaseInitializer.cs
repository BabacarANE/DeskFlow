using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Bootstrap;

public static class DemoAccounts
{
    public const string TenantSlug = "demo";
    public const string TenantName = "Entreprise de démo (données fictives)";

    public static readonly string[] CategoryNames = ["Accès et comptes", "Matériel", "Messagerie", "Réseau et VPN", "Logiciels"];

    public static readonly IReadOnlyDictionary<string, (string Email, string Name, Role Role)> ByDemoRole =
        new Dictionary<string, (string, string, Role)>
        {
            ["admin"] = ("admin@demo.deskflow.example", "Alex Admin (démo)", Role.TenantAdmin),
            ["agent"] = ("agent@demo.deskflow.example", "Camille Agent (démo)", Role.Agent),
            ["demandeur"] = ("demandeur@demo.deskflow.example", "Dominique Demandeur (démo)", Role.Requester),
        };
}

/// <summary>Applies migrations, then seeds the configured SuperAdmin and (if enabled) the demo company.</summary>
public static class DatabaseInitializer
{
    /// <summary>On in Development and when Demo:Enabled; otherwise opt-in with Database:MigrateOnStartup=true.</summary>
    public static bool ShouldRun(IConfiguration configuration, bool isDevelopment) =>
        configuration.GetValue<bool?>("Database:MigrateOnStartup")
        ?? (isDevelopment || configuration.GetValue<bool>("Demo:Enabled"));

    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DeskDbContext>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        await db.Database.MigrateAsync(ct);

        using var _ = sp.GetRequiredService<ITenantContext>().BeginUnscoped();
        await SeedSuperAdminAsync(db, configuration, clock, logger, ct);
        if (configuration.GetValue<bool>("Demo:Enabled"))
        {
            await SeedDemoAsync(db, clock, logger, ct);
        }
    }

    private static async Task SeedSuperAdminAsync(
        DeskDbContext db, IConfiguration configuration, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var configured = configuration["SuperAdmin:Email"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var email = InvitationRules.NormalizeEmail(configured);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return;
        }

        db.Users.Add(new User { Email = email, DisplayName = "SuperAdmin", Role = Role.SuperAdmin, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("SuperAdmin account bootstrapped; it links to its provider on first sign-in");
    }

    private static async Task SeedDemoAsync(DeskDbContext db, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == DemoAccounts.TenantSlug, ct);
        if (tenant is null)
        {
            tenant = new Tenant { Name = DemoAccounts.TenantName, Slug = DemoAccounts.TenantSlug, CreatedAt = clock.GetUtcNow() };
            db.Tenants.Add(tenant);
            db.SlaPolicies.AddRange(SlaPolicy.CreateDefaults(tenant.Id));
        }

        foreach (var (email, name, role) in DemoAccounts.ByDemoRole.Values)
        {
            if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            {
                db.Users.Add(new User { TenantId = tenant.Id, Email = email, DisplayName = name, Role = role, CreatedAt = clock.GetUtcNow() });
            }
        }

        // Demo categories, only on a company that has none yet (idempotent, never overwrites the admin's choices).
        if (!await db.Categories.IgnoreQueryFilters().AnyAsync(c => c.TenantId == tenant.Id, ct))
        {
            foreach (var name in DemoAccounts.CategoryNames)
            {
                db.Categories.Add(new Category { TenantId = tenant.Id, Name = name });
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Demo company seeded");
        }
    }
}
