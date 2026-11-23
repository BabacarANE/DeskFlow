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
