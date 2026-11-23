using System.Net;
using System.Net.Http.Json;
using Desk.Core.Domain;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>US-01 (tenants) and US-04 (users, invitations).</summary>
public class ManagementTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task SuperAdmin_CreatesTenant_WithDefaultSla_AndInvitesFirstAdmin()
    {
        var root = await SignInSuperAdminAsync();

        var response = await root.PostAsync("/api/v1/tenants", new { name = "Acme", slug = "acme", adminEmail = "Admin@Acme.test" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await JsonAsync(response);
        Assert.Equal("acme", tenant.GetProperty("slug").GetString());
        Assert.True(tenant.GetProperty("isActive").GetBoolean());

        var policies = await QueryDbAsync(db => db.SlaPolicies.IgnoreQueryFilters().OrderBy(p => p.FirstResponseMinutes).ToListAsync());
        Assert.Equal(4, policies.Count);
        Assert.Equal((Priority.Urgent, 30, 240), (policies[0].Priority, policies[0].FirstResponseMinutes, policies[0].ResolutionMinutes));
        Assert.Equal((Priority.Low, 1440, 4320), (policies[3].Priority, policies[3].FirstResponseMinutes, policies[3].ResolutionMinutes));

        var invitation = await QueryDbAsync(db => db.Invitations.IgnoreQueryFilters().SingleAsync());
        Assert.Equal("admin@acme.test", invitation.Email);
        Assert.Equal(Role.TenantAdmin, invitation.Role);
        Assert.InRange(invitation.ExpiresAt - invitation.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        var mail = Assert.Single(Factory.Email.Sent);
        Assert.Equal("admin@acme.test", mail.To);
        Assert.Contains("/login?invitation=", mail.Body);
        Assert.NotEqual(Factory.Email.InvitationTokenFor("admin@acme.test"), invitation.TokenHash); // only the hash is stored
    }

    [Fact]
    public async Task CreateTenant_RejectsDuplicates_AndInvalidInput()
    {
        var root = await SignInSuperAdminAsync();
        await root.PostAsync("/api/v1/tenants", new { name = "Acme", slug = "acme", adminEmail = "admin@acme.test" });

        var sameSlug = await root.PostAsync("/api/v1/tenants", new { name = "Other", slug = "acme", adminEmail = "other@acme.test" });
        var sameAdmin = await root.PostAsync("/api/v1/tenants", new { name = "Other", slug = "other", adminEmail = "root@platform.test" });
        var invalid = await root.PostAsync("/api/v1/tenants", new { name = "", slug = "Bad Slug", adminEmail = "nope" });

        Assert.Equal(HttpStatusCode.Conflict, sameSlug.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var errors = (await JsonAsync(invalid)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("slug", out _));
        Assert.True(errors.TryGetProperty("adminEmail", out _));
    }

    [Fact]
    public async Task Tenants_AreListedPaged_AndUpdated()
    {
        var root = await SignInSuperAdminAsync();
        foreach (var slug in new[] { "one", "two", "three" })
        {
            await root.PostAsync("/api/v1/tenants", new { name = slug, slug, adminEmail = $"admin@{slug}.test" });
        }

        var page = await JsonAsync(await root.GetAsync("/api/v1/tenants?page=2&pageSize=2"));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
        Assert.Single(page.GetProperty("items").EnumerateArray());

        var id = (await JsonAsync(await root.GetAsync("/api/v1/tenants"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var renamed = await JsonAsync(await root.PatchAsync($"/api/v1/tenants/{id}", new { name = "Renamed" }));
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await root.PatchAsync($"/api/v1/tenants/{Guid.NewGuid()}", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await root.PatchAsync($"/api/v1/tenants/{id}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await root.GetAsync("/api/v1/tenants?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await root.GetAsync("/api/v1/tenants?page=0")).StatusCode);
    }

    [Fact]
    public async Task Invitation_IsCreated_Listed_Mailed_AndRevoked()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");

        var created = await admin.PostAsync("/api/v1/invitations", new { email = "New@Acme.test", role = "Agent" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal("new@acme.test", body.GetProperty("email").GetString());
        Assert.Equal("Agent", body.GetProperty("role").GetString());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Contains(Factory.Email.Sent, m => m.To == "new@acme.test");

        var list = await JsonAsync(await admin.GetAsync("/api/v1/invitations"));
        Assert.Equal(2, list.GetProperty("total").GetInt32()); // the admin's own (accepted) + the new one

        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/invitations/{id}")).StatusCode);
    }

    [Fact]
    public async Task Invitation_Conflicts_And_Validation()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });

        var pending = await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Requester" });
        var existingUser = await admin.PostAsync("/api/v1/invitations", new { email = "admin@acme.test", role = "Agent" });
        var superAdminRole = await admin.PostAsync("/api/v1/invitations", new { email = "x@acme.test", role = "SuperAdmin" });
        var badEmail = await admin.PostAsync("/api/v1/invitations", new { email = "nope", role = "Agent" });

        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, existingUser.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, superAdminRole.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badEmail.StatusCode);
    }

    [Fact]
    public async Task Invitation_StillExists_WhenTheMailServerFails()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        Factory.Email.FailNext = true;

        var created = await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(await QueryDbAsync(db => db.Invitations.IgnoreQueryFilters().AnyAsync(i => i.Email == "pat@acme.test")));
    }

    [Fact]
    public async Task Users_RoleChange_Deactivation_AndSelfProtection()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var (agentId, _) = await InviteAndSignInAsync(admin, "pat@acme.test", "Agent");
        var adminId = (await JsonAsync(await admin.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid();

        var promoted = await admin.PatchAsync($"/api/v1/users/{agentId}", new { role = "TenantAdmin" });
        var self = await admin.PatchAsync($"/api/v1/users/{adminId}", new { isActive = false });
        var superRole = await admin.PatchAsync($"/api/v1/users/{agentId}", new { role = "SuperAdmin" });
        var empty = await admin.PatchAsync($"/api/v1/users/{agentId}", new { });
        var deactivated = await admin.PatchAsync($"/api/v1/users/{agentId}", new { isActive = false });

        Assert.Equal("TenantAdmin", (await JsonAsync(promoted)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, superRole.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.False((await JsonAsync(deactivated)).GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsync($"/api/v1/users/{Guid.NewGuid()}", new { isActive = false })).StatusCode);
    }
}

public class DemoEnabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?> Settings => new() { ["Demo:Enabled"] = "true" };

    [Theory]
    [InlineData("admin", "TenantAdmin")]
    [InlineData("agent", "Agent")]
    [InlineData("demandeur", "Requester")]
    public async Task DemoLogin_SignsInTheFictionalAccounts(string demoRole, string expectedRole)
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role = demoRole });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await JsonAsync(response);
        Assert.Equal(900, token.GetProperty("expiresIn").GetInt32());
        Assert.Contains("deskflow_refresh=", string.Join(";", response.Headers.GetValues("Set-Cookie")));
        var session = new Session("demo", token.GetProperty("accessToken").GetString()!, "", Http);
        var me = await JsonAsync(await session.GetAsync("/api/v1/me"));
        Assert.Equal(expectedRole, me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("isDemo").GetBoolean());
        Assert.Contains("démo", me.GetProperty("tenant").GetProperty("name").GetString());
    }

    [Fact]
    public async Task DemoAdmin_CanManageDemoUsers_ButTheSeederIsIdempotent()
    {
        var token = await JsonAsync(await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role = "admin" }));
        var session = new Session("demo", token.GetProperty("accessToken").GetString()!, "", Http);

        var users = await JsonAsync(await session.GetAsync("/api/v1/users"));

        Assert.Equal(3, users.GetProperty("total").GetInt32());
        Assert.Equal(4, await QueryDbAsync(db => db.SlaPolicies.IgnoreQueryFilters().CountAsync()));
        await Desk.Infrastructure.Bootstrap.DatabaseInitializer.RunAsync(Factory.Services);
        Assert.Equal(3, await QueryDbAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId != null)));
        Assert.Equal(1, await QueryDbAsync(db => db.Tenants.CountAsync()));
    }

    [Fact]
    public async Task DemoLogin_RejectsUnknownRole()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role = "superadmin" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}

public class DemoDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task DemoLogin_Is404_WhenDisabled()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role = "admin" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(await QueryDbAsync(db => db.Tenants.AnyAsync(t => t.Slug == "demo")));
    }
}
