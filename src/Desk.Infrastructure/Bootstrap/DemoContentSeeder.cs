using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Bootstrap;

/// <summary>
/// Rich demo content: 12 articles and 30 tickets with comments, events and SLA state (SPEC section 12).
/// <list type="bullet">
/// <item><b>Idempotent, never overwrites.</b> Articles are created only when the demo company has none, tickets only when it has
/// none: once the administrator (or the app) has created, edited or deleted anything, a restart changes nothing.</item>
/// <item><b>No outbox message, on purpose.</b> The seeder writes the rows directly and never goes through <c>OutboxWriter</c>: 30 tickets,
/// their comments and resolutions would otherwise publish dozens of events at start and flood RabbitMQ and Mailpit (e-mails to
/// the fictional addresses, notifications, knowledge drafts from the resolved tickets).</item>
/// <item><b>The domain rules do the work.</b> Statuses go through <c>Ticket.ChangeStatus</c>, priorities through <c>ChangePriority</c>
/// with the tenant SLA table, first responses through <c>TicketPolicy</c>; numbers come from the tenant's <c>TicketCounter</c>.</item>
/// <item><b>Nothing for the SlaWatcher to emit.</b> Every New or Open ticket is within its deadlines, except the escalated ones, which
/// carry <c>EscalatedAt</c> and their <c>SlaBreach</c> markers exactly as the EscalationConsumer would have left them.</item>
/// </list>
/// </summary>
internal static class DemoContentSeeder
{
    public static async Task<(int Articles, int Tickets)> SeedAsync(
        DeskDbContext db, Tenant tenant, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var now = Truncate(clock.GetUtcNow());
        var users = await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenant.Id).ToListAsync(ct);

        var articles = 0;
        var tickets = 0;
        if (!await db.KnowledgeArticles.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenant.Id, ct))
        {
            articles = SeedArticles(db, tenant, users, now);
        }

        if (!await db.Tickets.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenant.Id, ct))
        {
            tickets = await SeedTicketsAsync(db, tenant, users, now, ct);
        }

        if (articles + tickets > 0)
        {
            logger.LogInformation("Demo content seeded: {Articles} articles, {Tickets} tickets (no outbox message written)", articles, tickets);
        }

        return (articles, tickets);
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static int SeedArticles(DeskDbContext db, Tenant tenant, List<User> users, DateTimeOffset now)
    {
        var author = Person(users, DemoAccounts.ByDemoRole["agent"].Email);
        foreach (var article in DemoArticles.All)
        {
            var created = now.AddDays(-article.CreatedDaysAgo);
            db.KnowledgeArticles.Add(new KnowledgeArticle
            {
                TenantId = tenant.Id,
                Title = article.Title,
                Symptom = article.Symptom,
                Cause = article.Cause,
                Solution = article.Solution,
                Tags = ArticleRules.JoinTags(ArticleRules.NormalizeTags(article.Tags)),
                Status = article.PublishedDaysAgo is null ? ArticleStatus.Draft : ArticleStatus.Published,
                AuthorId = author.Id,
                CreatedAt = created,
                PublishedAt = article.PublishedDaysAgo is { } days ? now.AddDays(-days) : null,
            });
        }

        return DemoArticles.All.Count;
    }

    private static User Person(List<User> users, string email) =>
        users.FirstOrDefault(u => u.Email == email) ?? throw new InvalidOperationException($"Demo account {email} is missing.");

    private static async Task<int> SeedTicketsAsync(DeskDbContext db, Tenant tenant, List<User> users, DateTimeOffset now, CancellationToken ct)
    {
        // The fictional colleagues exist only to give the tickets variety; created here, with the tickets, never again after.
        foreach (var (email, name, role) in DemoTickets.Staff.Concat(DemoTickets.Requesters))
        {
            if (users.All(u => u.Email != email) && !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            {
                var user = new User { TenantId = tenant.Id, Email = email, DisplayName = name, Role = role, CreatedAt = now.AddDays(-30) };
                db.Users.Add(user);
                users.Add(user);
            }
        }

        var staff = DemoTickets.Staff.Select(s => Person(users, s.Email)).ToArray();
        var requesters = DemoTickets.Requesters.Select(r => Person(users, r.Email)).ToArray();
        var categories = await db.Categories.IgnoreQueryFilters().Where(c => c.TenantId == tenant.Id).ToDictionaryAsync(c => c.Name, ct);
        var sla = (await db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == tenant.Id).ToListAsync(ct))
            .Concat(SlaPolicy.CreateDefaults(tenant.Id))
            .GroupBy(p => p.Priority).ToDictionary(g => g.Key, g => g.First());

        var counter = await db.TicketCounters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenant.Id, ct);
        if (counter is null)
        {
            counter = new TicketCounter { TenantId = tenant.Id };
            db.TicketCounters.Add(counter);
        }

        // Oldest first: numbers follow creation order, like in real life.
        var plans = DemoTickets.All.OrderByDescending(p => p.AgeMinutes).ToList();
        foreach (var plan in plans)
        {
            var replay = new Replay(db, tenant.Id, now, staff, requesters, categories, sla, ++counter.LastNumber, plan);
            replay.Run();
        }

        return plans.Count;
    }

    /// <summary>Replays the life of one ticket with the domain rules and records what the API would have recorded.</summary>
    private sealed class Replay(
        DeskDbContext db, Guid tenantId, DateTimeOffset now, User[] staff, User[] requesters,
        Dictionary<string, Category> categories, Dictionary<Priority, SlaPolicy> sla, int number, DemoTicket plan)
    {
        private readonly DateTimeOffset _created = now.AddMinutes(-plan.AgeMinutes);
        private readonly Ticket _ticket = new() { TenantId = tenantId, Number = number };
        private readonly Dictionary<Guid, string> _names = staff.Concat(requesters).ToDictionary(u => u.Id, u => u.DisplayName);
        private DateTimeOffset _last;

        public void Run()
        {
            var requester = requesters[plan.Requester];
            var category = plan.Category is { } name && categories.TryGetValue(name, out var found) ? found : null;
            var due = SlaCalculator.Compute(_created, sla[plan.Priority].FirstResponseMinutes, sla[plan.Priority].ResolutionMinutes);
            _ticket.Title = plan.Title;
            _ticket.Description = plan.Description + DemoTickets.FictionalNote;
            _ticket.Status = TicketStatus.New;
            _ticket.Priority = plan.Priority;
            _ticket.CategoryId = category?.Id;
            _ticket.RequesterId = requester.Id;
            _ticket.CreatedAt = _created;
            _ticket.UpdatedAt = _created;
            _ticket.ResponseDueAt = due.ResponseDueAt;
            _ticket.ResolutionDueAt = due.ResolutionDueAt;
            _last = _created;
            db.Tickets.Add(_ticket);
            AddEvent(requester.Id, TicketEventType.Created, null, TicketStatus.New.ToString(), _created);

            foreach (var step in plan.Steps)
            {
                Apply(step, requester);
            }

            if (_last > now)
            {
                throw new InvalidOperationException($"Demo ticket \"{plan.Title}\" has a step in the future.");
            }
        }

        private void Apply(Step step, User requester)
        {
            var at = step.Kind == StepKind.Escalate ? EscalationTime() : _created.AddMinutes(step.Minute);
            if (at < _last)
            {
                throw new InvalidOperationException($"Demo ticket \"{plan.Title}\": steps are out of order at minute {step.Minute}.");
            }

            var actor = step.Staff >= 0 ? staff[step.Staff] : requester;
            switch (step.Kind)
            {
                case StepKind.Assign:
                    AddEvent(actor.Id, TicketEventType.AssigneeChanged,
                        _ticket.AssigneeId is { } previous ? _names[previous] : null, actor.DisplayName, at);
                    _ticket.AssigneeId = actor.Id;
                    break;
                case StepKind.Open:
                    ChangeStatus(TicketStatus.Open, actor, at);
                    break;
                case StepKind.Reply:
                    Comment(actor, step.Text!, isInternal: false, at);
                    break;
                case StepKind.Note:
                    Comment(actor, step.Text!, isInternal: true, at);
                    break;
                case StepKind.Ask:
                    ChangeStatus(TicketStatus.Pending, actor, at);
                    Comment(actor, step.Text!, isInternal: false, at);
                    break;
                case StepKind.Answer:
                    Comment(requester, step.Text!, isInternal: false, at);
                    break;
                case StepKind.Resolve:
                    ChangeStatus(TicketStatus.Resolved, actor, at);
                    Comment(actor, step.Text!, isInternal: false, at);
                    break;
                case StepKind.Close:
                    ChangeStatus(TicketStatus.Closed, actor, at);
                    break;
                case StepKind.SetPriority:
                    ChangePriority(step.Priority!.Value, actor.Id, at);
                    break;
                case StepKind.SetCategory:
                    var target = categories.TryGetValue(step.Category!, out var category) ? category : null;
                    AddEvent(actor.Id, TicketEventType.CategoryChanged,
                        categories.Values.FirstOrDefault(c => c.Id == _ticket.CategoryId)?.Name, target?.Name, at);
                    _ticket.CategoryId = target?.Id;
                    break;
                case StepKind.Escalate:
                    Escalate(at);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {step.Kind}.");
            }

            _ticket.UpdatedAt = at;
            _last = at;
        }

        private void ChangeStatus(TicketStatus to, User actor, DateTimeOffset at)
        {
            var from = _ticket.Status;
            if (!_ticket.ChangeStatus(to, at))
            {
                throw new InvalidOperationException($"Demo ticket \"{plan.Title}\": transition {from} to {to} is not allowed.");
            }

            AddEvent(actor.Id, TicketEventType.StatusChanged, from.ToString(), to.ToString(), at);
        }

        private void Comment(User author, string body, bool isInternal, DateTimeOffset at)
        {
            if (_ticket.Status == TicketStatus.Closed)
            {
                throw new InvalidOperationException($"Demo ticket \"{plan.Title}\": comment on a closed ticket.");
            }

            db.TicketComments.Add(new TicketComment
            {
                TenantId = tenantId,
                TicketId = _ticket.Id,
                AuthorId = author.Id,
                Body = body,
                IsInternal = isInternal,
                CreatedAt = at,
            });

            if (_ticket.FirstResponseAt is null && TicketPolicy.CountsAsFirstResponse(author.Role, author.Id, _ticket.RequesterId, isInternal))
            {
                _ticket.FirstResponseAt = at;
            }

            // A requester answering a ticket that waits for him reopens it, as the API does.
            if (TicketPolicy.ReopensPending(author.Role, _ticket.Status))
            {
                ChangeStatus(TicketStatus.Open, author, at);
            }
        }

        private void ChangePriority(Priority to, Guid actorId, DateTimeOffset at)
        {
            var from = _ticket.Priority;
            _ticket.ChangePriority(to, sla[to].FirstResponseMinutes, sla[to].ResolutionMinutes);
            AddEvent(actorId, TicketEventType.PriorityChanged, from.ToString(), to.ToString(), at);
        }

        /// <summary>Two minutes after the first missed deadline of a New or Open ticket: the SlaWatcher detects it, the consumer escalates.</summary>
        private DateTimeOffset EscalationTime()
        {
            var missed = new List<DateTimeOffset>();
            if (_ticket.ResponseDueAt is { } response && _ticket.FirstResponseAt is null)
            {
                missed.Add(response);
            }

            if (_ticket.ResolutionDueAt is { } resolution)
            {
                missed.Add(resolution);
            }

            return missed.Min().AddMinutes(2);
        }

        /// <summary>The state the EscalationConsumer leaves: breach markers, priority raised one step (not above Urgent), EscalatedAt.</summary>
        private void Escalate(DateTimeOffset at)
        {
            if (_ticket.Status is not (TicketStatus.New or TicketStatus.Open))
            {
                throw new InvalidOperationException($"Demo ticket \"{plan.Title}\": only a New or Open ticket escalates.");
            }

            var detected = at.AddMinutes(-1);
            if (_ticket.ResponseDueAt is { } responseDue && responseDue < detected && _ticket.FirstResponseAt is null)
            {
                db.SlaBreaches.Add(new SlaBreach { TicketId = _ticket.Id, Kind = SlaBreach.ResponseKind, TenantId = tenantId, DueAt = responseDue, DetectedAt = detected });
            }

            if (_ticket.ResolutionDueAt is { } resolutionDue && resolutionDue < detected)
            {
                db.SlaBreaches.Add(new SlaBreach { TicketId = _ticket.Id, Kind = SlaBreach.ResolutionKind, TenantId = tenantId, DueAt = resolutionDue, DetectedAt = detected });
            }

            var from = _ticket.Priority;
            var to = from == Priority.Urgent ? Priority.Urgent : from + 1;
            _ticket.ChangePriority(to, sla[to].FirstResponseMinutes, sla[to].ResolutionMinutes);
            _ticket.EscalatedAt = at;
            if (to != from)
            {
                // The consumer writes this event without an actor (system).
                db.TicketEvents.Add(new TicketEvent
                {
                    TenantId = tenantId,
                    TicketId = _ticket.Id,
                    ActorId = null,
                    Type = TicketEventType.PriorityChanged,
                    OldValue = from.ToString(),
                    NewValue = to.ToString(),
                    At = at,
                });
            }
        }

        private void AddEvent(Guid actorId, TicketEventType type, string? oldValue, string? newValue, DateTimeOffset at) =>
            db.TicketEvents.Add(new TicketEvent
            {
                TenantId = tenantId,
                TicketId = _ticket.Id,
                ActorId = actorId,
                Type = type,
                OldValue = oldValue,
                NewValue = newValue,
                At = at,
            });
    }
}
