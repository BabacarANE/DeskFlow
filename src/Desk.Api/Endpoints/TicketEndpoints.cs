using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Api.Tickets;
using Desk.Core.Domain;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>Tickets, comments, events and the assignee picker. Rules live in <see cref="TicketService"/>.</summary>
public static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var tickets = app.MapGroup("/api/v1/tickets").WithTags("tickets");

        tickets.MapGet("/", List).WithName("listTickets").RequireAuthorization(Policies.TenantMember);
        tickets.MapPost("/", Create).WithName("createTicket").RequireAuthorization(Policies.TenantMember)
            .AddEndpointFilter<ValidationFilter<CreateTicketRequest>>();
        tickets.MapGet("/{id:guid}", Get).WithName("getTicket").RequireAuthorization(Policies.TenantMember);
        tickets.MapPatch("/{id:guid}", Update).WithName("updateTicket").RequireAuthorization(Policies.Agent)
            .AddEndpointFilter<ValidationFilter<UpdateTicketRequest>>();
        tickets.MapPost("/{id:guid}/comments", AddComment).WithName("addComment").RequireAuthorization(Policies.TenantMember)
            .AddEndpointFilter<ValidationFilter<CreateCommentRequest>>();
        // Authenticated only: the SuperAdmin has no tenant, so the filter hides every ticket and he gets 404.
        tickets.MapGet("/{id:guid}/events", ListEvents).WithName("listTicketEvents").RequireAuthorization();

        app.MapGet("/api/v1/assignees", ListAssignees).WithTags("assignees").WithName("listAssignees").RequireAuthorization(Policies.Agent);
        return app;
    }

    private static Actor ActorOf(HttpContext http) => new(http.User.UserId(), http.User.RoleOf() ?? Role.Requester);

    private static async Task<IResult> List(
        HttpContext http, TicketService service, CancellationToken ct,
        string? status = null, string? priority = null, string? assigneeId = null, string? unassigned = null,
        string? categoryId = null, string? q = null, string? sort = null, string? direction = null,
        string? page = null, string? pageSize = null)
    {
        var (query, errors) = TicketListQuery.Parse(status, priority, assigneeId, unassigned, categoryId, q, sort, direction, page, pageSize);
        return query is null
            ? Results.ValidationProblem(errors, statusCode: 422, title: "Paramètres invalides")
            : await service.ListAsync(ActorOf(http), query, ct);
    }

    private static Task<IResult> Create(CreateTicketRequest request, HttpContext http, TicketService service, CancellationToken ct) =>
        service.CreateAsync(ActorOf(http), request, ct);

    private static Task<IResult> Get(Guid id, HttpContext http, TicketService service, CancellationToken ct) =>
        service.GetAsync(id, ActorOf(http), ct);

    private static Task<IResult> Update(Guid id, UpdateTicketRequest request, HttpContext http, TicketService service, CancellationToken ct) =>
        service.UpdateAsync(id, ActorOf(http), request, ct);

    private static Task<IResult> AddComment(Guid id, CreateCommentRequest request, HttpContext http, TicketService service, CancellationToken ct) =>
        service.AddCommentAsync(id, ActorOf(http), request, ct);

    private static Task<IResult> ListEvents(Guid id, HttpContext http, TicketService service, CancellationToken ct) =>
        service.ListEventsAsync(id, ActorOf(http), ct);

    private static async Task<IResult> ListAssignees(DeskDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.Role == Role.Agent || u.Role == Role.TenantAdmin))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .ToListAsync(ct);
        return Results.Ok(users.Select(u => new UserRefDto(u.Id, u.DisplayName)));
    }
}
