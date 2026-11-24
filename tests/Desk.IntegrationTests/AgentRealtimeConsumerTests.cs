using Desk.Core.Domain;
using Desk.Infrastructure.Messaging;
using Desk.Infrastructure.Messaging.Consumers;
using Desk.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.IntegrationTests;

/// <summary>
/// AgentRealtimeConsumer without SignalR: the notifier is a recorder, so what the consumer decides (group, method, payload,
/// who is told) is checked on real PostgreSQL and RabbitMQ. The hub itself is tested in <see cref="RealtimeHubTests"/>.
/// </summary>
public class AgentRealtimeConsumerTests(PostgresFixture postgres, RabbitMqFixture rabbit) : ConsumerTestBase(postgres, rabbit)
{
    private Task<TestWorker> StartRealtimeAsync(Dictionary<string, string?>? settings = null) =>
        StartWorkerAsync(settings ?? NoWatcher, configure: services =>
        {
            services.RemoveAll<ConsumerDefinition>(); // only this consumer runs: the Worker ones would also react to the same events
            services.AddSingleton<RecordingNotifier>();
            services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<RecordingNotifier>());
            services.AddDeskConsumer<AgentRealtimeConsumer>(); // hosted here only to test it: in the product it lives in the API
        });

    private static RecordingNotifier Notifier(TestWorker worker) => worker.Services.GetRequiredService<RecordingNotifier>();

    [Fact]
    public async Task A_created_ticket_is_announced_to_the_agents_group_of_its_tenant_without_its_description()
    {
        var c = await SeedCompanyAsync("rt-created");
        var ticket = await AddTicketAsync(c, t => { t.Title = "Écran noir"; t.Priority = Priority.High; t.Description = "SECRET-DESCRIPTION"; });
        await using var worker = await StartRealtimeAsync();

        var message = Created(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("AgentRealtimeConsumer", message.MessageId);

        var sent = Assert.Single(Notifier(worker).Sent);
        Assert.Equal($"agents:{c.TenantId}", sent.Group);
        Assert.Equal("ticketCreated", sent.Method);
        Assert.Equal(ticket.Id, sent.Payload.GetProperty("ticketId").GetGuid());
        Assert.Equal(ticket.Number, sent.Payload.GetProperty("number").GetInt32());
        Assert.Equal("Écran noir", sent.Payload.GetProperty("title").GetString());
        Assert.Equal("High", sent.Payload.GetProperty("priority").GetString());
        Assert.True(sent.Payload.TryGetProperty("at", out _));
        Assert.Equal(5, sent.Payload.EnumerateObject().Count());
        Assert.DoesNotContain("SECRET-DESCRIPTION", sent.Payload.GetRawText());
    }

    [Fact]
    public async Task An_assignment_goes_to_the_group_of_the_assignee_only()
    {
        var c = await SeedCompanyAsync("rt-assigned");
        var ticket = await AddTicketAsync(c);
        await using var worker = await StartRealtimeAsync();

        var message = Assigned(c.TenantId, ticket, c.Agent2, c.Admin);
        await PublishAsync(message);
        await WaitProcessedAsync("AgentRealtimeConsumer", message.MessageId);

        var sent = Assert.Single(Notifier(worker).Sent);
        Assert.Equal($"user:{c.Agent2.Id}", sent.Group);
        Assert.Equal("ticketAssigned", sent.Method);
        Assert.Equal(4, sent.Payload.EnumerateObject().Count()); // ticketId, number, title, at
        Assert.Equal(ticket.Id, sent.Payload.GetProperty("ticketId").GetGuid());
    }

    [Fact]
    public async Task Only_a_public_reply_of_the_requester_notifies_the_assignee()
    {
        var c = await SeedCompanyAsync("rt-replied");
        var ticket = await AddTicketAsync(c, t => t.AssigneeId = c.Agent.Id);
        var reply = await AddCommentAsync(c, ticket, c.Requester, Body);
        var byAgent = await AddCommentAsync(c, ticket, c.Agent2, Body);
        var note = await AddCommentAsync(c, ticket, c.Requester, Body, isInternal: true);
        var unassigned = await AddTicketAsync(c);
        var replyUnassigned = await AddCommentAsync(c, unassigned, c.Requester, Body);
        await using var worker = await StartRealtimeAsync();

        var messages = new[]
        {
            Commented(c.TenantId, ticket, byAgent), Commented(c.TenantId, ticket, note),
            Commented(c.TenantId, unassigned, replyUnassigned), Commented(c.TenantId, ticket, reply),
        };
        foreach (var m in messages)
        {
            await PublishAsync(m);
            await WaitProcessedAsync("AgentRealtimeConsumer", m.MessageId);
        }

        var sent = Assert.Single(Notifier(worker).Sent);
        Assert.Equal($"user:{c.Agent.Id}", sent.Group);
        Assert.Equal("ticketReplied", sent.Method);
        Assert.DoesNotContain(Body, sent.Payload.GetRawText());
    }

    [Fact]
    public async Task Messages_of_another_tenant_or_naming_a_foreign_user_go_to_the_dlq_and_send_nothing()
    {
        var a = await SeedCompanyAsync("rt-a");
        var b = await SeedCompanyAsync("rt-b");
        var ticketOfA = await AddTicketAsync(a);
        var commentOfA = await AddCommentAsync(a, ticketOfA, a.Requester, Body);
        await using var worker = await StartRealtimeAsync();

        await PublishAsync(Created(b.TenantId, ticketOfA));
        await PublishAsync(Assigned(b.TenantId, ticketOfA, a.Agent, a.Admin));
        await PublishAsync(Assigned(a.TenantId, ticketOfA, b.Agent, a.Admin)); // assignee of another company
        await PublishAsync(Commented(b.TenantId, ticketOfA, commentOfA));
        await WaitForDlqAsync(4);

        Assert.Empty(Notifier(worker).Sent);
    }

    [Fact]
    public async Task Realtime_notification_is_idempotent_and_retried_then_dead_lettered_on_failure()
    {
        var c = await SeedCompanyAsync("rt-idem");
        var ticket = await AddTicketAsync(c);
        await using (var worker = await StartRealtimeAsync())
        {
            var message = Created(c.TenantId, ticket);
            await PublishAsync(message);
            await WaitProcessedAsync("AgentRealtimeConsumer", message.MessageId);
            await PublishAsync(message);
            await WaitDuplicatesAsync(worker, 1);
            Assert.Single(Notifier(worker).Sent);
        }

        await using var failing = await StartRealtimeAsync(Failing("AgentRealtimeConsumer"));
        await PublishAsync(Created(c.TenantId, ticket));
        await WaitForDlqAsync(1);
        Assert.Equal("AgentRealtimeConsumer", Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue)).Header("x-failed-consumer"));
        Assert.Empty(Notifier(failing).Sent);
    }
}
