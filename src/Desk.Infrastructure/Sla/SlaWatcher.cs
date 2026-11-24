using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desk.Infrastructure.Sla;

/// <summary>Section <c>Sla</c> of the configuration.</summary>
public sealed class SlaWatcherOptions
{
    /// <summary>Switch of the background loop (tests drive <see cref="SlaWatcher.ScanAsync"/> by hand).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Tickets handled per round; a full round is followed by another one, up to <see cref="MaxRounds"/>.</summary>
    public int BatchSize { get; set; } = 200;

    public int MaxRounds { get; set; } = 10;
}

/// <summary>
/// Background service of the Worker (every 60 s): writes <c>ticket.sla.breached</c> to the outbox, ONCE per (ticket, kind).
/// <list type="bullet">
/// <item>Rules: New or Open, not escalated, <c>ResolutionDueAt</c> passed, or <c>ResponseDueAt</c> passed with no first response.
/// The deadlines are the ones <see cref="Desk.Core.Rules.SlaCalculator"/> computed (pause included): no rule is redefined here.</item>
/// <item>Exactly once: <c>SlaBreaches</c> has the primary key (TicketId, Kind). The marker is inserted with
/// <c>ON CONFLICT DO NOTHING</c> in the same transaction as the outbox row: whoever inserts it (one instance among several, or
/// a later cycle finds it present) is the only one to emit. Persistent, so a restart does not re-emit.</item>
/// <item>Tenants: the ONLY cross-tenant read of the product is the candidate query below (<c>IgnoreQueryFilters</c>, read-only,
/// projects ids). Every write happens in its own scope, with the tenant set (<c>UseTenant</c>), after the ticket has been reloaded
/// through the tenant filter and its conditions checked again. A failing tenant is logged and does not stop the others.</item>
/// </list>
/// </summary>
public sealed class SlaWatcher(
    IServiceScopeFactory scopes, TimeProvider clock, IOptions<SlaWatcherOptions> options, ILogger<SlaWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("SlaWatcher disabled (Sla:Enabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(settings.WatchInterval);
        try
        {
            do
            {
                try
                {
                    var emitted = await ScanAsync(stoppingToken);
                    if (emitted > 0)
                    {
                        logger.LogInformation("SlaWatcher: {Count} SLA breach event(s) written to the outbox", emitted);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Database down, schema not migrated yet...: the next cycle tries again, the host keeps running.
                    logger.LogError(ex, "SlaWatcher cycle failed; retrying at the next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
    }

    /// <summary>One detection pass over every tenant. Returns the number of <c>ticket.sla.breached</c> events written.</summary>
    public async Task<int> ScanAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var emitted = 0;
        var seen = new List<Guid>();

        for (var round = 0; round < settings.MaxRounds; round++)
        {
            var candidates = await FindCandidatesAsync(now, seen, settings.BatchSize, ct);
            foreach (var tenant in candidates.GroupBy(c => c.TenantId))
            {
                try
                {
                    emitted += await EmitForTenantAsync(tenant.Key, tenant.Select(c => c.TicketId).Order().ToList(), now, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "SlaWatcher: tenant {TenantId} skipped for this cycle", tenant.Key);
                }
            }

            seen.AddRange(candidates.Select(c => c.TicketId));
            if (candidates.Count < settings.BatchSize)
            {
                break;
            }
        }

        return emitted;
    }

    private sealed record Candidate(Guid TicketId, Guid TenantId);

    /// <summary>Cross-tenant, read-only: identifiers of the tickets that look overdue and have no marker yet.</summary>
    private async Task<List<Candidate>> FindCandidatesAsync(DateTimeOffset now, List<Guid> exclude, int batchSize, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();

        return await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => (t.Status == TicketStatus.New || t.Status == TicketStatus.Open) && t.EscalatedAt == null)
            .Where(t => !exclude.Contains(t.Id))
            .Where(t =>
                (t.ResolutionDueAt < now
                    && !db.SlaBreaches.IgnoreQueryFilters().Any(b => b.TicketId == t.Id && b.Kind == SlaBreach.ResolutionKind))
                || (t.ResponseDueAt < now && t.FirstResponseAt == null
                    && !db.SlaBreaches.IgnoreQueryFilters().Any(b => b.TicketId == t.Id && b.Kind == SlaBreach.ResponseKind)))
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .Select(t => new Candidate(t.Id, t.TenantId))
            .ToListAsync(ct);
    }

    /// <summary>All the writes of one tenant: tenant set first, one transaction, markers and outbox rows together.</summary>
    private async Task<int> EmitForTenantAsync(Guid tenantId, List<Guid> ticketIds, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<TenantContext>().UseTenant(tenantId);
        sp.GetRequiredService<CorrelationContext>().Id = "sla-" + Guid.NewGuid().ToString("N");
        var db = sp.GetRequiredService<DeskDbContext>();
        var outbox = sp.GetRequiredService<OutboxWriter>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var emitted = 0;
        foreach (var ticketId in ticketIds)
        {
            // Through the tenant filter: a ticket of another company cannot show up here.
            var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId)
                .Select(t => new { t.Id, t.Number, t.Status, t.EscalatedAt, t.ResponseDueAt, t.ResolutionDueAt, t.FirstResponseAt })
                .FirstOrDefaultAsync(ct);
            if (ticket is null || ticket.EscalatedAt is not null || ticket.Status is not (TicketStatus.New or TicketStatus.Open))
            {
                continue;
            }

            if (ticket.ResponseDueAt is { } responseDue && responseDue < now && ticket.FirstResponseAt is null
                && await TryMarkAsync(db, ticket.Id, tenantId, SlaBreach.ResponseKind, responseDue, now, ct))
            {
                outbox.Add(EventTypes.TicketSlaBreached, tenantId,
                    new TicketSlaBreachedPayload(ticket.Id, ticket.Number, SlaBreach.ResponseKind, responseDue), now);
                emitted++;
            }

            if (ticket.ResolutionDueAt is { } resolutionDue && resolutionDue < now
                && await TryMarkAsync(db, ticket.Id, tenantId, SlaBreach.ResolutionKind, resolutionDue, now, ct))
            {
                outbox.Add(EventTypes.TicketSlaBreached, tenantId,
                    new TicketSlaBreachedPayload(ticket.Id, ticket.Number, SlaBreach.ResolutionKind, resolutionDue), now);
                emitted++;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return emitted;
    }

    /// <summary>True when this call created the marker. A concurrent inserter makes the other wait for its commit, then insert nothing.</summary>
    private static async Task<bool> TryMarkAsync(
        DeskDbContext db, Guid ticketId, Guid tenantId, string kind, DateTimeOffset dueAt, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SlaBreaches" ("TicketId", "Kind", "TenantId", "DueAt", "DetectedAt")
            VALUES ({ticketId}, {kind}, {tenantId}, {dueAt}, {now})
            ON CONFLICT DO NOTHING
            """, ct) == 1;
}

public static class SlaWatcherExtensions
{
    /// <summary>The <c>SlaWatcher</c> background service (Worker).</summary>
    public static IServiceCollection AddDeskSlaWatcher(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<SlaWatcherOptions>(configuration.GetSection("Sla"));
        services.AddSingleton<SlaWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<SlaWatcher>());
        return services;
    }
}
