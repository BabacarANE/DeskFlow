using System.Security.Claims;
using Desk.Core.Domain;
using Desk.Core.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Desk.Infrastructure.Persistence;

/// <summary>Scoped tenant context: reads <c>tid</c> and <c>role</c> from the authenticated principal.</summary>
public sealed class TenantContext(IHttpContextAccessor? accessor = null) : ITenantContext
{
    private int _unscoped;
    private ClaimsPrincipal? Principal => accessor?.HttpContext?.User;

    public Guid? TenantId =>
        Guid.TryParse(Principal?.FindFirstValue("tid"), out var id) ? id : null;

    public bool IsSuperAdmin => Principal?.FindFirstValue("role") == nameof(Role.SuperAdmin);

    public bool IsUnscoped => Volatile.Read(ref _unscoped) > 0;

    public IDisposable BeginUnscoped()
    {
        Interlocked.Increment(ref _unscoped);
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._unscoped);
            }
        }
    }
}
