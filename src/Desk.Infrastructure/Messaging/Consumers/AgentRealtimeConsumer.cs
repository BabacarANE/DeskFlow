using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>Client method names of the hub (docs/api/events.md, section 5).</summary>
public static class RealtimeMethods
{
    public const string TicketCreated = "ticketCreated";
    public const string TicketAssigned = "ticketAssigned";
    public const string TicketReplied = "ticketReplied";
}

/// <summary>SignalR group names. The tenant and the user id always come from the JWT or from the message, never from a client.</summary>
public static class RealtimeGroups
{
    public static string User(Guid userId) => $"user:{userId:D}";

    public static string Agents(Guid tenantId) => $"agents:{tenantId:D}";
}

/// <summary>Payload of <c>ticketCreated</c>: no description, no comment, no e-mail address.</summary>
public sealed record TicketCreatedNotification(Guid TicketId, int Number, string Title, string Priority, DateTimeOffset At);

/// <summary>Payload of <c>ticketAssigned</c> and <c>ticketReplied</c>.</summary>
public sealed record TicketNotification(Guid TicketId, int Number, string Title, DateTimeOffset At);

/// <summary>
/// What the consumer needs from the real-time channel. The API implements it with SignalR; tests use a recorder, so the
/// consumer is tested without a hub.
/// </summary>
public interface IRealtimeNotifier
{
    Task SendToGroupAsync<T>(string group, string method, T payload, CancellationToken ct);
}

/// <summary>
/// <c>ticket.created</c>, <c>ticket.assigned</c>, <c>ticket.commented</c>: real-time notifications of the agents (US-13).
/// Runs in the API, which hosts the hub. Every lookup happens inside the tenant of the message; the payload sent to clients
/// holds the ticket title but never its description nor a comment.
/// </summary>
[Consumer("agent-realtime", ConsumerHost.Api, EventTypes.TicketCreated, EventTypes.TicketAssigned, EventTypes.TicketCommented)]
public sealed class AgentRealtimeConsumer(
    DeskDbContext db, IRealtimeNotifier notifier, ILogger<AgentRealtimeConsumer> logger) : DeskConsumer
{
    public override Task HandleAsync(MessageEnvelope message, CancellationToken ct) => message.Type switch
    {
        EventTypes.TicketCreated => CreatedAsync(message, ct),
        EventTypes.TicketAssigned => AssignedAsync(message, ct),
        EventTypes.TicketCommented => CommentedAsync(message, ct),
        _ => throw new UnprocessableMessageException($"Unexpected message type {message.Type}."),
    };

    private async Task CreatedAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketCreatedPayload>();
        var ticket = await FindTicketAsync(payload.TicketId, message.TenantId, ct);
        await notifier.SendToGroupAsync(RealtimeGroups.Agents(message.TenantId), RealtimeMethods.TicketCreated,
            new TicketCreatedNotification(ticket.Id, ticket.Number, ticket.Title, ticket.Priority.ToString(), message.OccurredAt), ct);
    }

    private async Task AssignedAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketAssignedPayload>();
        var ticket = await FindTicketAsync(payload.TicketId, message.TenantId, ct);
        // The recipient must belong to the tenant of the message: a user id of another company is "not found".
        var assignee = await db.Users.AsNoTracking().Where(u => u.Id == payload.AssigneeId).Select(u => new { u.Id, u.IsActive }).FirstOrDefaultAsync(ct)
            ?? throw new UnprocessableMessageException($"Assignee {payload.AssigneeId} not found in tenant {message.TenantId}.");
        if (!assignee.IsActive)
        {
            logger.LogInformation("Ticket {TicketId}: assignee is inactive, no real-time notification", ticket.Id);
            return;
        }

        await notifier.SendToGroupAsync(RealtimeGroups.User(assignee.Id), RealtimeMethods.TicketAssigned,
            new TicketNotification(ticket.Id, ticket.Number, ticket.Title, message.OccurredAt), ct);
    }

    private async Task CommentedAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketCommentedPayload>();
        var ticket = await FindTicketAsync(payload.TicketId, message.TenantId, ct);
        var comment = await db.TicketComments.AsNoTracking()
            .Where(c => c.Id == payload.CommentId && c.TicketId == ticket.Id)
            .Select(c => new { c.AuthorId, c.IsInternal })
            .FirstOrDefaultAsync(ct)
            ?? throw new UnprocessableMessageException($"Comment {payload.CommentId} not found on ticket {payload.TicketId}.");

        // Only the assignee is told, and only when the requester answered (docs/api/events.md, section 3).
        if (comment.IsInternal || comment.AuthorId != ticket.RequesterId || ticket.AssigneeId is not { } assigneeId)
        {
            logger.LogInformation("Ticket {TicketId}: no real-time notification for comment {CommentId}", ticket.Id, payload.CommentId);
            return;
        }

        await notifier.SendToGroupAsync(RealtimeGroups.User(assigneeId), RealtimeMethods.TicketReplied,
            new TicketNotification(ticket.Id, ticket.Number, ticket.Title, message.OccurredAt), ct);
    }

    private async Task<Ticket> FindTicketAsync(Guid ticketId, Guid tenantId, CancellationToken ct) =>
        await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {ticketId} not found in tenant {tenantId}.");
}
