using System.Security.Claims;
using System.Text.Json;
using AspNet.Security.OAuth.GitHub;
using Desk.Api.Http;
using Desk.Api.Realtime;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Desk.Api.Auth;

public static class AuthenticationSetup
{
    public const string CallbackPath = "/auth/callback";
    public const string InvitationItem = "invitation";

    public static IServiceCollection AddDeskAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtOptions>((o, jwt) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = JwtTokenService.ValidationParameters(jwt);
                // Browsers cannot set a header on a WebSocket: SignalR sends the token as ?access_token=. Accepted for the hub
                // path only: anywhere else a token in a URL (logged by proxies) is ignored.
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(NotificationsHub.Path))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        // Providers without credentials are simply not registered: the API still starts.
        var google = configuration.GetSection("Authentication:Google");
        if (!string.IsNullOrWhiteSpace(google["ClientId"]) && !string.IsNullOrWhiteSpace(google["ClientSecret"]))
        {
            auth.AddGoogle(ExternalProviders.GoogleScheme, o =>
            {
                o.ClientId = google["ClientId"]!;
                o.ClientSecret = google["ClientSecret"]!;
                o.ClaimActions.MapCustomJson(ExternalProviders.EmailVerifiedClaim, json =>
                    json.TryGetProperty("email_verified", out var v) ? v.ToString().ToLowerInvariant() : null);
                o.ClaimActions.MapJsonKey(ExternalProviders.AvatarClaim, "picture");
                Hook(o.Events, ExternalProviders.Google);
            });
        }

        var gitHub = configuration.GetSection("Authentication:GitHub");
        if (!string.IsNullOrWhiteSpace(gitHub["ClientId"]) && !string.IsNullOrWhiteSpace(gitHub["ClientSecret"]))
        {
            auth.AddGitHub(ExternalProviders.GitHubScheme, o =>
            {
                o.ClientId = gitHub["ClientId"]!;
                o.ClientSecret = gitHub["ClientSecret"]!;
                o.Scope.Add("user:email");
                o.ClaimActions.MapJsonKey(ExternalProviders.AvatarClaim, "avatar_url");
                Hook(o.Events, ExternalProviders.GitHub);
                o.Events.OnCreatingTicket = AddVerifiedPrimaryEmail;
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SuperAdmin, p => p.RequireAuthenticatedUser().RequireRole(nameof(Role.SuperAdmin)))
            .AddPolicy(Policies.TenantAdmin, p => p.RequireAuthenticatedUser().RequireRole(nameof(Role.TenantAdmin)).RequireClaim("tid"))
            .AddPolicy(Policies.Agent, p => p.RequireAuthenticatedUser()
                .RequireRole(nameof(Role.TenantAdmin), nameof(Role.Agent)).RequireClaim("tid"))
            .AddPolicy(Policies.TenantMember, p => p.RequireAuthenticatedUser()
                .RequireRole(nameof(Role.TenantAdmin), nameof(Role.Agent), nameof(Role.Requester)).RequireClaim("tid"));
        return services;
    }

    private static void Hook(OAuthEvents events, string provider)
    {
        events.OnTicketReceived = async ctx =>
        {
            var identity = ctx.Principal is null ? null : ExternalIdentityMapper.FromPrincipal(provider, ctx.Principal);
            var http = ctx.HttpContext;
            string? error;
            if (identity is null)
            {
                error = LoginErrors.ProviderError;
            }
            else
            {
                string? invitationToken = null;
                ctx.Properties?.Items.TryGetValue(InvitationItem, out invitationToken);
                var result = await http.RequestServices.GetRequiredService<ExternalLoginService>()
                    .CompleteAsync(identity, invitationToken, http.RequestAborted);
                error = result.Error;
                if (result.User is not null)
                {
                    var token = await http.RequestServices.GetRequiredService<RefreshTokenService>()
                        .IssueAsync(result.User, http.RequestAborted);
                    RefreshCookie.Set(http, token);
                }
            }

            http.Response.Redirect(error is null ? CallbackPath : $"{CallbackPath}?error={error}");
            ctx.HandleResponse();
        };
        events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect($"{CallbackPath}?error={LoginErrors.ProviderError}");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>GitHub's public profile e-mail is unverified: keep only the primary address GitHub verified.</summary>
    private static async Task AddVerifiedPrimaryEmail(OAuthCreatingTicketContext ctx)
    {
        var identity = ctx.Identity;
        if (identity is null)
        {
            return;
        }

        foreach (var claim in identity.FindAll(ClaimTypes.Email).ToList())
        {
            identity.RemoveClaim(claim);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
        request.Headers.Authorization = new("Bearer", ctx.AccessToken);
        request.Headers.Accept.Add(new("application/vnd.github+json"));
        using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
        if (GitHubEmails.PickPrimaryVerified(doc.RootElement) is { } email)
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
            identity.AddClaim(new Claim(ExternalProviders.EmailVerifiedClaim, "true"));
        }
    }
}
