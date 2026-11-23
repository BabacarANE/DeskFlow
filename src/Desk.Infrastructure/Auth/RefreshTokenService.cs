using Desk.Core.Domain;
using Desk.Core.Security;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Auth;

public sealed record IssuedRefreshToken(string RawToken, DateTimeOffset ExpiresAt);

public sealed record RefreshOutcome(User? User, IssuedRefreshToken? Token)
{
    public bool Succeeded => User is not null && Token is not null;
}

/// <summary>
/// Refresh tokens: 7 days, stored hashed, rotated at each use. Presenting an already used token
/// is treated as theft and revokes the whole family.
/// </summary>
public sealed class RefreshTokenService(DeskDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task<IssuedRefreshToken> IssueAsync(User user, CancellationToken ct = default) =>
        await AddAsync(user.Id, Guid.NewGuid(), Guid.NewGuid(), ct);

    public async Task<RefreshOutcome> RotateAsync(string? rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return new RefreshOutcome(null, null);
        }

        var now = clock.GetUtcNow();
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == TokenHasher.Hash(rawToken), ct);
        if (stored is null)
        {
            return new RefreshOutcome(null, null);
        }

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return new RefreshOutcome(null, null);
        }

        if (stored.ExpiresAt <= now)
        {
            return new RefreshOutcome(null, null);
        }

        // Identity lookup happens before any tenant is known: explicit bypass of the tenant filter.
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive || (user.Tenant is not null && !user.Tenant.IsActive))
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return new RefreshOutcome(null, null);
        }

        var newId = Guid.NewGuid();
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, newId), ct);
        if (claimed == 0)
        {
            // Lost a race with another use of the same token: same as reuse.
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return new RefreshOutcome(null, null);
        }

        return new RefreshOutcome(user, await AddAsync(user.Id, stored.FamilyId, newId, ct));
    }

    /// <summary>Revokes the family of the presented token, only if it belongs to <paramref name="userId"/>.</summary>
    public async Task RevokeFamilyOfAsync(string rawToken, Guid userId, CancellationToken ct = default)
    {
        var hash = TokenHasher.Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
        if (token is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, ct);
        }
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<IssuedRefreshToken> AddAsync(Guid userId, Guid familyId, Guid id, CancellationToken ct)
    {
        var raw = TokenHasher.NewToken();
        var now = clock.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = id,
            UserId = userId,
            FamilyId = familyId,
            TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return new IssuedRefreshToken(raw, now + Lifetime);
    }
}
