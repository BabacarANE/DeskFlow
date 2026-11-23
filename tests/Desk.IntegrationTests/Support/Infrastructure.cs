using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AspNet.Security.OAuth.GitHub;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Desk.IntegrationTests.Support;

/// <summary>One PostgreSQL container for the whole run; every test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>, ICollectionFixture<MailpitFixture>;

public sealed record SentEmail(string To, string Subject, string Body, string? Html = null);

public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<SentEmail> Sent { get; } = new();

    /// <summary>Simulates an SMTP outage for the next message.</summary>
    public bool FailNext { get; set; }

    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("SMTP down");
        }

        Sent.Enqueue(new SentEmail(to, subject, textBody));
        return Task.CompletedTask;
    }

    public Task SendAsync(string to, string subject, string textBody, string? htmlBody, CancellationToken ct = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("SMTP down");
        }

        Sent.Enqueue(new SentEmail(to, subject, textBody, htmlBody));
        return Task.CompletedTask;
    }

    /// <summary>The clear invitation token only exists in the e-mail link.</summary>
    public string InvitationTokenFor(string email)
    {
        var mail = Sent.Last(m => m.To == email);
        return Regex.Match(mail.Body, @"invitation=([^\s]+)").Groups[1].Value;
    }
}

/// <summary>
/// Replaces the network part of the OAuth handlers. State, correlation cookie, callback path and the
/// application's TicketReceived logic stay real. The "code" query value carries the simulated identity:
/// <c>sub|email|verified|name</c>.
/// </summary>
public static class FakeOAuth
{
    public static string Code(string sub, string email, bool verified = true, string name = "Test User") =>
        $"{sub}|{email}|{(verified ? "true" : "false")}|{name}";

    internal static OAuthTokenResponse Tokens() => OAuthTokenResponse.Success(JsonDocument.Parse("""{"access_token":"fake"}"""));

    internal static AuthenticationTicket Ticket(ClaimsIdentity identity, AuthenticationProperties props, string scheme, string? code)
    {
        var parts = (code ?? "").Split('|');
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, parts[0]));
        identity.AddClaim(new Claim(ClaimTypes.Email, parts[1]));
        identity.AddClaim(new Claim("email_verified", parts[2]));
        identity.AddClaim(new Claim(ClaimTypes.Name, parts[3]));
        return new AuthenticationTicket(new ClaimsPrincipal(identity), props, scheme);
    }
}

public sealed class FakeGoogleHandler(IOptionsMonitor<GoogleOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : GoogleHandler(options, logger, encoder)
{
    protected override Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context) => Task.FromResult(FakeOAuth.Tokens());

    protected override Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, OAuthTokenResponse tokens) =>
        Task.FromResult(FakeOAuth.Ticket(identity, properties, Scheme.Name, Request.Query["code"]));
}

public sealed class FakeGitHubHandler(IOptionsMonitor<GitHubAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : GitHubAuthenticationHandler(options, logger, encoder)
{
    protected override Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context) => Task.FromResult(FakeOAuth.Tokens());

    protected override Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, OAuthTokenResponse tokens) =>
        Task.FromResult(FakeOAuth.Ticket(identity, properties, Scheme.Name, Request.Query["code"]));
}

/// <summary>
/// Fault injection inside the database transaction of the API. <see cref="FailAfterSave"/> throws once the rows are
/// written but before the explicit transaction commits; <see cref="FailOutboxInsert"/> refuses the statement that
/// inserts the outbox row (EF sends it in the same batch as the business rows).
/// </summary>
public sealed class Faults
{
    public volatile bool FailAfterSave;
    public volatile bool FailOutboxInsert;

    public Interceptors.AfterSave AfterSave { get; } = new();

    public Interceptors.OnCommand OnCommand { get; } = new();

    public Faults() => (AfterSave.Owner, OnCommand.Owner) = (this, this);
}

public static class Interceptors
{
    public sealed class AfterSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        internal Faults Owner { get; set; } = null!;

        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            Owner.FailAfterSave ? throw new InvalidOperationException("Injected failure after the rows were written.") : ValueTask.FromResult(result);
    }

    public sealed class OnCommand : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal Faults Owner { get; set; } = null!;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            Owner.FailOutboxInsert && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Injected failure on the outbox insert.")
                : ValueTask.FromResult(result);
    }
}

public sealed class DeskFactory(string connectionString, Dictionary<string, string?>? settings = null, bool oauthConfigured = true)
    : WebApplicationFactory<Program>
{
    public FakeEmailSender Email { get; } = new();

    public Faults Faults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var all = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@127.0.0.1:1",
            ["Jwt:Key"] = "test-signing-key-test-signing-key-0123456789",
            ["SuperAdmin:Email"] = "root@platform.test",
            ["RateLimiting:AuthPermitLimit"] = "10000",
        };
        if (oauthConfigured)
        {
            all["Authentication:Google:ClientId"] = "google-test-id";
            all["Authentication:Google:ClientSecret"] = "google-test-secret";
            all["Authentication:GitHub:ClientId"] = "github-test-id";
            all["Authentication:GitHub:ClientSecret"] = "github-test-secret";
        }

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        // UseSetting: minimal hosting reads configuration before late overrides would apply.
        foreach (var (key, value) in all)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IEmailSender>(Email);
            services.ConfigureDbContext<DeskDbContext>(o => o.AddInterceptors(Faults.AfterSave, Faults.OnCommand));
            services.AddTransient<FakeGoogleHandler>();
            services.AddTransient<FakeGitHubHandler>();
            services.Configure<AuthenticationOptions>(o =>
            {
                foreach (var scheme in o.Schemes)
                {
                    if (scheme.Name == "Google")
                    {
                        scheme.HandlerType = typeof(FakeGoogleHandler);
                    }
                    else if (scheme.Name == "GitHub")
                    {
                        scheme.HandlerType = typeof(FakeGitHubHandler);
                    }
                }
            });
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
}

public sealed record LoginResult(string Location, string? Error, string? RefreshCookie, string? SetCookieHeader);

public sealed record Session(string Email, string AccessToken, string RefreshCookie, HttpClient Http)
{
    public HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.SendAsync(Request(HttpMethod.Get, url));

    public Task<HttpResponseMessage> PostAsync(string url, object body) => Http.SendAsync(Request(HttpMethod.Post, url, body));

    public Task<HttpResponseMessage> PutAsync(string url, object body) => Http.SendAsync(Request(HttpMethod.Put, url, body));

    public Task<HttpResponseMessage> PatchAsync(string url, object body) => Http.SendAsync(Request(HttpMethod.Patch, url, body));

    public Task<HttpResponseMessage> DeleteAsync(string url) => Http.SendAsync(Request(HttpMethod.Delete, url));
}

/// <summary>Base of API tests: a fresh database and application per test.</summary>
[Collection("postgres")]
public abstract class ApiTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected virtual Dictionary<string, string?>? Settings => null;

    protected virtual bool OAuthConfigured => true;

    protected DeskFactory Factory { get; private set; } = null!;

    protected HttpClient Http { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        Factory = new DeskFactory(await postgres.CreateDatabaseAsync(), Settings, OAuthConfigured);
        Http = Factory.NewClient();
    }

    public virtual async Task DisposeAsync()
    {
        Http.Dispose();
        await Factory.DisposeAsync();
    }

    /// <summary>Full OAuth round trip: challenge, provider callback (simulated), TicketReceived logic.</summary>
    protected async Task<LoginResult> ExternalLoginAsync(string provider, string sub, string email, string? invitation = null, bool verified = true)
    {
        var start = await Http.GetAsync($"/api/v1/auth/external/{provider}" + (invitation is null ? "" : $"?invitation={Uri.EscapeDataString(invitation)}"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query);
        var cookies = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/signin-{provider}?code={Uri.EscapeDataString(FakeOAuth.Code(sub, email, verified))}&state={Uri.EscapeDataString(query["state"]!)}");
        callback.Headers.Add("Cookie", cookies);
        var response = await Http.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.OriginalString;
        var error = System.Web.HttpUtility.ParseQueryString(location.Contains('?') ? location[(location.IndexOf('?') + 1)..] : "")["error"];
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("deskflow_refresh=", StringComparison.Ordinal))
            : null;
        return new LoginResult(location, error, setCookie?.Split(';')[0]["deskflow_refresh=".Length..], setCookie);
    }

    protected async Task<HttpResponseMessage> RefreshAsync(string? refreshCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        if (refreshCookie is not null)
        {
            request.Headers.Add("Cookie", $"deskflow_refresh={refreshCookie}");
        }

        return await Http.SendAsync(request);
    }

    protected static string? NewRefreshCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("deskflow_refresh=", StringComparison.Ordinal))?.Split(';')[0]["deskflow_refresh=".Length..]
            : null;

    protected async Task<Session> SignInAsync(string email, string? invitation = null, string provider = "google")
    {
        var login = await ExternalLoginAsync(provider, "sub-" + email, email, invitation);
        Assert.Null(login.Error);
        Assert.NotNull(login.RefreshCookie);
        var refresh = await RefreshAsync(login.RefreshCookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var token = (await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        return new Session(email, token, NewRefreshCookie(refresh)!, Http);
    }

    protected Task<Session> SignInSuperAdminAsync() => SignInAsync("root@platform.test");

    /// <summary>SuperAdmin creates the tenant, its first admin accepts the e-mailed invitation.</summary>
    protected async Task<(Guid TenantId, Session Admin)> CreateTenantWithAdminAsync(string slug, string adminEmail)
    {
        var root = await SignInSuperAdminAsync();
        var created = await root.PostAsync("/api/v1/tenants", new { name = "Entreprise " + slug, slug, adminEmail });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenantId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var admin = await SignInAsync(adminEmail, Factory.Email.InvitationTokenFor(adminEmail));
        return (tenantId, admin);
    }

    /// <summary>A tenant admin invites someone, who accepts.</summary>
    protected async Task<(Guid UserId, Session Session)> InviteAndSignInAsync(Session admin, string email, string role)
    {
        var invited = await admin.PostAsync("/api/v1/invitations", new { email, role });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var session = await SignInAsync(email, Factory.Email.InvitationTokenFor(email));
        var me = await (await session.GetAsync("/api/v1/me")).Content.ReadFromJsonAsync<JsonElement>();
        return (me.GetProperty("id").GetGuid(), session);
    }

    protected async Task WithDbAsync(Func<DeskDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginUnscoped();
        await action(scope.ServiceProvider.GetRequiredService<DeskDbContext>());
    }

    protected Task<T> QueryDbAsync<T>(Func<DeskDbContext, Task<T>> query) => QueryCoreAsync(query);

    private async Task<T> QueryCoreAsync<T>(Func<DeskDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginUnscoped();
        return await query(scope.ServiceProvider.GetRequiredService<DeskDbContext>());
    }

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
