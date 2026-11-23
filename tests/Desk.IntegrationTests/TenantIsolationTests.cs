using System.Net;
using Desk.Core.Domain;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>Spec section 4: tenant A can neither read, list nor modify data of tenant B.</summary>
public class TenantIsolationTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private sealed record World(Session AdminA, Session AdminB, Guid AgentA, Guid AgentB, Guid InvitationB);

    private async Task<World> BuildWorldAsync()
    {
        var (_, adminA) = await CreateTenantWithAdminAsync("alpha", "admin@alpha.test");
        var (_, adminB) = await CreateTenantWithAdminAsync("beta", "admin@beta.test");
        var (agentA, _) = await InviteAndSignInAsync(adminA, "agent@alpha.test", "Agent");
        var (agentB, _) = await InviteAndSignInAsync(adminB, "agent@beta.test", "Agent");

        var pending = await adminB.PostAsync("/api/v1/invitations", new { email = "later@beta.test", role = "Requester" });
        var invitationB = (await JsonAsync(pending)).GetProperty("id").GetGuid();
        return new World(adminA, adminB, agentA, agentB, invitationB);
    }

    [Fact]
    public async Task ListUsers_ReturnsOnlyOwnTenant()
    {
        var w = await BuildWorldAsync();

        var page = await JsonAsync(await w.AdminA.GetAsync("/api/v1/users"));

        var emails = page.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Contains("admin@alpha.test", emails);
        Assert.Contains("agent@alpha.test", emails);
        Assert.DoesNotContain(emails, e => e!.EndsWith("@beta.test"));
        Assert.DoesNotContain(emails, e => e == "root@platform.test");
    }

    [Fact]
    public async Task ModifyUserOfOtherTenant_Is404_AndChangesNothing()
    {
        var w = await BuildWorldAsync();

        var response = await w.AdminA.PatchAsync($"/api/v1/users/{w.AgentB}", new { isActive = false, role = "Requester" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var untouched = await QueryDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == w.AgentB));
        Assert.True(untouched.IsActive);
        Assert.Equal(Role.Agent, untouched.Role);
    }

    [Fact]
    public async Task OwnUser_CanBeModified()
    {
        var w = await BuildWorldAsync();

        var response = await w.AdminA.PatchAsync($"/api/v1/users/{w.AgentA}", new { role = "Requester" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Requester", (await JsonAsync(response)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task ListInvitations_ReturnsOnlyOwnTenant()
    {
        var w = await BuildWorldAsync();

        var page = await JsonAsync(await w.AdminA.GetAsync("/api/v1/invitations"));

        var emails = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()).ToList();
        Assert.DoesNotContain("later@beta.test", emails);
        Assert.DoesNotContain(emails, e => e!.EndsWith("@beta.test"));
        Assert.All(emails, e => Assert.EndsWith("@alpha.test", e));
    }

    [Fact]
    public async Task RevokeInvitationOfOtherTenant_Is404_AndKeepsIt()
    {
        var w = await BuildWorldAsync();

        var response = await w.AdminA.DeleteAsync($"/api/v1/invitations/{w.InvitationB}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await QueryDbAsync(db => db.Invitations.IgnoreQueryFilters().AnyAsync(i => i.Id == w.InvitationB)));
        // The owner can still revoke it.
        Assert.Equal(HttpStatusCode.NoContent, (await w.AdminB.DeleteAsync($"/api/v1/invitations/{w.InvitationB}")).StatusCode);
    }

    [Fact]
    public async Task Me_ExposesOnlyOwnTenant()
    {
        var w = await BuildWorldAsync();

        var me = await JsonAsync(await w.AdminA.GetAsync("/api/v1/me"));

        Assert.Equal("alpha", me.GetProperty("tenant").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task TenantAdmin_CannotUsePlatformEndpoints()
    {
        var w = await BuildWorldAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await w.AdminA.GetAsync("/api/v1/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AdminA.PostAsync("/api/v1/tenants", new { name = "X", slug = "x", adminEmail = "x@x.test" })).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_CannotReadTenantContent()
    {
        var w = await BuildWorldAsync();
        var root = await SignInSuperAdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/v1/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.PatchAsync($"/api/v1/users/{w.AgentA}", new { isActive = false })).StatusCode);
    }

    [Fact]
    public async Task AgentAndRequester_CannotManageUsers()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("gamma", "admin@gamma.test");
        var (_, agent) = await InviteAndSignInAsync(admin, "agent@gamma.test", "Agent");
        var (_, requester) = await InviteAndSignInAsync(admin, "req@gamma.test", "Requester");

        foreach (var session in new[] { agent, requester })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/api/v1/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await session.PostAsync("/api/v1/invitations", new { email = "x@gamma.test", role = "Agent" })).StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task QueryFilter_And_Interceptor_EnforceTheTenant()
    {
        var w = await BuildWorldAsync();
        var tenants = await QueryDbAsync(db => db.Tenants.OrderBy(t => t.Slug).Select(t => t.Id).ToListAsync());
        var (tenantA, tenantB) = (tenants[0], tenants[1]);

        using var scope = Factory.Services.CreateScope();
        // Fresh options: the application's own options already carry its (tenant-less) interceptor.
        var connection = scope.ServiceProvider.GetRequiredService<DeskDbContext>().Database.GetConnectionString();
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseNpgsql(connection).Options;
        var stub = new FixedTenant(tenantA);
        await using var db = new DeskDbContext(options, stub);

        // Filter: tenant A sees its own rows only, even without any Where clause.
        Assert.All(await db.Users.ToListAsync(), u => Assert.Equal(tenantA, u.TenantId));
        Assert.All(await db.Invitations.ToListAsync(), i => Assert.Equal(tenantA, i.TenantId));
        Assert.Empty(await db.Categories.ToListAsync());

        // Interceptor: TenantId is filled on creation...
        var interceptor = new TenantSaveChangesInterceptor(stub);
        await using var writer = new DeskDbContext(
            new DbContextOptionsBuilder<DeskDbContext>(options).AddInterceptors(interceptor).Options, stub);
        var category = new Category { Name = "VPN" };
        writer.Categories.Add(category);
        await writer.SaveChangesAsync();
        Assert.Equal(tenantA, category.TenantId);

        // ... and a write aimed at another tenant is refused.
        writer.Categories.Add(new Category { Name = "Imprimante", TenantId = tenantB });
        await Assert.ThrowsAsync<TenantViolationException>(() => writer.SaveChangesAsync());
        writer.ChangeTracker.Clear();

        // A tracked row cannot be moved to another tenant either.
        var mine = await writer.Categories.SingleAsync(c => c.Name == "VPN");
        mine.TenantId = tenantB;
        await Assert.ThrowsAsync<TenantViolationException>(() => writer.SaveChangesAsync());
        writer.ChangeTracker.Clear();

        // Without tenant and without explicit bypass, nothing is written or visible.
        var anonymous = new FixedTenant(null);
        await using var anonymousDb = new DeskDbContext(
            new DbContextOptionsBuilder<DeskDbContext>(options).AddInterceptors(new TenantSaveChangesInterceptor(anonymous)).Options, anonymous);
        Assert.Empty(await anonymousDb.Users.ToListAsync());
        anonymousDb.Categories.Add(new Category { Name = "Hack", TenantId = tenantA });
        await Assert.ThrowsAsync<TenantViolationException>(() => anonymousDb.SaveChangesAsync());
        Assert.NotNull(w);
    }

    private sealed class FixedTenant(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;

        public bool IsSuperAdmin => false;

        public bool IsUnscoped => false;

        public IDisposable BeginUnscoped() => throw new NotSupportedException();
    }
}
