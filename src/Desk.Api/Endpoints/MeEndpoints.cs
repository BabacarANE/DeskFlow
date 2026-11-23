using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me", GetMe).WithTags("me").WithName("getMe").RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetMe(
        HttpContext http, DeskDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        var id = http.User.UserId();
        // The SuperAdmin has no tenant (the filter would hide him): the id comes from the verified JWT subject.
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null || !user.IsActive || user.Tenant is { IsActive: false })
        {
            return Problems.Unauthorized("Compte indisponible.");
        }

        var tenant = user.Tenant is null
            ? null
            : new MeTenantDto(user.Tenant.Id, user.Tenant.Name, user.Tenant.Slug,
                new ThemeDto(user.Tenant.ColorPrimary, user.Tenant.ColorSurface, user.Tenant.ColorAccent));
        return Results.Ok(new MeDto(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.Role, tenant,
            configuration.GetValue<bool>("Demo:Enabled")));
    }
}
