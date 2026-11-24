using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>
/// SLA table of the company (US-20). The new values apply to tickets created afterwards: due dates already set are
/// never recomputed (TicketService reads this same table when a ticket is created or its priority changes).
/// </summary>
public static class SlaEndpoints
{
    public static IEndpointRouteBuilder MapSlaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sla-policies").WithTags("sla").RequireAuthorization(Policies.TenantAdmin);

        group.MapGet("/", List).WithName("listSlaPolicies");
        group.MapPut("/", Update).WithName("updateSlaPolicies").AddEndpointFilter<ValidationFilter<UpdateSlaPoliciesRequest>>();
        return app;
    }

    /// <summary>Exactly four lines in the order Low, Normal, High, Urgent; a missing row falls back to the platform default.</summary>
    private static List<SlaPolicyDto> Ordered(IEnumerable<SlaPolicy> rows)
    {
        var byPriority = rows.ToDictionary(p => p.Priority);
        var defaults = SlaPolicy.CreateDefaults(Guid.Empty).ToDictionary(p => p.Priority);
        return Enum.GetValues<Priority>().OrderBy(p => (int)p)
            .Select(p => SlaPolicyDto.From(byPriority.GetValueOrDefault(p) ?? defaults[p]))
            .ToList();
    }

    private static async Task<IResult> List(DeskDbContext db, CancellationToken ct) =>
        Results.Ok(Ordered(await db.SlaPolicies.AsNoTracking().ToListAsync(ct)));

    private static async Task<IResult> Update(UpdateSlaPoliciesRequest request, DeskDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return Problems.Forbidden("Aucune entreprise associée à ce compte.");
        }

        var rows = await db.SlaPolicies.ToListAsync(ct);
        foreach (var line in request.Policies!)
        {
            var priority = Enum.Parse<Priority>(line!.Priority!, ignoreCase: true);
            var row = rows.FirstOrDefault(r => r.Priority == priority);
            if (row is null)
            {
                row = new SlaPolicy { TenantId = tenantId, Priority = priority };
                rows.Add(row);
                db.SlaPolicies.Add(row);
            }

            row.FirstResponseMinutes = line.FirstResponseMinutes!.Value;
            row.ResolutionMinutes = line.ResolutionMinutes!.Value;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(Ordered(rows));
    }
}
