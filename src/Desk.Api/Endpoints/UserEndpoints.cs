using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Infrastructure.Auth;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>Tenant-scoped: the global query filter hides other tenants, so foreign ids answer 404.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("users").RequireAuthorization(Policies.TenantAdmin);

        group.MapGet("/", List).WithName("listUsers");
        group.MapPatch("/{id:guid}", Update).WithName("updateUser").AddEndpointFilter<ValidationFilter<UpdateUserRequest>>();
        return app;
    }

    private static async Task<IResult> List(DeskDbContext db, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        if (PagingExtensions.Validate(page, pageSize) is { } invalid)
        {
            return invalid;
        }

        return Results.Ok(await db.Users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .ToPageAsync(page, pageSize, UserDto.From, ct));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateUserRequest request, HttpContext http, DeskDbContext db, RefreshTokenService refresh, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Problems.NotFound();
        }

        var actorRole = http.User.RoleOf() ?? Role.Requester;
        if (!RolePolicy.CanManageUser(actorRole, http.User.UserId(), user.Id, user.Role))
        {
            return Problems.Conflict("Vous ne pouvez pas modifier votre propre compte.");
        }

        if (request.Role is not null)
        {
            var role = Enum.Parse<Role>(request.Role);
            if (!RolePolicy.CanAssignRole(actorRole, role))
            {
                return Problems.Conflict("Ce rôle ne peut pas être attribué.");
            }

            user.Role = role;
        }

        if (request.IsActive is { } active)
        {
            user.IsActive = active;
        }

        await db.SaveChangesAsync(ct);
        if (!user.IsActive)
        {
            await refresh.RevokeAllForUserAsync(user.Id, ct);
        }

        return Results.Ok(UserDto.From(user));
    }
}
