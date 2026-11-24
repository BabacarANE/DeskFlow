using Desk.Core.Events;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>
/// <c>ticket.assigned</c>: e-mail to the new assignee (US-13). Everything is read inside the tenant of the message, so a
/// ticket or a user of another company is "not found" (straight to the DLQ). Nobody is notified of his own action.
/// </summary>
[Consumer("assignment-notifier", ConsumerHost.Worker, EventTypes.TicketAssigned)]
public sealed class AssignmentNotifierConsumer(
    DeskDbContext db, IEmailSender email, IConfiguration configuration, ILogger<AssignmentNotifierConsumer> logger) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketAssignedPayload>();
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == payload.TicketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {payload.TicketId} not found in tenant {message.TenantId}.");
        var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == payload.AssigneeId, ct)
            ?? throw new UnprocessableMessageException($"Assignee {payload.AssigneeId} not found in tenant {message.TenantId}.");

        if (assignee.Id == payload.ActorId)
        {
            logger.LogInformation("Ticket {TicketId}: self-assignment, no e-mail", ticket.Id);
            return;
        }

        if (!assignee.IsActive)
        {
            logger.LogInformation("Ticket {TicketId}: assignee is inactive, no e-mail", ticket.Id);
            return;
        }

        var content = EmailTemplates.TicketAssigned(assignee.DisplayName, ticket.Number, ticket.Title, TicketLinks.Url(configuration, ticket.Id));
        await email.SendAsync(assignee.Email, content.Subject, content.Text, content.Html, ct);
    }
}
