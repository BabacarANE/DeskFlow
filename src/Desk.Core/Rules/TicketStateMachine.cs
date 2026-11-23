using Desk.Core.Domain;

namespace Desk.Core.Rules;

/// <summary>Status transitions of the specification (section 6); anything else is refused (409).</summary>
public static class TicketStateMachine
{
    private static readonly IReadOnlyDictionary<TicketStatus, TicketStatus[]> Allowed = new Dictionary<TicketStatus, TicketStatus[]>
    {
        [TicketStatus.New] = [TicketStatus.Open],
        [TicketStatus.Open] = [TicketStatus.Pending, TicketStatus.Resolved],
        [TicketStatus.Pending] = [TicketStatus.Open],
        [TicketStatus.Resolved] = [TicketStatus.Open, TicketStatus.Closed],
        [TicketStatus.Closed] = [],
    };

    public static bool CanTransition(TicketStatus from, TicketStatus to) => Allowed[from].Contains(to);

    public static IReadOnlyList<TicketStatus> NextStatuses(TicketStatus from) => Allowed[from];

    /// <summary>The SLA clock runs only while the ticket is New or Open.</summary>
    public static bool SlaRuns(TicketStatus status) => status is TicketStatus.New or TicketStatus.Open;
}
