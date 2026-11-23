using System.Text;
using System.Text.Json;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>Outbox, publication, consumption, retries, DLQ, idempotence and tenant isolation, on real PostgreSQL and RabbitMQ.</summary>
public class MessagingReliabilityTests(PostgresFixture postgres, RabbitMqFixture rabbit) : MessagingTestBase(postgres, rabbit)
{
    private static Dictionary<string, string?> FailEmailAck => new() { ["Messaging:FailConsumer"] = "EmailAckConsumer" };

    [Fact]
    public async Task Topology_is_declared_idempotently_with_one_delay_queue_per_tier()
    {
        await using var first = await StartWorkerAsync();
        await using var second = await StartWorkerAsync(); // Declaring again changes nothing and does not fail.

        Assert.True(await Broker.ExistsAsync(MessagingNames.EventsExchange, exchange: true));
        Assert.True(await Broker.ExistsAsync(MessagingNames.RetryExchange, exchange: true));
        Assert.True(await Broker.ExistsAsync(MessagingNames.DlxExchange, exchange: true));
        Assert.True(await Broker.ExistsAsync(MessagingNames.DlqQueue));
        Assert.True(await Broker.ExistsAsync("desk.email-ack"));
        foreach (var label in new[] { "400ms", "800ms", "1200ms" })
        {
            Assert.True(await Broker.ExistsAsync($"desk.email-ack.retry.{label}"), label);
        }

        Assert.Equal(2u, await Broker.ConsumersAsync("desk.email-ack"));
    }

    [Fact]
    public async Task An_outbox_message_is_published_with_confirms_then_consumed_once()
    {
        var ticket = await SeedTicketAsync("pub");
        await using var worker = await StartWorkerAsync();
        var envelope = TicketCreated(ticket, correlationId: "corr-e2e-1");

        await AddToOutboxAsync(envelope);

        await Poll.UntilAsync(() => worker.Email.Sent.Count >= 1, "acknowledgement e-mail");
        var mail = Assert.Single(worker.Email.Sent);
        Assert.Equal(ticket.RequesterEmail, mail.To);
        Assert.Contains("Votre demande a bien été reçue", mail.Subject);
        Assert.Contains(ticket.Title, mail.Body);
        Assert.Contains(ticket.Title, mail.Html);

        // The dispatcher marked the row; the consumer recorded (MessageId, Consumer).
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.OutboxMessage>(db => db.OutboxMessages.Where(o => o.DispatchedAt != null && o.Id == envelope.MessageId)));
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages.Where(p => p.MessageId == envelope.MessageId && p.Consumer == "EmailAckConsumer")));
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
        Assert.Contains(worker.Logs.Lines, l => l.Contains("published", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Published_messages_are_persistent_json_with_the_four_mandatory_headers()
    {
        var ticket = await SeedTicketAsync("hdr");
        await using var worker = await StartWorkerAsync(consumers: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        var envelope = TicketCreated(ticket, correlationId: "corr-headers");

        await AddToOutboxAsync(envelope);

        await Poll.UntilAsync(async () => await Broker.CountAsync("test.sink") == 1, "message in the sink queue");
        var message = Assert.Single(await Broker.PeekAsync("test.sink"));
        Assert.Equal(envelope.MessageId.ToString(), message.Header("message-id"));
        Assert.Equal("corr-headers", message.Header("correlation-id"));
        Assert.Equal(ticket.TenantId.ToString(), message.Header("tenant-id"));
        Assert.Equal("0", message.Header("attempt"));

        using var json = JsonDocument.Parse(message.Body);
        Assert.Equal(envelope.MessageId, json.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal("ticket.created", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(ticket.TicketId, json.RootElement.GetProperty("payload").GetProperty("ticketId").GetGuid());

        await using var channel = await Broker.ChannelAsync();
        var got = await channel.BasicGetAsync("test.sink", autoAck: true);
        Assert.NotNull(got);
        Assert.Equal(RabbitMQ.Client.DeliveryModes.Persistent, got.BasicProperties.DeliveryMode);
        Assert.Equal("application/json", got.BasicProperties.ContentType);
    }

    [Fact]
    public async Task A_failing_consumer_is_retried_three_times_with_growing_delays_then_dead_lettered_with_the_reason()
    {
        var ticket = await SeedTicketAsync("retry");
        await using var worker = await StartWorkerAsync(FailEmailAck);
        var envelope = TicketCreated(ticket, correlationId: "corr-retry");
        var clock = StartTimer();

        await PublishAsync(envelope);
        await WaitForDlqAsync(1);

        // 0.4 s + 0.8 s + 1.2 s of delays configured for the tests: the three retries really waited.
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.4), $"dead-lettered after {clock.Elapsed}, before the retry delays elapsed");
        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Equal("EmailAckConsumer", dead.Header("x-failed-consumer"));
        Assert.Contains("Failure forced by Messaging:FailConsumer", dead.Header("x-failure-reason"));
        Assert.Equal("retries-exhausted", dead.Header("x-failure-kind"));
        Assert.Equal("desk.events", dead.Header("x-original-exchange"));
        Assert.Equal("ticket.created", dead.Header("x-original-routing-key"));
        Assert.Equal("3", dead.Header("attempt"));
        Assert.Equal(envelope.MessageId.ToString(), dead.Header("message-id"));
        Assert.Equal("corr-retry", dead.Header("correlation-id"));
        Assert.Equal(ticket.TenantId.ToString(), dead.Header("tenant-id"));
        Assert.NotEmpty(dead.Header("x-failed-at"));
        Assert.Equal(envelope.ToJson(), dead.Body);

        Assert.Empty(worker.Email.Sent);
        Assert.Equal(0, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages));
        Assert.Equal(3, worker.Logs.Lines.Count(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("retry in", StringComparison.Ordinal)));
        Assert.Equal(1, worker.Logs.Lines.Count(l => l.Contains("sent to the DLQ", StringComparison.Ordinal)));
        // Nothing is left behind in the delay queues.
        Assert.Equal(0u, await Broker.CountAsync("desk.email-ack.retry.400ms"));
        Assert.Equal(0u, await Broker.CountAsync("desk.email-ack"));
    }

    [Fact]
    public async Task A_message_that_fails_once_is_retried_and_then_processed()
    {
        var ticket = await SeedTicketAsync("flaky");
        await using var worker = await StartWorkerAsync();
        worker.Email.FailNext = true; // SMTP down for the first attempt only.

        await PublishAsync(TicketCreated(ticket));

        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "e-mail after one retry");
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages));
    }

    [Theory]
    [InlineData("this is not json", "Invalid JSON")]
    [InlineData("""{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "Missing tenantId")]
    [InlineData("""{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"00000000-0000-0000-0000-000000000000","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "Missing tenantId")]
    public async Task A_poisoned_message_goes_straight_to_the_dlq_without_any_retry(string body, string reason)
    {
        // Default delays (5 s, 30 s, 2 min): a retry could not reach the DLQ within the wait below.
        await using var worker = await StartWorkerAsync(new Dictionary<string, string?>
        {
            ["Messaging:RetryDelays:0"] = "00:00:05",
            ["Messaging:RetryDelays:1"] = "00:00:30",
            ["Messaging:RetryDelays:2"] = "00:02:00",
        });
        var clock = StartTimer();

        await Broker.PublishAsync(MessagingNames.EventsExchange, EventTypes.TicketCreated, Encoding.UTF8.GetBytes(body));
        await WaitForDlqAsync(1, seconds: 4);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Equal("poison", dead.Header("x-failure-kind"));
        Assert.Contains(reason, dead.Header("x-failure-reason"));
        Assert.Equal("EmailAckConsumer", dead.Header("x-failed-consumer"));
        Assert.Equal("desk.events", dead.Header("x-original-exchange"));
        Assert.Equal("ticket.created", dead.Header("x-original-routing-key"));
        Assert.Equal("0", dead.Header("attempt"));
        Assert.Equal(body, dead.Body);
        Assert.Empty(worker.Email.Sent);
        Assert.Equal(0u, await Broker.CountAsync("desk.email-ack.retry.5s"));
    }

    [Fact]
    public async Task A_tenant_header_that_contradicts_the_envelope_is_poison()
    {
        var ticket = await SeedTicketAsync("tamper");
        await using var worker = await StartWorkerAsync();

        await Broker.PublishAsync(MessagingNames.EventsExchange, EventTypes.TicketCreated, TicketCreated(ticket).ToUtf8Json(),
            tenantId: Guid.NewGuid().ToString());

        await WaitForDlqAsync(1, seconds: 10);
        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Contains("tenant-id header differs", dead.Header("x-failure-reason"));
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task The_same_message_published_twice_has_a_single_effect()
    {
        var ticket = await SeedTicketAsync("idem");
        await using var worker = await StartWorkerAsync();
        var envelope = TicketCreated(ticket);

        await PublishAsync(envelope);
        await PublishAsync(envelope);

        await Poll.UntilAsync(() => worker.Logs.Lines.Any(l => l.Contains("Duplicate message", StringComparison.Ordinal)), "duplicate detected");
        await Poll.UntilAsync(async () => await Broker.CountAsync("desk.email-ack") == 0, "queue drained");
        Assert.Single(worker.Email.Sent);
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages));
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task Duplicates_delivered_at_the_same_time_still_have_a_single_effect()
    {
        var ticket = await SeedTicketAsync("burst");
        await using var worker = await StartWorkerAsync();
        var envelope = TicketCreated(ticket);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PublishAsync(envelope)));

        await Poll.UntilAsync(() => worker.Logs.Lines.Count(l => l.Contains("Duplicate message", StringComparison.Ordinal)) == 7, "7 duplicates ignored");
        Assert.Single(worker.Email.Sent);
    }

    [Fact]
    public async Task The_same_message_is_processed_once_per_consumer()
    {
        var ticket = await SeedTicketAsync("two");
        await using var worker = await StartWorkerAsync(probe: true);
        var envelope = TicketCreated(ticket);

        await PublishAsync(envelope);

        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1 && worker.Probe.Seen.Count == 1, "both consumers processed it");
        Assert.Equal(2, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages.Where(p => p.MessageId == envelope.MessageId)));
    }

    [Fact]
    public async Task A_consumer_works_in_the_tenant_of_the_message_and_never_reads_another_one()
    {
        var a = await SeedTicketAsync("tenant-a");
        var b = await SeedTicketAsync("tenant-b");
        await using var worker = await StartWorkerAsync(probe: true);

        // Tenant B's message points at tenant A's ticket: invisible from B, so nothing is sent and the message is abandoned.
        var crossTenant = TicketCreated(a, tenantId: b.TenantId);
        await PublishAsync(crossTenant);
        await WaitForDlqAsync(1, seconds: 10);

        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Equal("EmailAckConsumer", dead.Header("x-failed-consumer"));
        Assert.Equal("poison", dead.Header("x-failure-kind"));
        Assert.Contains("not found in tenant " + b.TenantId, dead.Header("x-failure-reason"));
        Assert.Empty(worker.Email.Sent);

        // The probe ran in B's scope: the tenant is the message's, and A's ticket is filtered out.
        Assert.Contains(worker.Probe.Seen, s => s.Tenant == b.TenantId && s.MessageTenant == b.TenantId && s.VisibleTickets == 0);

        // A regular message of tenant A is served in tenant A.
        await PublishAsync(TicketCreated(a));
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "e-mail of tenant A");
        Assert.Equal(a.RequesterEmail, worker.Email.Sent.Single().To);
        Assert.Contains(worker.Probe.Seen, s => s.Tenant == a.TenantId && s.VisibleTickets == 1);
    }

    [Fact]
    public async Task Concurrent_dispatchers_never_publish_the_same_outbox_message_twice()
    {
        await using var seed = await StartWorkerAsync(consumers: false, dispatcher: false); // declares the topology
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);

        var ticket = await SeedTicketAsync("conc");
        var envelopes = Enumerable.Range(0, 300).Select(i => TicketCreated(ticket, correlationId: "c" + i)).ToArray();
        await AddToOutboxAsync(envelopes);

        var settings = new Dictionary<string, string?> { ["Messaging:OutboxBatchSize"] = "20", ["Messaging:OutboxPollInterval"] = "00:00:00.1" };
        await using var d1 = await StartWorkerAsync(settings, consumers: false);
        await using var d2 = await StartWorkerAsync(settings, consumers: false);
        await using var d3 = await StartWorkerAsync(settings, consumers: false);

        await Poll.UntilAsync(async () => await CountAsync<Desk.Core.Domain.OutboxMessage>(db => db.OutboxMessages.Where(o => o.DispatchedAt == null)) == 0, "outbox drained", 60);
        await Poll.UntilAsync(async () => await Broker.CountAsync("test.sink") >= 300, "300 messages in the sink", 30);
        await Task.Delay(500); // a duplicate would show up late

        var received = (await Broker.PeekAsync("test.sink")).Select(m => m.Header("message-id")).ToList();
        Assert.Equal(300, received.Count);
        Assert.Equal(300, received.Distinct().Count());
        Assert.Equal(envelopes.Select(e => e.MessageId.ToString()).Order(), received.Order());
    }

    [Fact]
    public async Task Outbox_rows_written_before_the_worker_starts_are_published_once_it_runs()
    {
        // Fresh vhost, no topology yet: the dispatcher declares it before publishing, nothing is lost.
        var ticket = await SeedTicketAsync("late");
        var envelope = TicketCreated(ticket);
        await AddToOutboxAsync(envelope);

        await using var worker = await StartWorkerAsync(consumers: true);

        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "e-mail for a row written before the Worker started");
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.OutboxMessage>(db => db.OutboxMessages.Where(o => o.DispatchedAt != null)));
    }
}
