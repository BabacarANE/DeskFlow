using Desk.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Desk.Infrastructure.Persistence;

/// <summary>
/// Fills <c>TenantId</c> on creation and refuses any write that targets another tenant.
/// Without a tenant (SuperAdmin, identity flows, seeders) the write must be explicit
/// (<see cref="ITenantContext.IsSuperAdmin"/> or <see cref="ITenantContext.BeginUnscoped"/>) and carry its TenantId.
/// </summary>
public sealed class TenantSaveChangesInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    public const string TenantIdProperty = "TenantId";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enforce(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Enforce(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var property = entry.Metadata.FindProperty(TenantIdProperty);
            if (property is null)
            {
                continue;
            }

            var prop = entry.Property(TenantIdProperty);
            var current = prop.CurrentValue as Guid?;
            if (current == Guid.Empty)
            {
                current = null;
            }

            if (tenant.TenantId is { } tenantId)
            {
                Enforce(entry, prop, current, tenantId);
            }
            else
            {
                EnforceWithoutTenant(entry, current, property.IsNullable);
            }
        }
    }

    private static void Enforce(EntityEntry entry, PropertyEntry prop, Guid? current, Guid tenantId)
    {
        if (entry.State == EntityState.Added)
        {
            if (current is null)
            {
                prop.CurrentValue = tenantId;
            }
            else if (current != tenantId)
            {
                throw new TenantViolationException($"Write to another tenant refused ({entry.Metadata.ClrType.Name}).");
            }

            return;
        }

        var original = prop.OriginalValue as Guid?;
        if (current != tenantId || (original is not null && original != tenantId))
        {
            throw new TenantViolationException($"Write to another tenant refused ({entry.Metadata.ClrType.Name}).");
        }
    }

    private void EnforceWithoutTenant(EntityEntry entry, Guid? current, bool tenantIdIsNullable)
    {
        if (!tenant.IsSuperAdmin && !tenant.IsUnscoped)
        {
            throw new TenantViolationException(
                $"Write without tenant context refused ({entry.Metadata.ClrType.Name}).");
        }

        if (entry.State == EntityState.Added && current is null && !tenantIdIsNullable)
        {
            throw new TenantViolationException(
                $"TenantId must be explicit when there is no tenant context ({entry.Metadata.ClrType.Name}).");
        }
    }
}
