using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invitations").WithTags("invitations").RequireAuthorization(Policies.TenantAdmin);

        group.MapGet("/", List).WithName("listInvitations");
        group.MapPost("/", Create).WithName("createInvitation").AddEndpointFilter<ValidationFilter<CreateInvitationRequest>>();
        group.MapDelete("/{id:guid}", Revoke).WithName("revokeInvitation");
        return app;
    }

    private static InvitationDto ToDto(Invitation i, DateTimeOffset now) =>
        new(i.Id, i.Email, i.Role, i.ExpiresAt, i.AcceptedAt, InvitationRules.StatusOf(i, now));

    private static async Task<IResult> List(DeskDbContext db, TimeProvider clock, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        if (PagingExtensions.Validate(page, pageSize) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        return Results.Ok(await db.Invitations.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToPageAsync(page, pageSize, i => ToDto(i, now), ct));
    }

    private static async Task<IResult> Create(
        CreateInvitationRequest request, HttpContext http, DeskDbContext db, InvitationService invitations,
        TimeProvider clock, CancellationToken ct)
    {
        var role = Enum.Parse<Role>(request.Role!);
        if (!RolePolicy.CanInvite(http.User.RoleOf() ?? Role.Requester, role))
        {
            return Results.Problem(statusCode: 403, title: "Accès refusé");
        }

        var email = InvitationRules.NormalizeEmail(request.Email!);
        var now = clock.GetUtcNow();

        // One account per e-mail on the whole platform; the answer stays generic.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return Problems.Conflict("Cette adresse e-mail ne peut pas être invitée.");
        }

        if (await db.Invitations.AnyAsync(i => i.Email == email && i.AcceptedAt == null && i.ExpiresAt > now, ct))
        {
            return Problems.Conflict("Une invitation est déjà en attente pour cette adresse e-mail.");
        }

        var tenantId = Guid.Parse(http.User.FindFirst("tid")!.Value);
        var tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
        var (invitation, rawToken) = invitations.Build(tenantId, email, role);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        await invitations.SendAsync(invitation, rawToken, tenantName, ct);
        return Results.Created($"/api/v1/invitations/{invitation.Id}", ToDto(invitation, now));
    }

    private static async Task<IResult> Revoke(Guid id, DeskDbContext db, CancellationToken ct)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id && i.AcceptedAt == null, ct);
        if (invitation is null)
        {
            return Problems.NotFound();
        }

        db.Invitations.Remove(invitation);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
