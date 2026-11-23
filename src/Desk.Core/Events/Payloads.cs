using Desk.Core.Domain;

namespace Desk.Core.Events;

// Payloads carry identifiers only: never an e-mail address nor a comment body (docs/api/events.md, section 1).

public sealed record TicketCreatedPayload(Guid TicketId, int Number, string Title, Priority Priority, Guid RequesterId);

public sealed record TicketAssignedPayload(
    Guid TicketId, int Number, string Title, Guid AssigneeId, Guid? PreviousAssigneeId, Guid ActorId);

public sealed record TicketCommentedPayload(Guid TicketId, int Number, Guid CommentId, Guid AuthorId);

public sealed record TicketResolvedPayload(Guid TicketId, int Number, DateTimeOffset ResolvedAt);

/// <summary><paramref name="Kind"/> is <c>response</c> or <c>resolution</c>.</summary>
public sealed record TicketSlaBreachedPayload(Guid TicketId, int Number, string Kind, DateTimeOffset DueAt);
