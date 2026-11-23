using System.Security.Claims;
using Desk.Api.Contracts;
using Desk.Core.Domain;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Auth;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Http;

public static class Policies
{
    public const string SuperAdmin = "SuperAdmin";
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Agent or TenantAdmin of a tenant.</summary>
    public const string Agent = "Agent";

    /// <summary>Any user of a tenant (Requester, Agent, TenantAdmin): never the SuperAdmin.</summary>
    public const string TenantMember = "TenantMember";
}

/// <summary>RFC 7807 <c>type</c> URIs of the business conflicts (the contract documents the trailing slug).</summary>
public static class ProblemTypes
{
    public const string Base = "https://deskflow.dev/problems/";
    public const string InvalidTransition = Base + "invalid-transition";
    public const string ConcurrencyConflict = Base + "concurrency-conflict";
    public const string TicketClosed = Base + "ticket-closed";
    public const string CategoryInUse = Base + "category-in-use";
    public const string DuplicateName = Base + "duplicate-name";
}

public static class Problems
{
    public static IResult NotFound(string? detail = null) => Results.Problem(statusCode: 404, title: "Ressource introuvable", detail: detail);

    public static IResult Conflict(string detail, string? type = null) =>
        Results.Problem(statusCode: 409, title: "Conflit", detail: detail, type: type);

    public static IResult Forbidden(string detail) => Results.Problem(statusCode: 403, title: "Accès refusé", detail: detail);

    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, statusCode: 422, title: "Données invalides");

    public static IResult Unauthorized(string detail) => Results.Problem(statusCode: 401, title: "Non authentifié", detail: detail);
}

public static class PagingExtensions
{
    public const int MaxPageSize = 100;

    public static IResult? Validate(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page doit être supérieur ou égal à 1."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize doit être compris entre 1 et {MaxPageSize}."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors, statusCode: 422, title: "Paramètres invalides");
    }

    public static async Task<PagedResult<TOut>> ToPageAsync<TIn, TOut>(
        this IQueryable<TIn> ordered, int page, int pageSize, Func<TIn, TOut> map, CancellationToken ct)
    {
        var total = await ordered.CountAsync(ct);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TOut>(items.Select(map).ToList(), total, page, pageSize);
    }
}

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : Guid.Empty;

    public static Role? RoleOf(this ClaimsPrincipal user) =>
        Enum.TryParse<Role>(user.FindFirstValue("role"), out var role) ? role : null;
}

/// <summary>Runs the FluentValidation validator of the T argument and answers 422 ProblemDetails.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return Results.Problem(statusCode: 400, title: "Corps de requête manquant");
        }

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(e => char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return Results.ValidationProblem(errors, statusCode: 422, title: "Données invalides");
    }
}

/// <summary>A write that crosses tenants is a bug or an attack: reveal nothing (404) and log it.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        if (exception is not TenantViolationException)
        {
            return false;
        }

        logger.LogWarning(exception, "Tenant violation refused");
        await Problems.NotFound().ExecuteAsync(httpContext);
        return true;
    }
}

public static class RefreshCookie
{
    public const string Name = "deskflow_refresh";
    public const string Path = "/api/v1/auth";

    public static void Set(HttpContext http, IssuedRefreshToken token) =>
        http.Response.Cookies.Append(Name, token.RawToken, Options(token.ExpiresAt));

    public static void Delete(HttpContext http) => http.Response.Cookies.Delete(Name, Options(null));

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = Path,
        Expires = expires,
    };
}
