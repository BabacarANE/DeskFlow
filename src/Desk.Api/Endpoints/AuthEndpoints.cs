using Desk.Api.Auth;
using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Infrastructure.Auth;
using Desk.Infrastructure.Bootstrap;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("auth");

        group.MapGet("/external/{provider}", StartExternalLogin).WithName("startExternalLogin").AllowAnonymous();
        group.MapPost("/refresh", Refresh).WithName("refreshToken").AllowAnonymous();
        group.MapPost("/logout", Logout).WithName("logout").RequireAuthorization();
        group.MapPost("/demo", DemoLogin).WithName("demoLogin").AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<DemoLoginRequest>>();
        return app;
    }

    private static async Task<IResult> StartExternalLogin(
        string provider, string? invitation, IAuthenticationSchemeProvider schemes)
    {
        var scheme = ExternalProviders.SchemeFor(provider);
        if (scheme is null || await schemes.GetSchemeAsync(scheme) is null)
        {
            return Problems.NotFound("Fournisseur inconnu ou non configuré.");
        }

        var properties = new AuthenticationProperties { RedirectUri = AuthenticationSetup.CallbackPath };
        if (!string.IsNullOrWhiteSpace(invitation) && invitation.Length <= 200)
        {
            properties.Items[AuthenticationSetup.InvitationItem] = invitation;
        }

        return Results.Challenge(properties, [scheme]);
    }

    private static async Task<IResult> Refresh(
        HttpContext http, RefreshTokenService refresh, JwtTokenService jwt, CancellationToken ct)
    {
        var outcome = await refresh.RotateAsync(http.Request.Cookies[RefreshCookie.Name], ct);
        if (!outcome.Succeeded)
        {
            RefreshCookie.Delete(http);
            return Problems.Unauthorized("Session expirée ou invalide.");
        }

        RefreshCookie.Set(http, outcome.Token!);
        return TokenResponse(http, jwt.Create(outcome.User!));
    }

    private static async Task<IResult> Logout(HttpContext http, RefreshTokenService refresh, CancellationToken ct)
    {
        if (http.Request.Cookies[RefreshCookie.Name] is { Length: > 0 } raw)
        {
            await refresh.RevokeFamilyOfAsync(raw, http.User.UserId(), ct);
        }

        RefreshCookie.Delete(http);
        return Results.NoContent();
    }

    private static async Task<IResult> DemoLogin(
        DemoLoginRequest request, HttpContext http, IConfiguration configuration, DeskDbContext db,
        RefreshTokenService refresh, JwtTokenService jwt, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Demo:Enabled"))
        {
            return Problems.NotFound();
        }

        var email = DemoAccounts.ByDemoRole[request.Role!].Email;
        // Demo sign-in is anonymous, so the tenant filter cannot apply: explicit lookup of the three fixed accounts.
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == email && u.IsActive, ct);
        if (user is null)
        {
            return Problems.NotFound("Compte de démo indisponible.");
        }

        RefreshCookie.Set(http, await refresh.IssueAsync(user, ct));
        return TokenResponse(http, jwt.Create(user));
    }

    private static IResult TokenResponse(HttpContext http, AccessToken token)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new AccessTokenResponse(token.Token, token.ExpiresIn));
    }
}
