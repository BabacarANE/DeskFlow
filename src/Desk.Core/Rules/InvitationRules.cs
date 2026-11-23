using Desk.Core.Domain;

namespace Desk.Core.Rules;

public enum InvitationCheck
{
    Ok,
    AlreadyUsed,
    Expired,
    EmailMismatch,
}

public static class InvitationRules
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static InvitationStatus StatusOf(Invitation invitation, DateTimeOffset now) =>
        invitation.AcceptedAt is not null ? InvitationStatus.Accepted
        : invitation.ExpiresAt <= now ? InvitationStatus.Expired
        : InvitationStatus.Pending;

    /// <summary>Single use, 7 days, and the verified provider e-mail must equal the invited e-mail.</summary>
    public static InvitationCheck Check(Invitation invitation, string verifiedEmail, DateTimeOffset now)
    {
        if (invitation.AcceptedAt is not null)
        {
            return InvitationCheck.AlreadyUsed;
        }

        if (invitation.ExpiresAt <= now)
        {
            return InvitationCheck.Expired;
        }

        return NormalizeEmail(invitation.Email) == NormalizeEmail(verifiedEmail)
            ? InvitationCheck.Ok
            : InvitationCheck.EmailMismatch;
    }
}
