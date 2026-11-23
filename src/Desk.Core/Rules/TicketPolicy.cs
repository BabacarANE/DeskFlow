using Desk.Core.Domain;

namespace Desk.Core.Rules;

/// <summary>Role rules for tickets (SPEC section 4). The SuperAdmin never reaches ticket content.</summary>
public static class TicketPolicy
{
    public static bool IsStaff(Role role) => role is Role.Agent or Role.TenantAdmin;

    public static bool CanCreate(Role role) => role is Role.Requester or Role.Agent or Role.TenantAdmin;

    public static bool CanView(Role role, Guid actorId, Guid requesterId) =>
        IsStaff(role) || (role == Role.Requester && actorId == requesterId);

    public static bool CanModify(Role role) => IsStaff(role);

    public static bool CanPostInternal(Role role) => IsStaff(role);

    public static bool CanSeeComment(Role role, bool isInternal) => IsStaff(role) || (role == Role.Requester && !isInternal);

    public static bool IsAssignable(Role role, bool isActive) => isActive && IsStaff(role);

    /// <summary>The first public answer of someone other than the requester counts as the first response.</summary>
    public static bool CountsAsFirstResponse(Role authorRole, Guid authorId, Guid requesterId, bool isInternal) =>
        IsStaff(authorRole) && !isInternal && authorId != requesterId;

    /// <summary>A requester answering a ticket that waits for him reopens it.</summary>
    public static bool ReopensPending(Role authorRole, TicketStatus status) =>
        authorRole == Role.Requester && status == TicketStatus.Pending;
}
