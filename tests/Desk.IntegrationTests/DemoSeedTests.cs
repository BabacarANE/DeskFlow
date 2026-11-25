using System.Net;
using System.Net.Http.Json;
using Desk.Core.Domain;
using Desk.Infrastructure.Bootstrap;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>The rich demo seeder: 12 articles, 30 tickets, idempotent, isolated, silent (no outbox message).</summary>
public class DemoSeedTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?> Settings => new() { ["Demo:Enabled"] = "true" };

    private async Task<Session> DemoSessionAsync(string role)
    {
        var token = await JsonAsync(await Http.PostAsJsonAsync("/api/v1/auth/demo", new { role }));
        return new Session("demo", token.GetProperty("accessToken").GetString()!, "", Http);
    }

    private sealed record Counts(int Articles, int Tickets, int Comments, int Events, int Users, int Breaches, int Counter, int Outbox, int Tenants);

    private Task<Counts> CountAsync() => QueryDbAsync(async db => new Counts(
        await db.KnowledgeArticles.IgnoreQueryFilters().CountAsync(),
        await db.Tickets.IgnoreQueryFilters().CountAsync(),
        await db.TicketComments.IgnoreQueryFilters().CountAsync(),
        await db.TicketEvents.IgnoreQueryFilters().CountAsync(),
        await db.Users.IgnoreQueryFilters().CountAsync(),
        await db.SlaBreaches.IgnoreQueryFilters().CountAsync(),
        (await db.TicketCounters.IgnoreQueryFilters().Select(c => c.LastNumber).SingleAsync()),
        await db.OutboxMessages.CountAsync(),
        await db.Tenants.CountAsync()));

    [Fact]
    public async Task Seeding_twice_gives_the_same_12_articles_and_30_tickets_and_writes_no_outbox_message()
    {
        await DemoSessionAsync("admin"); // the host has started: migrations and the seeder have run
        var first = await CountAsync();

        Assert.Equal(12, first.Articles);
        Assert.Equal(30, first.Tickets);
        Assert.Equal(30, first.Counter);
        Assert.Equal(1, first.Tenants);
        Assert.True(first.Comments > 40, $"comments: {first.Comments}");
        Assert.True(first.Events > 80, $"events: {first.Events}");
        Assert.Equal(0, first.Outbox);

        await DatabaseInitializer.RunAsync(Factory.Services);
        await DatabaseInitializer.RunAsync(Factory.Services);

        Assert.Equal(first, await CountAsync());
        Assert.Equal(9, await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync(a => a.Status == ArticleStatus.Published)));
        Assert.Equal(3, await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync(a => a.Status == ArticleStatus.Draft)));
    }

    [Fact]
    public async Task Tickets_are_numbered_uniquely_and_spread_over_every_status_priority_category_and_person()
    {
        await DemoSessionAsync("admin");
        var tickets = await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Number).ToListAsync());

        Assert.Equal(Enumerable.Range(1, 30), tickets.Select(t => t.Number));
        Assert.Equal(30, tickets.Select(t => t.Id).Distinct().Count());
        Assert.All(Enum.GetValues<TicketStatus>(), s => Assert.Contains(tickets, t => t.Status == s));
        Assert.All(Enum.GetValues<Priority>(), p => Assert.Contains(tickets, t => t.Priority == p));
        Assert.True(tickets.Select(t => t.CategoryId).Distinct().Count() >= 5);
        Assert.True(tickets.Select(t => t.RequesterId).Distinct().Count() >= 4);
        Assert.True(tickets.Where(t => t.AssigneeId != null).Select(t => t.AssigneeId).Distinct().Count() >= 2);
        Assert.Contains(tickets, t => t.AssigneeId == null);

        // Numbers follow creation order; creation dates span about 30 days, none in the future.
        Assert.Equal(tickets.OrderBy(t => t.CreatedAt).Select(t => t.Number), tickets.Select(t => t.Number));
        var now = DateTimeOffset.UtcNow;
        Assert.InRange((now - tickets[0].CreatedAt).TotalDays, 28, 31);
        Assert.All(tickets, t => Assert.True(t.CreatedAt <= now && t.UpdatedAt <= now && t.UpdatedAt >= t.CreatedAt));
        Assert.All(tickets, t => Assert.Contains("données fictives", t.Description));
    }

    [Fact]
    public async Task Histories_are_consistent_with_the_domain_rules_and_sla_state()
    {
        await DemoSessionAsync("admin");
        var data = await QueryDbAsync(async db => new
        {
            Tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking().ToListAsync(),
            Events = await db.TicketEvents.IgnoreQueryFilters().AsNoTracking().ToListAsync(),
            Comments = await db.TicketComments.IgnoreQueryFilters().AsNoTracking().Include(c => c.Author).ToListAsync(),
            Breaches = await db.SlaBreaches.IgnoreQueryFilters().AsNoTracking().ToListAsync(),
            Policies = await db.SlaPolicies.IgnoreQueryFilters().AsNoTracking().ToListAsync(),
        });
        var now = DateTimeOffset.UtcNow;

        foreach (var ticket in data.Tickets)
        {
            var events = data.Events.Where(e => e.TicketId == ticket.Id).OrderBy(e => e.At).ThenBy(e => e.Seq).ToList();
            var label = $"#{ticket.Number} {ticket.Title}";

            Assert.True(events[0].Type == TicketEventType.Created && events[0].At == ticket.CreatedAt, label);

            // The current status and priority are what the last matching event says.
            var status = events.LastOrDefault(e => e.Type == TicketEventType.StatusChanged)?.NewValue ?? "New";
            Assert.True(status == ticket.Status.ToString(), $"{label}: status {ticket.Status} vs events {status}");
            var priority = events.LastOrDefault(e => e.Type == TicketEventType.PriorityChanged)?.NewValue;
            Assert.True(priority is null || priority == ticket.Priority.ToString(), $"{label}: priority");

            // Only allowed transitions, replayed from New.
            var current = TicketStatus.New;
            foreach (var change in events.Where(e => e.Type == TicketEventType.StatusChanged))
            {
                var to = Enum.Parse<TicketStatus>(change.NewValue!);
                Assert.True(Desk.Core.Rules.TicketStateMachine.CanTransition(current, to), $"{label}: {current} to {to}");
                Assert.Equal(current.ToString(), change.OldValue);
                current = to;
            }

            // First response = first public comment of staff; resolved dates follow the status.
            var firstStaffReply = data.Comments.Where(c => c.TicketId == ticket.Id && !c.IsInternal && c.Author!.Role != Role.Requester)
                .OrderBy(c => c.CreatedAt).FirstOrDefault();
            Assert.Equal(firstStaffReply?.CreatedAt, ticket.FirstResponseAt);
            Assert.True((ticket.Status is TicketStatus.Resolved or TicketStatus.Closed) == (ticket.ResolvedAt is not null), label);
            Assert.True(ticket.Status != TicketStatus.Pending || ticket.PendingSince is not null, label);

            // Nothing left for the SlaWatcher: a New or Open ticket is within its deadlines unless it was escalated.
            if (ticket.Status is TicketStatus.New or TicketStatus.Open && ticket.EscalatedAt is null)
            {
                Assert.True(ticket.ResolutionDueAt > now, $"{label}: resolution overdue without escalation");
                Assert.True(ticket.ResponseDueAt > now || ticket.FirstResponseAt is not null, $"{label}: response overdue without escalation");
            }

            // Deadlines come from the SLA table of the (current) priority, paused time included.
            var policy = data.Policies.Single(p => p.Priority == ticket.Priority);
            var expected = Desk.Core.Rules.SlaCalculator.Compute(ticket.CreatedAt, policy.FirstResponseMinutes, policy.ResolutionMinutes, ticket.SlaPausedTotal);
            Assert.True(expected.ResponseDueAt == ticket.ResponseDueAt && expected.ResolutionDueAt == ticket.ResolutionDueAt, $"{label}: deadlines");
        }

        // Escalations: EscalatedAt, a marker per missed deadline, and the system event when the priority moved.
        var escalated = data.Tickets.Where(t => t.EscalatedAt is not null).ToList();
        Assert.Equal(3, escalated.Count);
        Assert.All(escalated, t => Assert.Contains(data.Breaches, b => b.TicketId == t.Id));
        Assert.Equal(escalated.Select(t => t.Id).Order(), data.Breaches.Select(b => b.TicketId).Distinct().Order());
        Assert.All(data.Breaches, b => Assert.True(b.DetectedAt > b.DueAt));
        Assert.Equal(2, data.Events.Count(e => e.Type == TicketEventType.PriorityChanged && e.ActorId is null));
        Assert.Contains(data.Tickets, t => t.Status == TicketStatus.Resolved && t.EscalatedAt is not null);
        Assert.Contains(data.Tickets, t => t is { Status: TicketStatus.Open, Priority: Priority.Urgent, EscalatedAt: not null });

        // Both public and internal comments exist.
        Assert.Contains(data.Comments, c => c.IsInternal);
        Assert.Contains(data.Comments, c => !c.IsInternal && c.Author!.Role == Role.Requester);
        Assert.Contains(data.Comments, c => !c.IsInternal && c.Author!.Role != Role.Requester);
        Assert.Contains(data.Events, e => e.Type == TicketEventType.AssigneeChanged);
        Assert.Contains(data.Events, e => e.Type == TicketEventType.CategoryChanged);

        // Two tickets are about to breach (within two hours) without being late yet.
        Assert.True(data.Tickets.Count(t => t.Status is TicketStatus.New or TicketStatus.Open && t.EscalatedAt is null
            && (t.ResolutionDueAt!.Value < now.AddHours(2) || (t.FirstResponseAt is null && t.ResponseDueAt!.Value < now.AddHours(2)))) >= 2);
    }

    [Fact]
    public async Task The_demo_data_is_served_by_the_api_with_visibility_rules_and_never_leaks_to_another_company()
    {
        var other = await CreateTenantWithAdminAsync("autre", "admin@autre.test");
        var agent = await DemoSessionAsync("agent");
        var requester = await DemoSessionAsync("demandeur");

        var tickets = await JsonAsync(await agent.GetAsync("/api/v1/tickets?pageSize=100"));
        Assert.Equal(30, tickets.GetProperty("total").GetInt32());
        var articles = await JsonAsync(await agent.GetAsync("/api/v1/articles?pageSize=100"));
        Assert.Equal(12, articles.GetProperty("total").GetInt32());
        Assert.Equal(9, (await JsonAsync(await requester.GetAsync("/api/v1/articles?pageSize=100"))).GetProperty("total").GetInt32());

        // The requester only sees his own tickets, and no internal note.
        var mine = await JsonAsync(await requester.GetAsync("/api/v1/tickets?pageSize=100"));
        var mineIds = mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.InRange(mineIds.Count, 3, 29);
        foreach (var id in mineIds)
        {
            var detail = await JsonAsync(await requester.GetAsync($"/api/v1/tickets/{id}"));
            Assert.All(detail.GetProperty("comments").EnumerateArray(), c => Assert.False(c.GetProperty("isInternal").GetBoolean()));
        }

        // Accent-insensitive search finds the fictional printer article.
        Assert.True((await JsonAsync(await agent.GetAsync("/api/v1/articles?q=imprimanté"))).GetProperty("total").GetInt32() >= 1);

        // Another company sees nothing, and the seeder did not touch it.
        Assert.Equal(0, (await JsonAsync(await other.Admin.GetAsync("/api/v1/tickets"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await JsonAsync(await other.Admin.GetAsync("/api/v1/articles"))).GetProperty("total").GetInt32());
        foreach (var id in mineIds.Take(2))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/tickets/{id}")).StatusCode);
        }

        await DatabaseInitializer.RunAsync(Factory.Services);
        Assert.Equal(0, await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == other.TenantId)));
        Assert.Equal(0, await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync(a => a.TenantId == other.TenantId)));
        Assert.Equal(0, await QueryDbAsync(db => db.TicketCounters.IgnoreQueryFilters().CountAsync(c => c.TenantId == other.TenantId)));
    }

    [Fact]
    public async Task A_restart_never_rewrites_what_the_administrator_changed_or_added()
    {
        var agent = await DemoSessionAsync("agent");
        var articles = await JsonAsync(await agent.GetAsync("/api/v1/articles?pageSize=100"));
        var article = articles.GetProperty("items").EnumerateArray().First();
        var id = article.GetProperty("id").GetGuid();
        var full = await JsonAsync(await agent.GetAsync($"/api/v1/articles/{id}"));
        var edited = await agent.PutAsync($"/api/v1/articles/{id}", new
        {
            title = "Titre modifié par l'administrateur",
            symptom = full.GetProperty("symptom").GetString(),
            cause = full.GetProperty("cause").GetString(),
            solution = full.GetProperty("solution").GetString(),
            tags = new[] { "modifié" },
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var created = await agent.PostAsync("/api/v1/tickets", new { title = "Ticket créé pendant la démo", description = "Un vrai ticket." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var number = (await JsonAsync(created)).GetProperty("number").GetInt32();
        Assert.Equal(31, number);
        var before = await CountAsync();

        await DatabaseInitializer.RunAsync(Factory.Services);

        var after = await CountAsync();
        Assert.Equal(before with { }, after);
        Assert.Equal("Titre modifié par l'administrateur", (await JsonAsync(await agent.GetAsync($"/api/v1/articles/{id}"))).GetProperty("title").GetString());
        Assert.Equal(31, after.Counter);
    }
}
