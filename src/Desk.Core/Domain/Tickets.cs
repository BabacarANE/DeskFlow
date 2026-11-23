using Desk.Core.Rules;

namespace Desk.Core.Domain;

/// <summary>Per-tenant ticket sequence; incremented under a row lock in the transaction that inserts the ticket.</summary>
public class TicketCounter
{
    public Guid TenantId { get; set; }
    public int LastNumber { get; set; }
}

public class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.New;
    public Priority Priority { get; set; } = Priority.Normal;
    public Guid? CategoryId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FirstResponseAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ResponseDueAt { get; set; }
    public DateTimeOffset? ResolutionDueAt { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }

    /// <summary>Set while the ticket is Pending: the SLA clock is suspended since then.</summary>
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>Total time already spent in Pending; deadlines always include it.</summary>
    public TimeSpan SlaPausedTotal { get; set; }

    /// <summary>PostgreSQL <c>xmin</c> system column: optimistic concurrency token, exposed as an opaque string.</summary>
    public uint RowVersion { get; set; }

    public Category? Category { get; set; }
    public User? Requester { get; set; }
    public User? Assignee { get; set; }

    /// <summary>Applies a status change with its side effects; false when the transition is not allowed.</summary>
    public bool ChangeStatus(TicketStatus to, DateTimeOffset now)
    {
        if (!TicketStateMachine.CanTransition(Status, to))
        {
            return false;
        }

        if (Status == TicketStatus.Pending && PendingSince is { } since)
        {
            var paused = SlaCalculator.PausedDuration(since, now);
            SlaPausedTotal += paused;
            ResponseDueAt = ResponseDueAt?.Add(paused);
            ResolutionDueAt = ResolutionDueAt?.Add(paused);
            PendingSince = null;
        }

        if (to == TicketStatus.Pending)
        {
            PendingSince = now;
        }

        // Choice: ResolvedAt is the date of the current resolution; a reopening clears it.
        if (to == TicketStatus.Resolved)
        {
            ResolvedAt = now;
        }
        else if (Status == TicketStatus.Resolved && to == TicketStatus.Open)
        {
            ResolvedAt = null;
        }

        Status = to;
        return true;
    }

    /// <summary>Recomputes both deadlines from the creation date with the new priority (paused time included).</summary>
    public void ChangePriority(Priority priority, int firstResponseMinutes, int resolutionMinutes)
    {
        Priority = priority;
        var due = SlaCalculator.Compute(CreatedAt, firstResponseMinutes, resolutionMinutes, SlaPausedTotal);
        ResponseDueAt = due.ResponseDueAt;
        ResolutionDueAt = due.ResolutionDueAt;
    }
}

public class TicketComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TicketId { get; set; }
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = "";
    public bool IsInternal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public User? Author { get; set; }
}

public class TicketEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Insertion order, used to sort events written at the same instant.</summary>
    public long Seq { get; set; }
    public Guid TenantId { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>Null for a system event.</summary>
    public Guid? ActorId { get; set; }
    public TicketEventType Type { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset At { get; set; }
    public User? Actor { get; set; }
}
