using System.Net;
using System.Net.Http.Json;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Desk.IntegrationTests;

public class AuthFlowTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task FirstSignIn_WithMatchingInvitation_CreatesUser_AndSetsRefreshCookie()
    {
        var (tenantId, _) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var admin = await SignInAsync("admin@acme.test"); // second sign-in: existing identity

        var me = await JsonAsync(await admin.GetAsync("/api/v1/me"));

        Assert.Equal("admin@acme.test", me.GetProperty("email").GetString());
        Assert.Equal("TenantAdmin", me.GetProperty("role").GetString());
        Assert.Equal(tenantId, me.GetProperty("tenant").GetProperty("id").GetGuid());
        var theme = me.GetProperty("tenant").GetProperty("theme");
        Assert.Equal("#7CC4EE", theme.GetProperty("colorPrimary").GetString());
        Assert.Equal("#F4F7FA", theme.GetProperty("colorSurface").GetString());
        Assert.Equal("#D63A14", theme.GetProperty("colorAccent").GetString());
        Assert.False(me.GetProperty("isDemo").GetBoolean());
    }

    [Fact]
    public async Task RefreshCookie_IsHttpOnly_Secure_LaxAndScopedToAuth()
    {
        await CreateTenantWithAdminAsync("acme", "admin@acme.test");

        var login = await ExternalLoginAsync("google", "sub-admin@acme.test", "admin@acme.test");

        Assert.Equal("/auth/callback", login.Location);
        var header = login.SetCookieHeader!.ToLowerInvariant();
        Assert.Contains("httponly", header);
        Assert.Contains("secure", header);
        Assert.Contains("samesite=lax", header);
        Assert.Contains("path=/api/v1/auth", header);
        Assert.Contains("expires=", header);
    }

    [Fact]
    public async Task AccessToken_HasExpectedClaims_AndLasts15Minutes()
    {
        var (tenantId, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(admin.AccessToken);

        Assert.Equal("TenantAdmin", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.Equal(tenantId.ToString(), jwt.Claims.Single(c => c.Type == "tid").Value);
        Assert.Equal("admin@acme.test", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Claims.Single(c => c.Type == "name").Value));
        Assert.True(Guid.TryParse(jwt.Subject, out _));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.ValidFrom);
    }

    [Fact]
    public async Task SuperAdmin_HasNoTenantClaim_AndNullTenantInMe()
    {
        var root = await SignInSuperAdminAsync();

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(root.AccessToken);
        var me = await JsonAsync(await root.GetAsync("/api/v1/me"));

        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tid");
        Assert.Equal("SuperAdmin", me.GetProperty("role").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("tenant").ValueKind);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("github")]
    public async Task BothProviders_SignInWithInvitation(string provider)
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Requester" });

        var session = await SignInAsync("pat@acme.test", Factory.Email.InvitationTokenFor("pat@acme.test"), provider);

        var me = await JsonAsync(await session.GetAsync("/api/v1/me"));
        Assert.Equal("Requester", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task FirstSignIn_WithoutInvitation_IsRefused()
    {
        await CreateTenantWithAdminAsync("acme", "admin@acme.test");

        var login = await ExternalLoginAsync("google", "stranger", "stranger@acme.test");

        Assert.Equal("invitation_required", login.Error);
        Assert.Null(login.RefreshCookie);
        Assert.False(await QueryDbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "stranger@acme.test")));
    }

    [Fact]
    public async Task UnknownInvitationToken_IsRefused()
    {
        var login = await ExternalLoginAsync("google", "stranger", "stranger@acme.test", invitation: "not-a-real-token");

        Assert.Equal("invitation_invalid", login.Error);
        Assert.Null(login.RefreshCookie);
    }

    [Fact]
    public async Task Invitation_WithDifferentVerifiedEmail_IsRefused_AndStaysUsable()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });
        var token = Factory.Email.InvitationTokenFor("pat@acme.test");

        var wrong = await ExternalLoginAsync("google", "intruder", "intruder@evil.test", token);

        Assert.Equal("email_mismatch", wrong.Error);
        Assert.Null(wrong.RefreshCookie);
        var right = await ExternalLoginAsync("google", "pat", "pat@acme.test", token);
        Assert.Null(right.Error);
        Assert.NotNull(right.RefreshCookie);
    }

    [Fact]
    public async Task Invitation_WithUnverifiedEmail_IsRefused()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });

        var login = await ExternalLoginAsync("github", "pat", "pat@acme.test", Factory.Email.InvitationTokenFor("pat@acme.test"), verified: false);

        Assert.Equal("email_not_verified", login.Error);
        Assert.Null(login.RefreshCookie);
    }

    [Fact]
    public async Task ExpiredInvitation_IsRefused()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });
        await WithDbAsync(async db =>
        {
            var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == "pat@acme.test");
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        var login = await ExternalLoginAsync("google", "pat", "pat@acme.test", Factory.Email.InvitationTokenFor("pat@acme.test"));

        Assert.Equal("invitation_invalid", login.Error);
        Assert.Null(login.RefreshCookie);
    }

    [Fact]
    public async Task AlreadyUsedInvitation_IsRefused()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await admin.PostAsync("/api/v1/invitations", new { email = "pat@acme.test", role = "Agent" });
        await WithDbAsync(async db =>
        {
            var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == "pat@acme.test");
            invitation.AcceptedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        var login = await ExternalLoginAsync("google", "pat", "pat@acme.test", Factory.Email.InvitationTokenFor("pat@acme.test"));

        Assert.Equal("invitation_invalid", login.Error);
        Assert.False(await QueryDbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "pat@acme.test")));
    }

    [Fact]
    public async Task Invitation_IsSingleUse_AndMarkedAccepted()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await InviteAndSignInAsync(admin, "pat@acme.test", "Agent");
        var token = Factory.Email.InvitationTokenFor("pat@acme.test");

        var other = await ExternalLoginAsync("github", "someone-else", "other@acme.test", token);

        Assert.Equal("invitation_invalid", other.Error);
        var page = await JsonAsync(await admin.GetAsync("/api/v1/invitations"));
        var accepted = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == "pat@acme.test");
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeactivatedUser_CannotSignIn_NorRefresh()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var (agentId, agent) = await InviteAndSignInAsync(admin, "pat@acme.test", "Agent");

        var patch = await admin.PatchAsync($"/api/v1/users/{agentId}", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(agent.RefreshCookie)).StatusCode);
        Assert.Equal("account_disabled", (await ExternalLoginAsync("google", "sub-pat@acme.test", "pat@acme.test")).Error);
    }

    [Fact]
    public async Task SuspendedTenant_CannotSignIn_NorRefresh()
    {
        var (tenantId, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var root = await SignInSuperAdminAsync();

        var patch = await root.PatchAsync($"/api/v1/tenants/{tenantId}", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(admin.RefreshCookie)).StatusCode);
        Assert.Equal("tenant_suspended", (await ExternalLoginAsync("google", "sub-admin@acme.test", "admin@acme.test")).Error);
    }

    [Fact]
    public async Task Refresh_Rotates_AndReusingAnOldToken_RevokesTheWholeFamily()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var first = admin.RefreshCookie;

        var second = await RefreshAsync(first);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondCookie = NewRefreshCookie(second);
        Assert.NotNull(secondCookie);
        Assert.NotEqual(first, secondCookie);

        // Replaying the first (already rotated) token is theft: refused...
        var replay = await RefreshAsync(first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...and the legitimate newest token of the family is revoked too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(secondCookie)).StatusCode);
        var family = await QueryDbAsync(db => db.RefreshTokens.Where(t => t.RevokedAt == null && db.Users.IgnoreQueryFilters().Any(u => u.Id == t.UserId && u.Email == "admin@acme.test")).CountAsync());
        Assert.Equal(0, family);
    }

    [Fact]
    public async Task Refresh_StoresOnlyAHash_AndFamiliesAreIndependent()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var otherDevice = await SignInAsync("admin@acme.test");

        var stored = await QueryDbAsync(db => db.RefreshTokens.Select(t => t.TokenHash).ToListAsync());
        Assert.DoesNotContain(admin.RefreshCookie, stored);
        Assert.All(stored, h => Assert.Equal(64, h.Length));

        // Reuse on one device does not log the other one out.
        var rotated = await RefreshAsync(otherDevice.RefreshCookie);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(otherDevice.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(admin.RefreshCookie)).StatusCode);
    }

    [Fact]
    public async Task Refresh_RejectsExpiredToken_AndMissingCookie()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        await WithDbAsync(async db =>
        {
            foreach (var t in await db.RefreshTokens.ToListAsync())
            {
                t.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }

            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(admin.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync("garbage")).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken_AndClearsTheCookie()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var request = admin.Request(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", $"deskflow_refresh={admin.RefreshCookie}");

        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("deskflow_refresh=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(admin.RefreshCookie)).StatusCode);
    }

    [Fact]
    public async Task Logout_RequiresAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync("/api/v1/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task StartLogin_RedirectsToTheProvider_WithTheRightScopes()
    {
        var google = await Http.GetAsync("/api/v1/auth/external/google?invitation=abc");
        var github = await Http.GetAsync("/api/v1/auth/external/github");

        Assert.Equal(HttpStatusCode.Redirect, google.StatusCode);
        Assert.StartsWith("https://accounts.google.com/", google.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, github.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize", github.Headers.Location!.ToString());
        Assert.Contains("user%3Aemail", github.Headers.Location!.ToString());
        Assert.Contains("signin-github", github.Headers.Location!.ToString());
    }

    [Fact]
    public async Task StartLogin_UnknownProvider_Is404()
    {
        var response = await Http.GetAsync("/api/v1/auth/external/facebook");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProviderError_RedirectsToTheFrontWithAnErrorCode()
    {
        var start = await Http.GetAsync("/api/v1/auth/external/google");
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"]!;
        var cookies = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var callback = new HttpRequestMessage(HttpMethod.Get, $"/signin-google?error=access_denied&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", cookies);

        var response = await Http.SendAsync(callback);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/callback?error=provider_error", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ProblemDetails_AreReturnedForErrors()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role = "nope" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("role", out _));
    }
}

public class OAuthNotConfiguredTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override bool OAuthConfigured => false;

    [Fact]
    public async Task ApiStarts_AndAnswersAClearError_WhenNoProviderIsConfigured()
    {
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/health")).StatusCode);

        var response = await Http.GetAsync("/api/v1/auth/external/google");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("non configuré", (await JsonAsync(response)).GetProperty("detail").GetString());
    }
}

public class AuthRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?> Settings => new() { ["RateLimiting:AuthPermitLimit"] = "3" };

    [Fact]
    public async Task AuthEndpoints_AreRateLimited_OthersAreNot()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await Http.PostAsync("/api/v1/auth/refresh", null)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/health")).StatusCode);
        }
    }
}

/// <summary>Account linking by verified e-mail (no invitation needed for an already known address).</summary>
public class EmailLinkingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task BootstrappedSuperAdmin_LinksOnFirstSignIn_WithoutInvitation()
    {
        var login = await ExternalLoginAsync("github", "gh-root", "Root@Platform.test");

        Assert.Null(login.Error);
        Assert.NotNull(login.RefreshCookie);
        Assert.Equal(1, await QueryDbAsync(db => db.ExternalLogins.CountAsync(l => l.ProviderKey == "gh-root")));
    }

    [Fact]
    public async Task ExistingUser_CanLinkASecondProvider()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test"); // linked to google

        var login = await ExternalLoginAsync("github", "gh-admin", "admin@acme.test");

        Assert.Null(login.Error);
        Assert.NotNull(login.RefreshCookie);
        Assert.Equal(2, await QueryDbAsync(db => db.ExternalLogins.CountAsync(l => l.UserId == db.Users.IgnoreQueryFilters().Single(u => u.Email == "admin@acme.test").Id)));
        Assert.NotNull(admin);
    }

    [Fact]
    public async Task LinkingByEmail_RefusesUnverifiedEmail()
    {
        await CreateTenantWithAdminAsync("acme", "admin@acme.test");

        var login = await ExternalLoginAsync("github", "gh-admin", "admin@acme.test", verified: false);

        Assert.Equal("email_not_verified", login.Error);
        Assert.Null(login.RefreshCookie);
        Assert.False(await QueryDbAsync(db => db.ExternalLogins.AnyAsync(l => l.ProviderKey == "gh-admin")));
    }

    [Fact]
    public async Task LinkingByEmail_RefusesDisabledAccount_AndLinksNothing()
    {
        var (_, admin) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var (agentId, _) = await InviteAndSignInAsync(admin, "pat@acme.test", "Agent");
        await admin.PatchAsync($"/api/v1/users/{agentId}", new { isActive = false });

        var login = await ExternalLoginAsync("github", "gh-pat", "pat@acme.test");

        Assert.Equal("account_disabled", login.Error);
        Assert.Null(login.RefreshCookie);
        Assert.False(await QueryDbAsync(db => db.ExternalLogins.AnyAsync(l => l.ProviderKey == "gh-pat")));
    }

    [Fact]
    public async Task LinkingByEmail_RefusesSuspendedTenant_AndLinksNothing()
    {
        var (tenantId, _) = await CreateTenantWithAdminAsync("acme", "admin@acme.test");
        var root = await SignInSuperAdminAsync();
        await root.PatchAsync($"/api/v1/tenants/{tenantId}", new { isActive = false });

        var login = await ExternalLoginAsync("github", "gh-admin", "admin@acme.test");

        Assert.Equal("tenant_suspended", login.Error);
        Assert.Null(login.RefreshCookie);
        Assert.False(await QueryDbAsync(db => db.ExternalLogins.AnyAsync(l => l.ProviderKey == "gh-admin")));
    }
}
