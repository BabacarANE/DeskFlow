using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Messaging.Consumers;

/// <summary>
/// <c>ticket.resolved</c>: proposes a knowledge base draft (US-16). One draft per ticket: a second delivery, a replay, or a
/// ticket reopened then resolved again finds the existing article and does nothing; the unique index on
/// <c>(TenantId, SourceTicketId)</c> backs this up if two deliveries race (the loser fails, is retried, then sees the article).
/// Content follows docs/api/events.md: title of the ticket, description as symptom, last public comment as solution.
/// </summary>
[Consumer("kb-draft", ConsumerHost.Worker, EventTypes.TicketResolved)]
public sealed class KbDraftConsumer(DeskDbContext db, TimeProvider clock, ILogger<KbDraftConsumer> logger) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketResolvedPayload>();
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == payload.TicketId, ct)
            ?? throw new UnprocessableMessageException($"Ticket {payload.TicketId} not found in tenant {message.TenantId}.");

        if (await db.KnowledgeArticles.AnyAsync(a => a.SourceTicketId == ticket.Id, ct))
        {
            logger.LogInformation("Ticket {TicketId} already has a knowledge draft: nothing to create", ticket.Id);
            return;
        }

        var solution = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id && !c.IsInternal)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Select(c => c.Body)
            .FirstOrDefaultAsync(ct);

        var now = clock.GetUtcNow();
        db.KnowledgeArticles.Add(new KnowledgeArticle
        {
            TenantId = ticket.TenantId,
            Title = ticket.Title,
            Symptom = ticket.Description,
            Cause = "",
            Solution = solution ?? "",
            Status = ArticleStatus.Draft,
            SourceTicketId = ticket.Id,
            AuthorId = ticket.AssigneeId ?? ticket.RequesterId,
            CreatedAt = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero),
        });
    }
}
