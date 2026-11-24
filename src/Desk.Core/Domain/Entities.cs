namespace Desk.Core.Domain;

public static class ThemeDefaults
{
    public const string ColorPrimary = "#7CC4EE";
    public const string ColorSurface = "#F4F7FA";
    public const string ColorAccent = "#D63A14";
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ColorPrimary { get; set; } = ThemeDefaults.ColorPrimary;
    public string ColorSurface { get; set; } = ThemeDefaults.ColorSurface;
    public string ColorAccent { get; set; } = ThemeDefaults.ColorAccent;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for the SuperAdmin only.</summary>
    public Guid? TenantId { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public Role Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public Tenant? Tenant { get; set; }
}

public class ExternalLogin
{
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderKey { get; set; } = "";
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public Role Role { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

public class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
}

public class SlaPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Priority Priority { get; set; }
    public int FirstResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }

    /// <summary>Default SLA table of the specification (section 6).</summary>
    public static IReadOnlyList<SlaPolicy> CreateDefaults(Guid tenantId) =>
    [
        new() { TenantId = tenantId, Priority = Priority.Urgent, FirstResponseMinutes = 30, ResolutionMinutes = 240 },
        new() { TenantId = tenantId, Priority = Priority.High, FirstResponseMinutes = 120, ResolutionMinutes = 480 },
        new() { TenantId = tenantId, Priority = Priority.Normal, FirstResponseMinutes = 480, ResolutionMinutes = 1440 },
        new() { TenantId = tenantId, Priority = Priority.Low, FirstResponseMinutes = 1440, ResolutionMinutes = 4320 },
    ];
}

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>Set when the row can never be published (invalid envelope): the dispatcher skips it for good.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureReason { get; set; }
}

public class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}
