using System.Globalization;
using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>Company palette (US-19). The tenant always comes from the verified token, never from the request.</summary>
public static class ThemeEndpoints
{
    public static IEndpointRouteBuilder MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/theme").WithTags("theme");

        group.MapGet("/", Get).WithName("getTheme").RequireAuthorization();
        group.MapPut("/", Update).WithName("updateTheme").RequireAuthorization(Policies.TenantAdmin)
            .AddEndpointFilter<ValidationFilter<ThemeRequest>>();
        group.MapDelete("/", Reset).WithName("resetTheme").RequireAuthorization(Policies.TenantAdmin);
        return app;
    }

    private static ThemeDto Defaults() => new(ThemeDefaults.ColorPrimary, ThemeDefaults.ColorSurface, ThemeDefaults.ColorAccent);

    private static ThemeDto Of(Tenant t) => new(t.ColorPrimary, t.ColorSurface, t.ColorAccent);

    private static Task<Tenant?> CurrentAsync(DeskDbContext db, ITenantContext tenant, CancellationToken ct) =>
        tenant.TenantId is { } id ? db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) : Task.FromResult<Tenant?>(null);

    private static async Task<IResult> Get(DeskDbContext db, ITenantContext tenant, CancellationToken ct) =>
        // The SuperAdmin has no company: he gets the platform defaults.
        await CurrentAsync(db, tenant, ct) is { } current ? Results.Ok(Of(current)) : Results.Ok(Defaults());

    private static async Task<IResult> Update(ThemeRequest request, DeskDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        if (await CurrentAsync(db, tenant, ct) is not { } current)
        {
            return Problems.NotFound();
        }

        current.ColorPrimary = request.ColorPrimary!.ToUpperInvariant();
        current.ColorSurface = request.ColorSurface!.ToUpperInvariant();
        current.ColorAccent = request.ColorAccent!.ToUpperInvariant();
        await db.SaveChangesAsync(ct);

        var warnings = new List<string>();
        var ratio = ColorContrast.Ratio(current.ColorAccent, current.ColorSurface);
        if (ratio < ColorContrast.MinimumAccentRatio)
        {
            // Truncated, never rounded up: 2,96 must not read "3,0" next to a "below 3:1" warning.
            var shown = (Math.Floor(ratio * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
            warnings.Add($"Le contraste entre la couleur d'accent et la surface est de {shown}:1, en dessous du minimum recommandé de 3:1 : "
                + "les boutons et éléments accentués risquent d'être difficiles à distinguer.");
        }

        return Results.Ok(new ThemeUpdateResultDto(Of(current), warnings));
    }

    private static async Task<IResult> Reset(DeskDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        if (await CurrentAsync(db, tenant, ct) is not { } current)
        {
            return Problems.NotFound();
        }

        current.ColorPrimary = ThemeDefaults.ColorPrimary;
        current.ColorSurface = ThemeDefaults.ColorSurface;
        current.ColorAccent = ThemeDefaults.ColorAccent;
        await db.SaveChangesAsync(ct);
        return Results.Ok(Of(current));
    }
}
