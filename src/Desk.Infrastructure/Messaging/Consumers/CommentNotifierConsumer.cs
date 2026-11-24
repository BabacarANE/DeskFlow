using Desk.Core.Events;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>
/// <c>ticket.commented</c>: e-mail to the other party of a public comment (US-13, US-14): the requester when somebody else
/// wrote, the assignee when the requester wrote. Never to the author, never for an internal note, nothing without assignee.
/// The comment body is never read: it does not go into the e-mail.
/// </summary>
[Consumer("comment-notifier", ConsumerHost.Worker, EventTypes.TicketCommented)]
public sealed class CommentNotifierConsumer(
    DeskDbContext db, IEmailSender email, IConfiguration configuration, ILogger<CommentNotifierConsumer> logger) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketCommentedPayload>();
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).Include(t => t.Assignee)
            .FirstOrDefaultAsync(t => t.Id == payload.TicketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {payload.TicketId} not found in tenant {message.TenantId}.");
        var comment = await db.TicketComments.AsNoTracking()
            .Where(c => c.Id == payload.CommentId && c.TicketId == ticket.Id)
            .Select(c => new { c.AuthorId, c.IsInternal })
            .FirstOrDefaultAsync(ct)
            ?? throw new UnprocessableMessageException($"Comment {payload.CommentId} not found on ticket {payload.TicketId}.");

        if (comment.IsInternal)
        {
            logger.LogWarning("Comment {CommentId} is an internal note: no e-mail", payload.CommentId);
            return;
        }

        var authorIsRequester = comment.AuthorId == ticket.RequesterId;
        var recipient = authorIsRequester ? ticket.Assignee : ticket.Requester;
        if (recipient is null || recipient.Id == comment.AuthorId || !recipient.IsActive)
        {
            logger.LogInformation("Ticket {TicketId}: no one to notify for comment {CommentId}", ticket.Id, payload.CommentId);
            return;
        }

        var url = TicketLinks.Url(configuration, ticket.Id);
        var content = authorIsRequester
            ? EmailTemplates.ReplyToAssignee(recipient.DisplayName, ticket.Number, ticket.Title, url)
            : EmailTemplates.ReplyToRequester(recipient.DisplayName, ticket.Number, ticket.Title, url);
        await email.SendAsync(recipient.Email, content.Subject, content.Text, content.Html, ct);
    }
}
