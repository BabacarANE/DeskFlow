namespace Desk.Core.Events;

/// <summary>Routing keys of the domain events (docs/api/events.md, section 2).</summary>
public static class EventTypes
{
    public const string TicketCreated = "ticket.created";
    public const string TicketAssigned = "ticket.assigned";
    public const string TicketCommented = "ticket.commented";
    public const string TicketResolved = "ticket.resolved";
    public const string TicketSlaBreached = "ticket.sla.breached";
}
