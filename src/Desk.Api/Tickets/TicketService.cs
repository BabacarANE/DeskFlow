using System.Globalization;
using System.Linq.Expressions;
using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Desk.Api.Tickets;

/// <summary>The authenticated user as the ticket rules see him.</summary>
public sealed record Actor(Guid Id, Role Role)
{
    public bool IsStaff => TicketPolicy.IsStaff(Role);
}

/// <summary>
/// Ticket use cases. Tenant isolation comes from the global query filter: a ticket of another tenant is simply
/// not found (404). A Requester only ever sees his own tickets, whatever the endpoint.
/// </summary>
public sealed class TicketService(DeskDbContext db, ITenantContext tenant, TimeProvider clock)
{
    private const int MaxAttempts = 3;

    /// <summary>PostgreSQL keeps microseconds: truncate so that what we return equals what is stored.</summary>
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), now.Offset);
    }

    private IQueryable<Ticket> Visible(Actor actor) =>
        actor.IsStaff ? db.Tickets : db.Tickets.Where(t => t.RequesterId == actor.Id);

    public async Task<IResult> CreateAsync(Actor actor, CreateTicketRequest request, CancellationToken ct)
    {
        if (!TicketPolicy.CanCreate(actor.Role) || tenant.TenantId is not { } tenantId)
        {
            return Problems.Forbidden("Création de ticket non autorisée.");
        }

        if (request.CategoryId is { } categoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
        {
            return Problems.Invalid("categoryId", "Catégorie inconnue.");
        }

        var priority = request.Priority is null ? Priority.Normal : Enum.Parse<Priority>(request.Priority, ignoreCase: true);
        var (firstResponse, resolution) = await SlaMinutesAsync(priority, ct);
        var now = Now();
        var due = SlaCalculator.Compute(now, firstResponse, resolution);

        var ticket = new Ticket
        {
            Title = request.Title!.Trim(),
            Description = request.Description!.Trim(),
            Status = TicketStatus.New,
            Priority = priority,
            CategoryId = request.CategoryId,
            RequesterId = actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
            ResponseDueAt = due.ResponseDueAt,
            ResolutionDueAt = due.ResolutionDueAt,
        };

        // The sequence row is locked until commit: concurrent creations of one tenant queue up, numbers stay
        // consecutive, and a failed insert rolls the number back with it.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ticket.Number = await NextNumberAsync(tenantId, ct);
        db.Tickets.Add(ticket);
        db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.Created, null, TicketStatus.New.ToString(), now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var detail = await GetDetailAsync(ticket.Id, actor, ct);
        return Results.Created($"/api/v1/tickets/{ticket.Id}", detail);
    }

    public async Task<IResult> GetAsync(Guid id, Actor actor, CancellationToken ct) =>
        await GetDetailAsync(id, actor, ct) is { } detail ? Results.Ok(detail) : Problems.NotFound();

    public async Task<IResult> ListAsync(Actor actor, TicketListQuery query, CancellationToken ct)
    {
        var tickets = Visible(actor).AsNoTracking();

        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (query.Unassigned)
        {
            tickets = tickets.Where(t => t.AssigneeId == null);
        }
        else if (query.AssigneeId is { } assigneeId && actor.IsStaff)
        {
            tickets = tickets.Where(t => t.AssigneeId == assigneeId);
        }

        if (query.CategoryId is { } categoryId)
        {
            tickets = tickets.Where(t => t.CategoryId == categoryId);
        }

        if (query.Search is { Length: > 0 } search)
        {
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var number = int.TryParse(search.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
            tickets = tickets.Where(t => EF.Functions.ILike(t.Title, pattern, "\\") || (number != null && t.Number == number));
        }

        var ordered = Order(tickets, query.Sort, query.Descending)
            .Include(t => t.Category).Include(t => t.Requester).Include(t => t.Assignee);
        return Results.Ok(await ordered.ToPageAsync(query.Page, query.PageSize, TicketSummaryDto.From, ct));
    }

    public async Task<IResult> UpdateAsync(Guid id, Actor actor, UpdateTicketRequest request, CancellationToken ct)
    {
        if (!TicketPolicy.CanModify(actor.Role))
        {
            return Problems.Forbidden("Réservé aux agents.");
        }

        var ticket = await db.Tickets.Include(t => t.Category).Include(t => t.Assignee).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return Problems.NotFound();
        }

        // The validator guarantees a well-formed token; a stale one is a conflict, not a validation error.
        RowVersions.TryParse(request.RowVersion, out var expected);
        if (ticket.RowVersion != expected)
        {
            return StaleConflict();
        }

        db.Entry(ticket).Property(t => t.RowVersion).OriginalValue = expected;

        User? newAssignee = null;
        Category? newCategory = null;
        if (request.AssigneeId is { IsSet: true, Value: { } assigneeId } && assigneeId != ticket.AssigneeId)
        {
            newAssignee = await db.Users.FirstOrDefaultAsync(u => u.Id == assigneeId, ct);
            if (newAssignee is null || !TicketPolicy.IsAssignable(newAssignee.Role, newAssignee.IsActive))
            {
                return Problems.Invalid("assigneeId", "L'assigné doit être un agent ou un administrateur actif de l'entreprise.");
            }
        }

        if (request.CategoryId is { IsSet: true, Value: { } categoryId } && categoryId != ticket.CategoryId)
        {
            newCategory = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
            if (newCategory is null)
            {
                return Problems.Invalid("categoryId", "Catégorie inconnue.");
            }
        }

        var now = Now();
        var changed = false;

        if (request.Status is not null)
        {
            var to = Enum.Parse<TicketStatus>(request.Status, ignoreCase: true);
            var from = ticket.Status;
            // Re-sending the current status is not a transition: nothing to do.
            if (to != from)
            {
                if (!ticket.ChangeStatus(to, now))
                {
                    return Problems.Conflict($"Transition interdite : {from} vers {to}.", ProblemTypes.InvalidTransition);
                }

                db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.StatusChanged, from.ToString(), to.ToString(), now));
                changed = true;
            }
        }

        if (request.Priority is not null)
        {
            var to = Enum.Parse<Priority>(request.Priority, ignoreCase: true);
            var from = ticket.Priority;
            if (to != from)
            {
                var (firstResponse, resolution) = await SlaMinutesAsync(to, ct);
                ticket.ChangePriority(to, firstResponse, resolution);
                db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.PriorityChanged, from.ToString(), to.ToString(), now));
                changed = true;
            }
        }

        if (request.AssigneeId.IsSet && request.AssigneeId.Value != ticket.AssigneeId)
        {
            var oldName = ticket.Assignee?.DisplayName;
            ticket.AssigneeId = newAssignee?.Id;
            ticket.Assignee = newAssignee;
            db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.AssigneeChanged, oldName, newAssignee?.DisplayName, now));
            changed = true;
        }

        if (request.CategoryId.IsSet && request.CategoryId.Value != ticket.CategoryId)
        {
            var oldName = ticket.Category?.Name;
            ticket.CategoryId = newCategory?.Id;
            ticket.Category = newCategory;
            db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.CategoryChanged, oldName, newCategory?.Name, now));
            changed = true;
        }

        if (changed)
        {
            ticket.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return StaleConflict();
            }
        }

        db.ChangeTracker.Clear();
        return Results.Ok(await GetDetailAsync(id, actor, ct));
    }

    public async Task<IResult> AddCommentAsync(Guid id, Actor actor, CreateCommentRequest request, CancellationToken ct)
    {
        var internalNote = request.IsInternal == true;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var ticket = await Visible(actor).FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null)
            {
                return Problems.NotFound();
            }

            if (internalNote && !TicketPolicy.CanPostInternal(actor.Role))
            {
                return Problems.Forbidden("Les notes internes sont réservées aux agents.");
            }

            if (ticket.Status == TicketStatus.Closed)
            {
                return Problems.Conflict("Le ticket est clos : il n'accepte plus de commentaire.", ProblemTypes.TicketClosed);
            }

            var now = Now();
            var comment = new TicketComment
            {
                TicketId = ticket.Id,
                AuthorId = actor.Id,
                Body = request.Body!.Trim(),
                IsInternal = internalNote,
                CreatedAt = now,
            };
            db.TicketComments.Add(comment);

            if (ticket.FirstResponseAt is null && TicketPolicy.CountsAsFirstResponse(actor.Role, actor.Id, ticket.RequesterId, internalNote))
            {
                ticket.FirstResponseAt = now;
            }

            if (TicketPolicy.ReopensPending(actor.Role, ticket.Status))
            {
                var from = ticket.Status;
                ticket.ChangeStatus(TicketStatus.Open, now);
                db.TicketEvents.Add(NewEvent(ticket.Id, actor.Id, TicketEventType.StatusChanged, from.ToString(), TicketStatus.Open.ToString(), now));
            }

            // Last activity; also bumps the row version, so an agent editing a stale copy gets a conflict.
            ticket.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another comment or edit won the race on the ticket row: start again from fresh data.
                db.ChangeTracker.Clear();
                continue;
            }

            var author = await db.Users.AsNoTracking().FirstAsync(u => u.Id == actor.Id, ct);
            comment.Author = author;
            return Results.Created($"/api/v1/tickets/{id}", CommentDto.From(comment));
        }

        return Problems.Conflict("Le ticket est modifié en continu, réessayez.", ProblemTypes.ConcurrencyConflict);
    }

    public async Task<IResult> ListEventsAsync(Guid id, Actor actor, CancellationToken ct)
    {
        if (!await Visible(actor).AnyAsync(t => t.Id == id, ct))
        {
            return Problems.NotFound();
        }

        var events = await db.TicketEvents.AsNoTracking().Include(e => e.Actor)
            .Where(e => e.TicketId == id).OrderBy(e => e.At).ThenBy(e => e.Seq).ToListAsync(ct);
        // Events of one operation share their instant: the kind of event (declaration order) breaks the tie.
        return Results.Ok(events.OrderBy(e => e.At).ThenBy(e => e.Type).ThenBy(e => e.Seq).Select(TicketEventDto.From));
    }

    private async Task<TicketDetailDto?> GetDetailAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var ticket = await Visible(actor).AsNoTracking()
            .Include(t => t.Category).Include(t => t.Requester).Include(t => t.Assignee)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return null;
        }

        var comments = db.TicketComments.AsNoTracking().Include(c => c.Author).Where(c => c.TicketId == id);
        if (!actor.IsStaff)
        {
            comments = comments.Where(c => !c.IsInternal);
        }

        var list = await comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        return TicketDetailDto.From(ticket, list.Select(CommentDto.From).ToList());
    }

    private async Task<(int FirstResponse, int Resolution)> SlaMinutesAsync(Priority priority, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Priority == priority, ct)
            ?? SlaPolicy.CreateDefaults(Guid.Empty).First(p => p.Priority == priority);
        return (policy.FirstResponseMinutes, policy.ResolutionMinutes);
    }

    private async Task<int> NextNumberAsync(Guid tenantId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "TicketCounters" ("TenantId", "LastNumber") VALUES (@tenant, 1)
            ON CONFLICT ("TenantId") DO UPDATE SET "LastNumber" = "TicketCounters"."LastNumber" + 1
            RETURNING "LastNumber"
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static TicketEvent NewEvent(Guid ticketId, Guid actorId, TicketEventType type, string? oldValue, string? newValue, DateTimeOffset at) =>
        new() { TicketId = ticketId, ActorId = actorId, Type = type, OldValue = oldValue, NewValue = newValue, At = at };

    private static IResult StaleConflict() =>
        Problems.Conflict("Le ticket a été modifié entre-temps : rechargez-le.", ProblemTypes.ConcurrencyConflict);

    private static IQueryable<Ticket> Order(IQueryable<Ticket> tickets, string sort, bool descending)
    {
        // Priority is stored as text; rank it explicitly so that sorting follows Low < Normal < High < Urgent.
        // The values go through locals: an enum literal in the lambda would be compiled to an int comparison,
        // which EF cannot apply to a text column.
        var urgent = Priority.Urgent;
        var high = Priority.High;
        var normal = Priority.Normal;
        Expression<Func<Ticket, int>> rank = t => t.Priority == urgent ? 3 : t.Priority == high ? 2 : t.Priority == normal ? 1 : 0;

        return (sort, descending) switch
        {
            ("updatedAt", true) => tickets.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Number),
            ("updatedAt", false) => tickets.OrderBy(t => t.UpdatedAt).ThenBy(t => t.Number),
            ("number", true) => tickets.OrderByDescending(t => t.Number),
            ("number", false) => tickets.OrderBy(t => t.Number),
            ("priority", true) => tickets.OrderByDescending(rank).ThenByDescending(t => t.Number),
            ("priority", false) => tickets.OrderBy(rank).ThenBy(t => t.Number),
            (_, true) => tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Number),
            _ => tickets.OrderBy(t => t.CreatedAt).ThenBy(t => t.Number),
        };
    }
}

/// <summary>Validated list parameters (all query values arrive as text so that bad ones answer 422, not 400).</summary>
public sealed record TicketListQuery(
    TicketStatus? Status,
    Priority? Priority,
    Guid? AssigneeId,
    bool Unassigned,
    Guid? CategoryId,
    string? Search,
    string Sort,
    bool Descending,
    int Page,
    int PageSize)
{
    public static readonly string[] SortFields = ["createdAt", "updatedAt", "priority", "number"];

    public static (TicketListQuery? Query, Dictionary<string, string[]> Errors) Parse(
        string? status, string? priority, string? assigneeId, string? unassigned, string? categoryId,
        string? q, string? sort, string? direction, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();

        TicketStatus? parsedStatus = null;
        if (status is not null)
        {
            if (EnumNames.TryParse<TicketStatus>(status, out var s)) { parsedStatus = s; }
            else { errors["status"] = ["Statut invalide."]; }
        }

        Priority? parsedPriority = null;
        if (priority is not null)
        {
            if (EnumNames.TryParse<Priority>(priority, out var p)) { parsedPriority = p; }
            else { errors["priority"] = ["Priorité invalide."]; }
        }

        var assignee = ParseGuid("assigneeId", assigneeId, errors);
        var category = ParseGuid("categoryId", categoryId, errors);

        var onlyUnassigned = false;
        if (unassigned is not null)
        {
            if (bool.TryParse(unassigned, out var u)) { onlyUnassigned = u; }
            else { errors["unassigned"] = ["unassigned doit valoir true ou false."]; }
        }

        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > 100 })
        {
            errors["q"] = ["q ne doit pas dépasser 100 caractères."];
        }

        var sortField = sort ?? "createdAt";
        if (!SortFields.Contains(sortField))
        {
            errors["sort"] = ["sort doit valoir createdAt, updatedAt, priority ou number."];
        }

        var descending = true;
        if (direction is not null)
        {
            if (direction is "asc" or "desc") { descending = direction == "desc"; }
            else { errors["direction"] = ["direction doit valoir asc ou desc."]; }
        }

        var pageNumber = 1;
        if (page is not null && (!int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber) || pageNumber < 1))
        {
            errors["page"] = ["page doit être un entier supérieur ou égal à 1."];
        }

        var size = 20;
        if (pageSize is not null
            && (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > PagingExtensions.MaxPageSize))
        {
            errors["pageSize"] = [$"pageSize doit être compris entre 1 et {PagingExtensions.MaxPageSize}."];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new TicketListQuery(parsedStatus, parsedPriority, assignee, onlyUnassigned, category, search, sortField, descending, pageNumber, size), errors);
    }

    private static Guid? ParseGuid(string name, string? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (Guid.TryParse(value, out var id))
        {
            return id;
        }

        errors[name] = [$"{name} doit être un UUID."];
        return null;
    }
}
