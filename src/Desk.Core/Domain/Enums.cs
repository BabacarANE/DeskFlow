namespace Desk.Core.Domain;

public enum Role
{
    SuperAdmin,
    TenantAdmin,
    Agent,
    Requester,
}

public enum Priority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Expired,
}

public enum TicketStatus
{
    New,
    Open,
    Pending,
    Resolved,
    Closed,
}

public enum TicketEventType
{
    Created,
    StatusChanged,
    PriorityChanged,
    AssigneeChanged,
    CategoryChanged,
}
