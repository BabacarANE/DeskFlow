using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>
/// <c>ticket.sla.breached</c>: raises the priority by one step, once (US-15). <c>EscalatedAt</c> is the guard: a second
/// event for the same ticket (the other SLA kind, a replay) finds it set and does nothing. A ticket that left New/Open in
/// the meantime (pending, resolved, closed) is not escalated: its SLA clock no longer runs. The ticket row is saved BEFORE the
/// e-mails go out: if an agent edited the ticket concurrently, the optimistic check (xmin) fails here, the delivery is retried
/// on fresh data, and no e-mail has been sent for the failed attempt.
/// </summary>
[Consumer("escalation", ConsumerHost.Worker, EventTypes.TicketSlaBreached)]
public sealed class EscalationConsumer(
    DeskDbContext db, IEmailSender email, IConfiguration configuration, TimeProvider clock, ILogger<EscalationConsumer> logger) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketSlaBreachedPayload>();
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == payload.TicketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {payload.TicketId} not found in tenant {message.TenantId}.");

        if (ticket.EscalatedAt is not null)
        {
            logger.LogInformation("Ticket {TicketId} is already escalated: nothing to do", ticket.Id);
            return;
        }

        if (ticket.Status is not (TicketStatus.New or TicketStatus.Open))
        {
            logger.LogInformation("Ticket {TicketId} is {Status}: its SLA clock is not running, no escalation", ticket.Id, ticket.Status);
            return;
        }

        var from = ticket.Priority;
        var to = from == Priority.Urgent ? Priority.Urgent : from + 1;
        var now = clock.GetUtcNow();
        now = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);

        var policy = await db.SlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Priority == to, ct)
            ?? SlaPolicy.CreateDefaults(Guid.Empty).First(p => p.Priority == to);
        ticket.ChangePriority(to, policy.FirstResponseMinutes, policy.ResolutionMinutes);
        ticket.EscalatedAt = now;
        ticket.UpdatedAt = now;
        if (to != from)
        {
            db.TicketEvents.Add(new TicketEvent
            {
                TicketId = ticket.Id,
                ActorId = null,
                Type = TicketEventType.PriorityChanged,
                OldValue = from.ToString(),
                NewValue = to.ToString(),
                At = now,
            });
        }

        await db.SaveChangesAsync(ct);

        var admins = await db.Users.AsNoTracking()
            .Where(u => u.Role == Role.TenantAdmin && u.IsActive)
            .OrderBy(u => u.Email)
            .ToListAsync(ct);
        if (admins.Count == 0)
        {
            logger.LogWarning("Ticket {TicketId} escalated but the tenant has no active administrator to notify", ticket.Id);
        }

        var url = TicketLinks.Url(configuration, ticket.Id);
        foreach (var admin in admins)
        {
            var content = EmailTemplates.TicketEscalated(admin.DisplayName, ticket.Number, ticket.Title, payload.Kind, from, to, url);
            await email.SendAsync(admin.Email, content.Subject, content.Text, content.Html, ct);
        }

        logger.LogInformation("Ticket {TicketId} escalated from {From} to {To} ({Kind} SLA missed)", ticket.Id, from, to, payload.Kind);
    }
}
