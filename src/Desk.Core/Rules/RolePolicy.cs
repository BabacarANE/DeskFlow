using Desk.Core.Domain;

namespace Desk.Core.Rules;

/// <summary>Role rules of the specification (section 4) for identity management.</summary>
public static class RolePolicy
{
    public static bool CanInvite(Role actor, Role target) => actor switch
    {
        Role.SuperAdmin => target == Role.TenantAdmin,
        Role.TenantAdmin => target != Role.SuperAdmin,
        _ => false,
    };

    public static bool CanAssignRole(Role actor, Role newRole) => actor == Role.TenantAdmin && newRole != Role.SuperAdmin;

    /// <summary>A TenantAdmin manages other users of the tenant, never himself, never a SuperAdmin.</summary>
    public static bool CanManageUser(Role actor, Guid actorId, Guid targetId, Role targetRole) =>
        actor == Role.TenantAdmin && actorId != targetId && targetRole != Role.SuperAdmin;
}

public static class LoginErrors
{
    public const string InvitationRequired = "invitation_required";
    public const string InvitationInvalid = "invitation_invalid";
    public const string EmailMismatch = "email_mismatch";
    public const string EmailNotVerified = "email_not_verified";
    public const string AccountDisabled = "account_disabled";
    public const string TenantSuspended = "tenant_suspended";
    public const string ProviderError = "provider_error";
}
