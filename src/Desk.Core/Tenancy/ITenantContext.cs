namespace Desk.Core.Tenancy;

/// <summary>Ambient tenant of the current operation (claim <c>tid</c> of the JWT).</summary>
public interface ITenantContext
{
    /// <summary>Null for the SuperAdmin, anonymous requests and background work without tenant.</summary>
    Guid? TenantId { get; }

    bool IsSuperAdmin { get; }

    /// <summary>True inside <see cref="BeginUnscoped"/>: explicit bypass for identity flows and seeders.</summary>
    bool IsUnscoped { get; }

    /// <summary>Allows writes with an explicit TenantId while there is no tenant. Dispose to end.</summary>
    IDisposable BeginUnscoped();
}

public sealed class TenantViolationException(string message) : InvalidOperationException(message);
