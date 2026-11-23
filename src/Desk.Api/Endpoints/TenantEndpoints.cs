using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>Platform endpoints: the only place where the SuperAdmin reads across tenants (IgnoreQueryFilters).</summary>
public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants").WithTags("tenants").RequireAuthorization(Policies.SuperAdmin);

        group.MapGet("/", List).WithName("listTenants");
        group.MapPost("/", Create).WithName("createTenant").AddEndpointFilter<ValidationFilter<CreateTenantRequest>>();
        group.MapPatch("/{id:guid}", Update).WithName("updateTenant").AddEndpointFilter<ValidationFilter<UpdateTenantRequest>>();
        return app;
    }

    private static async Task<IResult> List(DeskDbContext db, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        if (PagingExtensions.Validate(page, pageSize) is { } invalid)
        {
            return invalid;
        }

        return Results.Ok(await db.Tenants.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToPageAsync(page, pageSize, TenantDto.From, ct));
    }

    private static async Task<IResult> Create(
        CreateTenantRequest request, DeskDbContext db, InvitationService invitations, TimeProvider clock, CancellationToken ct)
    {
        var email = InvitationRules.NormalizeEmail(request.AdminEmail!);
        var slug = request.Slug!;

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            return Problems.Conflict("Ce slug est déjà utilisé.");
        }

        // E-mail is unique across the platform: this check must see every tenant.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return Problems.Conflict("Cette adresse e-mail est déjà associée à un compte.");
        }

        var tenant = new Tenant { Name = request.Name!.Trim(), Slug = slug, CreatedAt = clock.GetUtcNow() };
        var (invitation, rawToken) = invitations.Build(tenant.Id, email, Role.TenantAdmin);
        db.Tenants.Add(tenant);
        db.SlaPolicies.AddRange(SlaPolicy.CreateDefaults(tenant.Id));
        db.Invitations.Add(invitation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict("Ce slug est déjà utilisé.");
        }

        await invitations.SendAsync(invitation, rawToken, tenant.Name, ct);
        return Results.Created($"/api/v1/tenants/{tenant.Id}", TenantDto.From(tenant));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateTenantRequest request, DeskDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
        {
            return Problems.NotFound();
        }

        if (request.Name is not null)
        {
            tenant.Name = request.Name.Trim();
        }

        if (request.IsActive is { } active)
        {
            tenant.IsActive = active;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(TenantDto.From(tenant));
    }
}
