using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Security;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Auth;

public sealed record ExternalLoginResult(User? User, string? Error)
{
    public static ExternalLoginResult Fail(string error) => new(null, error);
}

/// <summary>
/// Finds or creates the user behind a provider identity. A first connection requires an invitation
/// whose e-mail equals the verified e-mail of the provider.
/// </summary>
public sealed class ExternalLoginService(DeskDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<ExternalLoginResult> CompleteAsync(
        ExternalIdentity identity, string? invitationToken, CancellationToken ct = default)
    {
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
        {
            return ExternalLoginResult.Fail(LoginErrors.EmailNotVerified);
        }

        var email = InvitationRules.NormalizeEmail(identity.Email);

        // Identity flows run before any tenant is known: explicit, narrow bypass of the tenant filter.
        using var _ = tenant.BeginUnscoped();

        var linked = await db.Users.IgnoreQueryFilters().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => db.ExternalLogins.Any(l =>
                l.UserId == u.Id && l.Provider == identity.Provider && l.ProviderKey == identity.ProviderKey), ct);
        if (linked is not null)
        {
            return Allow(linked);
        }

        // Same verified e-mail as an existing account (e.g. bootstrapped SuperAdmin, second provider): link it.
        var existing = await db.Users.IgnoreQueryFilters().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existing is not null)
        {
            var gate = Allow(existing);
            if (gate.Error is not null)
            {
                return gate;
            }

            db.ExternalLogins.Add(new ExternalLogin { UserId = existing.Id, Provider = identity.Provider, ProviderKey = identity.ProviderKey });
            return await SaveAsync(existing, ct);
        }

        if (string.IsNullOrWhiteSpace(invitationToken))
        {
            return ExternalLoginResult.Fail(LoginErrors.InvitationRequired);
        }

        var hash = TokenHasher.Hash(invitationToken);
        var invitation = await db.Invitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null)
        {
            return ExternalLoginResult.Fail(LoginErrors.InvitationInvalid);
        }

        var now = clock.GetUtcNow();
        switch (InvitationRules.Check(invitation, email, now))
        {
            case InvitationCheck.AlreadyUsed:
            case InvitationCheck.Expired:
                return ExternalLoginResult.Fail(LoginErrors.InvitationInvalid);
            case InvitationCheck.EmailMismatch:
                return ExternalLoginResult.Fail(LoginErrors.EmailMismatch);
        }

        var invitedTenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == invitation.TenantId, ct);
        if (invitedTenant is null)
        {
            return ExternalLoginResult.Fail(LoginErrors.InvitationInvalid);
        }

        if (!invitedTenant.IsActive)
        {
            return ExternalLoginResult.Fail(LoginErrors.TenantSuspended);
        }

        var user = new User
        {
            TenantId = invitation.TenantId,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? email.Split('@')[0] : identity.DisplayName.Trim(),
            AvatarUrl = identity.AvatarUrl,
            Role = invitation.Role,
            CreatedAt = now,
            Tenant = invitedTenant,
        };
        invitation.AcceptedAt = now;
        db.Users.Add(user);
        db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = identity.Provider, ProviderKey = identity.ProviderKey });
        return await SaveAsync(user, ct, uniqueViolationError: LoginErrors.InvitationInvalid);
    }

    private static ExternalLoginResult Allow(User user)
    {
        if (!user.IsActive)
        {
            return ExternalLoginResult.Fail(LoginErrors.AccountDisabled);
        }

        return user.Tenant is { IsActive: false }
            ? ExternalLoginResult.Fail(LoginErrors.TenantSuspended)
            : new ExternalLoginResult(user, null);
    }

    private async Task<ExternalLoginResult> SaveAsync(User user, CancellationToken ct, string uniqueViolationError = LoginErrors.InvitationInvalid)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new ExternalLoginResult(user, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent use of the same invitation / identity: the unique indexes keep it single-use.
            return ExternalLoginResult.Fail(uniqueViolationError);
        }
    }
}
