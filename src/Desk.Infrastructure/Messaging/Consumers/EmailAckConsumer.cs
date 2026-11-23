using Desk.Core.Events;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>
/// <c>ticket.created</c>: acknowledgement e-mail to the requester (US-12). The payload only holds identifiers: the address
/// and the title come from the database, read inside the tenant of the message (a ticket of another company is "not found").
/// </summary>
[Consumer("email-ack", ConsumerHost.Worker, EventTypes.TicketCreated)]
public sealed class EmailAckConsumer(
    DeskDbContext db, IEmailSender email, IConfiguration configuration, ILogger<EmailAckConsumer> logger) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketCreatedPayload>();
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).FirstOrDefaultAsync(t => t.Id == payload.TicketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {payload.TicketId} not found in tenant {message.TenantId}.");
        if (ticket.Requester is not { IsActive: true } requester)
        {
            logger.LogInformation("Ticket {TicketId}: requester missing or inactive, no acknowledgement sent", ticket.Id);
            return;
        }

        var baseUrl = (configuration["App:PublicBaseUrl"] ?? "http://localhost:8080").TrimEnd('/');
        var content = EmailTemplates.TicketAcknowledgement(requester.DisplayName, ticket.Number, ticket.Title, $"{baseUrl}/tickets/{ticket.Id}");
        await email.SendAsync(requester.Email, content.Subject, content.Text, content.Html, ct);
    }
}
