using System.Text.Json;
using Desk.Core.Domain;
using Desk.Infrastructure.Messaging;
using Desk.Infrastructure.Sla;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>SlaWatcher (detection, exactly once, several instances) and EscalationConsumer (one step, once, concurrency).</summary>
public class SlaEscalationTests(PostgresFixture postgres, RabbitMqFixture rabbit) : ConsumerTestBase(postgres, rabbit)
{
    private static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddHours(-2);

    private static void Overdue(Ticket t)
    {
        t.CreatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        t.ResponseDueAt = Past;
        t.ResolutionDueAt = Past;
    }

    private static SlaWatcher NewWatcher(TestWorker worker) => ActivatorUtilities.CreateInstance<SlaWatcher>(worker.Services);

    private async Task<List<(string Type, Guid TenantId, Guid TicketId, string Kind, int Number)>> BreachEventsAsync()
    {
        var rows = await ReadAsync(db => db.OutboxMessages.Where(o => o.Type == "ticket.sla.breached").ToListAsync());
        return rows.Select(r =>
        {
            using var json = JsonDocument.Parse(r.Payload);
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(r.Id, json.RootElement.GetProperty("messageId").GetGuid());
            Assert.NotEmpty(json.RootElement.GetProperty("correlationId").GetString()!);
            Assert.True(payload.TryGetProperty("dueAt", out _));
            return (r.Type, json.RootElement.GetProperty("tenantId").GetGuid(), payload.GetProperty("ticketId").GetGuid(),
                payload.GetProperty("kind").GetString()!, payload.GetProperty("number").GetInt32());
        }).ToList();
    }

    // ---- SlaWatcher ----

    [Fact]
    public async Task The_watcher_flags_only_new_or_open_unescalated_overdue_tickets_once_per_kind_whatever_the_cycles()
    {
        var a = await SeedCompanyAsync("sla-a");
        var b = await SeedCompanyAsync("sla-b");
        var both = await AddTicketAsync(a, Overdue); // response and resolution missed
        var resolutionOnly = await AddTicketAsync(a, t => { Overdue(t); t.FirstResponseAt = DateTimeOffset.UtcNow.AddDays(-2); t.Status = TicketStatus.New; });
        var responseOnly = await AddTicketAsync(a, t => { t.ResponseDueAt = Past; t.ResolutionDueAt = DateTimeOffset.UtcNow.AddDays(1); });
        await AddTicketAsync(a, t => { Overdue(t); t.Status = TicketStatus.Pending; });
        await AddTicketAsync(a, t => { Overdue(t); t.Status = TicketStatus.Resolved; });
        await AddTicketAsync(a, t => { Overdue(t); t.Status = TicketStatus.Closed; });
        await AddTicketAsync(a, t => { Overdue(t); t.EscalatedAt = DateTimeOffset.UtcNow.AddHours(-1); });
        await AddTicketAsync(a, t => { t.ResponseDueAt = DateTimeOffset.UtcNow.AddHours(1); t.ResolutionDueAt = DateTimeOffset.UtcNow.AddHours(5); });
        await AddTicketAsync(a); // no deadline at all
        var otherTenant = await AddTicketAsync(b, Overdue);
        await using var worker = await StartWorkerAsync(NoWatcher, consumers: false, dispatcher: false);
        var watcher = NewWatcher(worker);

        var first = await watcher.ScanAsync(CancellationToken.None);
        var second = await watcher.ScanAsync(CancellationToken.None);
        var third = await NewWatcher(worker).ScanAsync(CancellationToken.None); // a fresh instance (restart): the marker is persistent

        Assert.Equal(6, first); // both:2, resolutionOnly:1, responseOnly:1, otherTenant:2 = 6
        Assert.Equal(0, second);
        Assert.Equal(0, third);
        var events = await BreachEventsAsync();
        Assert.Equal(6, events.Count);
        Assert.Contains(events, e => e.TicketId == both.Id && e.Kind == "response" && e.TenantId == a.TenantId && e.Number == both.Number);
        Assert.Contains(events, e => e.TicketId == both.Id && e.Kind == "resolution");
        Assert.Single(events, e => e.TicketId == resolutionOnly.Id && e.Kind == "resolution");
        Assert.DoesNotContain(events, e => e.TicketId == resolutionOnly.Id && e.Kind == "response"); // a first response exists
        Assert.Single(events, e => e.TicketId == responseOnly.Id && e.Kind == "response");
        Assert.Equal(2, events.Count(e => e.TicketId == otherTenant.Id && e.TenantId == b.TenantId)); // its own tenant, never A's
        Assert.Equal(6, await ReadAsync(db => db.SlaBreaches.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task Concurrent_instances_and_many_cycles_emit_each_ticket_and_kind_exactly_once()
    {
        var a = await SeedCompanyAsync("sla-conc-a");
        var b = await SeedCompanyAsync("sla-conc-b");
        for (var i = 0; i < 15; i++)
        {
            await AddTicketAsync(a, Overdue);
            await AddTicketAsync(b, Overdue);
        }

        await using var worker = await StartWorkerAsync(NoWatcher, consumers: false, dispatcher: false);
        var watchers = Enumerable.Range(0, 4).Select(_ => NewWatcher(worker)).ToList();

        var results = await Task.WhenAll(watchers.Concat(watchers).Select(w => Task.Run(() => w.ScanAsync(CancellationToken.None))));

        Assert.Equal(60, results.Sum()); // 30 tickets x 2 kinds, whoever got there first
        var events = await BreachEventsAsync();
        Assert.Equal(60, events.Count);
        Assert.Equal(60, events.Select(e => (e.TicketId, e.Kind)).Distinct().Count());
        Assert.Equal(60, await ReadAsync(db => db.SlaBreaches.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0, await watchers[0].ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_background_loop_escalates_an_overdue_ticket_end_to_end_and_only_once()
    {
        var c = await SeedCompanyAsync("sla-e2e");
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Priority = Priority.Normal; t.AssigneeId = c.Agent.Id; });
        // Real loop (1 s), dispatcher, broker, EscalationConsumer, e-mail: nothing replaced.
        await using var worker = await StartWorkerAsync(new() { ["Sla:WatchInterval"] = "00:00:01" });

        await Poll.UntilAsync(() => worker.Email.Sent.Count >= 2, "escalation e-mails", 30);
        await Task.Delay(3500); // three more cycles: nothing more may happen

        var reloaded = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(Priority.High, reloaded.Priority);
        Assert.NotNull(reloaded.EscalatedAt);
        Assert.Equal(1, await ReadAsync(db => db.TicketEvents.IgnoreQueryFilters().CountAsync(e => e.TicketId == ticket.Id && e.Type == TicketEventType.PriorityChanged)));
        Assert.Equal(new[] { c.Admin.Email, c.Admin2.Email }.Order(), worker.Email.Sent.Select(m => m.To).Order());
        Assert.Equal(2, await ReadAsync(db => db.SlaBreaches.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    // ---- EscalationConsumer ----

    [Theory]
    [InlineData(Priority.Low, Priority.Normal)]
    [InlineData(Priority.Normal, Priority.High)]
    [InlineData(Priority.High, Priority.Urgent)]
    [InlineData(Priority.Urgent, Priority.Urgent)]
    public async Task Escalation_raises_the_priority_one_step_and_never_beyond_urgent(Priority from, Priority expected)
    {
        var c = await SeedCompanyAsync("esc-" + from.ToString().ToLowerInvariant());
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Priority = from; t.Title = "Coupure <b>réseau</b>"; });
        var before = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Breached(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("EscalationConsumer", message.MessageId);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(expected, after.Priority);
        Assert.NotNull(after.EscalatedAt);
        Assert.NotEqual(before.RowVersion, after.RowVersion);
        // Deadlines recomputed from the creation date with the SLA of the new priority.
        var policy = SlaPolicy.CreateDefaults(c.TenantId).Single(p => p.Priority == expected);
        Assert.Equal(before.CreatedAt.AddMinutes(policy.FirstResponseMinutes), after.ResponseDueAt);
        Assert.Equal(before.CreatedAt.AddMinutes(policy.ResolutionMinutes), after.ResolutionDueAt);

        var audit = Assert.Single(await ReadAsync(db => db.TicketEvents.IgnoreQueryFilters().Where(e => e.TicketId == ticket.Id).ToListAsync()));
        Assert.Equal(TicketEventType.PriorityChanged, audit.Type);
        Assert.Null(audit.ActorId);
        Assert.Equal(from.ToString(), audit.OldValue);
        Assert.Equal(expected.ToString(), audit.NewValue);

        // Active TenantAdmins of this company only: not the inactive one, not the agents, not the requester.
        Assert.Equal(new[] { c.Admin.Email, c.Admin2.Email }.Order(), worker.Email.Sent.Select(m => m.To).Order());
        Assert.All(worker.Email.Sent, m =>
        {
            Assert.Equal($"[#{ticket.Number}] Ticket escaladé : échéance SLA dépassée", m.Subject);
            Assert.Contains("Coupure &lt;b&gt;r", m.Html);
            Assert.DoesNotContain("<b>", m.Html);
        });
    }

    [Fact]
    public async Task A_replayed_or_second_kind_event_never_escalates_twice()
    {
        var c = await SeedCompanyAsync("esc-once");
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Priority = Priority.Low; });
        await using var worker = await StartWorkerAsync(NoWatcher);
        var response = Breached(c.TenantId, ticket, "response");
        var resolution = Breached(c.TenantId, ticket, "resolution");

        await PublishAsync(response);
        await WaitProcessedAsync("EscalationConsumer", response.MessageId);
        await PublishAsync(response); // redelivery of the same message
        await WaitDuplicatesAsync(worker, 1);
        await PublishAsync(resolution); // the other kind, another message: EscalatedAt is the guard
        await WaitProcessedAsync("EscalationConsumer", resolution.MessageId);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(Priority.Normal, after.Priority);
        Assert.Equal(1, await ReadAsync(db => db.TicketEvents.IgnoreQueryFilters().CountAsync(e => e.TicketId == ticket.Id)));
        Assert.Equal(2, worker.Email.Sent.Count); // one per admin, once
    }

    [Fact]
    public async Task Racing_escalations_raise_the_priority_once_concurrency_conflicts_are_retried_not_lost()
    {
        var c = await SeedCompanyAsync("esc-race");
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Priority = Priority.Low; });
        await using var worker = await StartWorkerAsync(NoWatcher);

        var messages = Enumerable.Range(0, 8).Select(i => Breached(c.TenantId, ticket, i % 2 == 0 ? "response" : "resolution")).ToList();
        await Task.WhenAll(messages.Select(m => PublishAsync(m)));
        foreach (var m in messages)
        {
            await WaitProcessedAsync("EscalationConsumer", m.MessageId, 40);
        }

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(Priority.Normal, after.Priority); // one step, not eight
        Assert.Equal(1, await ReadAsync(db => db.TicketEvents.IgnoreQueryFilters().CountAsync(e => e.TicketId == ticket.Id)));
        Assert.Equal(2, worker.Email.Sent.Count);
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Theory]
    [InlineData(TicketStatus.Pending)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public async Task A_ticket_that_left_new_or_open_is_not_escalated(TicketStatus status)
    {
        var c = await SeedCompanyAsync("esc-" + status.ToString().ToLowerInvariant());
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Status = status; });
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Breached(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("EscalationConsumer", message.MessageId);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(Priority.Normal, after.Priority);
        Assert.Null(after.EscalatedAt);
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task Escalation_message_of_another_tenant_or_for_an_unknown_ticket_goes_to_the_dlq_without_effect()
    {
        var a = await SeedCompanyAsync("esc-a");
        var b = await SeedCompanyAsync("esc-b");
        var ticketOfA = await AddTicketAsync(a, Overdue);
        await using var worker = await StartWorkerAsync(NoWatcher);

        await PublishAsync(Breached(b.TenantId, ticketOfA));
        await PublishAsync(Breached(a.TenantId, new Ticket { Number = 5 }));
        await WaitForDlqAsync(2);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketOfA.Id));
        Assert.Equal(Priority.Normal, after.Priority);
        Assert.Null(after.EscalatedAt);
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task A_failing_escalation_is_retried_then_dead_lettered_and_leaves_the_ticket_untouched()
    {
        var c = await SeedCompanyAsync("esc-fail");
        var ticket = await AddTicketAsync(c, Overdue);
        await using var worker = await StartWorkerAsync(Failing("EscalationConsumer"));

        await PublishAsync(Breached(c.TenantId, ticket));
        await WaitForDlqAsync(1);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Null(after.EscalatedAt);
        Assert.Equal("EscalationConsumer", Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue)).Header("x-failed-consumer"));
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task A_failure_after_the_save_rolls_the_escalation_back_so_the_retry_starts_clean()
    {
        var c = await SeedCompanyAsync("esc-smtp");
        var ticket = await AddTicketAsync(c, t => { Overdue(t); t.Priority = Priority.Low; });
        await using var worker = await StartWorkerAsync(NoWatcher);
        worker.Email.FailNext = true; // the first e-mail fails after the ticket was saved: the transaction must not commit

        var message = Breached(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("EscalationConsumer", message.MessageId, 30);

        var after = await ReadAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticket.Id));
        Assert.Equal(Priority.Normal, after.Priority); // one step in total
        Assert.Equal(1, await ReadAsync(db => db.TicketEvents.IgnoreQueryFilters().CountAsync(e => e.TicketId == ticket.Id)));
        Assert.Equal(2, worker.Email.Sent.Count);
    }
}
