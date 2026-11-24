using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>
/// What happens when things break: a broker that stops or refuses, a connection closed from the server side, a lost
/// acknowledgement, a Worker that is stopped while a handler runs, unusable outbox rows. The guarantee under test:
/// nothing is lost (at least once) and nothing happens twice (idempotence).
/// </summary>
public class MessagingFailureTests(PostgresFixture postgres, RabbitMqFixture rabbit) : MessagingTestBase(postgres, rabbit)
{
    private static readonly Dictionary<string, string?> FastPolling = new() { ["Messaging:OutboxPollInterval"] = "00:00:00.2" };

    private Task<int> PendingAsync(Guid id) =>
        CountAsync<OutboxMessage>(db => db.OutboxMessages.Where(o => o.Id == id && o.DispatchedAt == null));

    private Task<int> DispatchedAsync(Guid id) =>
        CountAsync<OutboxMessage>(db => db.OutboxMessages.Where(o => o.Id == id && o.DispatchedAt != null));

    private Task WaitDispatchedAsync(Guid id, int seconds = 30) =>
        Poll.UntilAsync(async () => await DispatchedAsync(id) == 1, "outbox row dispatched", seconds);

    [Fact]
    public async Task A_publication_failure_leaves_the_outbox_row_pending_and_it_is_published_once_the_broker_accepts_again()
    {
        var ticket = await SeedTicketAsync("pub-fail");
        await using var worker = await StartWorkerAsync(FastPolling, consumers: false, configure: FaultyPublisher.Install(out var get));
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        var faulty = get();
        faulty.FailWhen = m => m.Exchange == MessagingNames.EventsExchange;
        var envelope = TicketCreated(ticket);

        await AddToOutboxAsync(envelope);
        await Poll.UntilAsync(() => faulty.Failures >= 2, "two refused publications");

        Assert.Equal(1, await PendingAsync(envelope.MessageId)); // not lost, not marked
        Assert.Equal(0u, await Broker.CountAsync("test.sink"));

        faulty.FailWhen = _ => false; // the broker accepts again
        await WaitDispatchedAsync(envelope.MessageId);
        Assert.Equal(1u, await Broker.CountAsync("test.sink"));
    }

    [Fact]
    public async Task An_unroutable_event_is_never_marked_dispatched_and_is_published_once_a_queue_is_bound()
    {
        var ticket = await SeedTicketAsync("unroutable");
        await using var worker = await StartWorkerAsync(FastPolling, consumers: false);
        var envelope = MessageEnvelope.Create(Guid.NewGuid(), "ticket.nobody-listens", ticket.TenantId, DateTimeOffset.UtcNow, "c", new { ticketId = ticket.TicketId });

        await AddToOutboxAsync(envelope);
        await Poll.UntilAsync(() => worker.Logs.Lines.Any(l => l.Contains("is unroutable", StringComparison.Ordinal)), "unroutable warning", 8);
        await Task.Delay(1000); // several more polls

        Assert.Equal(1, await PendingAsync(envelope.MessageId));
        Assert.Equal(1, worker.Logs.Lines.Count(l => l.Contains("is unroutable", StringComparison.Ordinal))); // logged once, not at every poll

        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, "ticket.nobody-listens");
        await WaitDispatchedAsync(envelope.MessageId);
        Assert.Equal(1u, await Broker.CountAsync("test.sink"));
    }

    [Fact]
    public async Task Rows_with_an_invalid_envelope_are_marked_failed_once_and_never_block_the_others()
    {
        var ticket = await SeedTicketAsync("invalid");
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        var valid = TicketCreated(ticket);
        await using (var db = OpenDb(out var scope))
        {
            using var _ = scope;
            // More broken rows than a batch (50), all older than the valid one: they would hold the head of every batch.
            for (var i = 0; i < 60; i++)
            {
                db.OutboxMessages.Add(new OutboxMessage { Type = "ticket.created", Payload = """{"broken":true}""", CreatedAt = valid.OccurredAt.AddMinutes(-10).AddSeconds(i) });
            }

            await db.SaveChangesAsync();
        }

        await AddToOutboxAsync(valid);
        await using var worker = await StartWorkerAsync(FastPolling, consumers: false);

        await WaitDispatchedAsync(valid.MessageId);
        await Task.Delay(800); // more polls: the broken rows must not be logged again
        Assert.Equal(60, await CountAsync<OutboxMessage>(db => db.OutboxMessages.Where(o => o.FailedAt != null && o.DispatchedAt == null && o.FailureReason!.Contains("Missing"))));
        Assert.Equal(60, worker.Logs.Lines.Count(l => l.Contains("marked failed", StringComparison.Ordinal)));
        Assert.Equal(1u, await Broker.CountAsync("test.sink"));
    }

    [Fact]
    public async Task A_refused_retry_publication_requeues_the_delivery_and_the_effect_happens_once()
    {
        var ticket = await SeedTicketAsync("retry-nack");
        await using var worker = await StartWorkerAsync(configure: FaultyPublisher.Install(out var get));
        var faulty = get();
        faulty.FailWhen = m => m.Exchange == MessagingNames.RetryExchange && faulty.Failures == 0; // refuse the first one only
        worker.Email.FailNext = true; // SMTP down once: the handler fails, the retry publication is refused too

        await PublishAsync(TicketCreated(ticket));

        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "e-mail after the requeued delivery", 30);
        Assert.Equal(1, faulty.Failures);
        Assert.Contains(worker.Logs.Lines, l => l.Contains("could not be settled; requeued", StringComparison.Ordinal));
        Assert.Equal(1, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
        await Task.Delay(1000);
        Assert.Single(worker.Email.Sent);
    }

    [Fact]
    public async Task A_lost_acknowledgement_is_redelivered_and_the_duplicate_has_no_second_effect()
    {
        var ticket = await SeedTicketAsync("lost-ack");
        await using var worker = await StartWorkerAsync();
        var gate = new TaskCompletionSource();
        worker.Email.Hold = gate.Task; // the handler is in flight when the connection is cut

        await PublishAsync(TicketCreated(ticket));
        await Poll.UntilAsync(() => worker.Email.Entered == 1, "handler in flight");
        Assert.Equal(1, await Broker.CloseConnectionsAsync("deskflow-consumer"));
        await Poll.UntilAsync(() => worker.Logs.Lines.Any(l => l.Contains("lost its subscription", StringComparison.Ordinal)), "subscription loss detected");
        gate.SetResult(); // the handler finishes and commits; its acknowledgement goes nowhere

        await Poll.UntilAsync(() => worker.Logs.Lines.Any(l => l.Contains("Duplicate message", StringComparison.Ordinal)), "redelivery ignored as a duplicate");
        Assert.Single(worker.Email.Sent);
        Assert.Equal(1, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
        await Poll.UntilAsync(async () => await Broker.CountAsync("desk.email-ack") == 0 && await Broker.ConsumersAsync("desk.email-ack") == 1, "re-subscribed and drained");
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task A_consumer_whose_connection_the_server_closed_subscribes_again_and_reports_it_in_readiness()
    {
        var ticket = await SeedTicketAsync("server-close");
        await using var worker = await StartWorkerAsync();
        var readiness = worker.Services.GetRequiredService<ConsumerReadiness>();

        Assert.Equal(1, await Broker.CloseConnectionsAsync("deskflow-consumer"));
        await Poll.UntilAsync(() => readiness.NotSubscribed.Count == 1, "readiness reports the lost consumer", 10);
        await Poll.UntilAsync(() => readiness.NotSubscribed.Count == 0, "consumer subscribed again", 30);

        await PublishAsync(TicketCreated(ticket));
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "message consumed after re-subscription");
    }

    [Fact]
    public async Task Stopping_the_worker_lets_the_running_handler_finish_and_acknowledges_its_message()
    {
        var ticket = await SeedTicketAsync("graceful");
        var worker = await StartWorkerAsync();
        var email = worker.Email;
        var gate = new TaskCompletionSource();
        email.Hold = gate.Task;

        await PublishAsync(TicketCreated(ticket));
        await Poll.UntilAsync(() => email.Entered == 1, "handler in flight");
        var stopping = worker.DisposeAsync().AsTask();
        await Task.Delay(800);

        Assert.False(stopping.IsCompleted, "the Worker must wait for the handler in flight");
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(email.Sent);
        Assert.Equal(1, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
        Assert.Equal(0u, await Broker.CountAsync("desk.email-ack")); // acknowledged, not requeued
        Assert.Equal(0u, await Broker.ConsumersAsync("desk.email-ack"));
    }

    [Fact]
    public async Task A_handler_that_outlives_the_shutdown_grace_is_interrupted_and_its_message_is_kept()
    {
        var ticket = await SeedTicketAsync("grace-over");
        var worker = await StartWorkerAsync(new Dictionary<string, string?> { ["Messaging:ShutdownTimeout"] = "00:00:00.5" });
        var email = worker.Email;
        email.Hold = new TaskCompletionSource().Task; // never finishes

        await PublishAsync(TicketCreated(ticket));
        await Poll.UntilAsync(() => email.Entered == 1, "handler in flight");

        await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(email.Sent);
        Assert.Equal(0, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
        await Poll.UntilAsync(async () => await Broker.CountAsync("desk.email-ack") == 1, "message back in its queue");
    }

    [Fact]
    public async Task A_broker_restart_is_survived_by_the_dispatcher_the_publisher_and_the_consumers()
    {
        var ticket = await SeedTicketAsync("broker-restart");
        await using var worker = await StartWorkerAsync(FastPolling);
        var first = TicketCreated(ticket);
        await AddToOutboxAsync(first);
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "baseline e-mail");

        // The broker goes away. The business transaction does not need it: the event is written to the outbox anyway.
        await Rabbit.StopBrokerAsync();
        var during = TicketCreated(ticket);
        await AddToOutboxAsync(during);
        await Task.Delay(3000);
        Assert.Equal(1, await PendingAsync(during.MessageId));
        Assert.Single(worker.Email.Sent);

        // It comes back (durable queues and the vhost survive on its disk): the same Worker process catches up by itself.
        await Rabbit.StartBrokerAsync();
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 2, "event published and consumed after the restart", 90);
        await WaitDispatchedAsync(during.MessageId);

        // And it keeps working: new events flow through the recovered publisher and consumer.
        var after = TicketCreated(ticket);
        await AddToOutboxAsync(after);
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 3, "event after the restart", 30);

        Assert.True(await Broker.ExistsAsync("desk.email-ack"));
        Assert.Equal(1u, await Broker.ConsumersAsync("desk.email-ack")); // no second subscription left behind
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
        Assert.Empty(worker.Services.GetRequiredService<ConsumerReadiness>().NotSubscribed);
        Assert.Equal(3, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
    }

    [Fact]
    public async Task A_message_id_header_that_contradicts_the_envelope_is_poison()
    {
        var ticket = await SeedTicketAsync("id-tamper");
        await using var worker = await StartWorkerAsync();

        await Broker.PublishAsync(MessagingNames.EventsExchange, EventTypes.TicketCreated, TicketCreated(ticket).ToUtf8Json(),
            tenantId: ticket.TenantId.ToString(), messageId: Guid.NewGuid().ToString());

        await WaitForDlqAsync(1, seconds: 10);
        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Contains("message-id header differs", dead.Header("x-failure-reason"));
        Assert.Equal("poison", dead.Header("x-failure-kind"));
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task A_broker_cut_in_the_middle_of_replay_dlq_exits_with_code_3_and_loses_nothing()
    {
        var ticket = await SeedTicketAsync("replay-cut");
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        const int total = 3000;
        await Broker.FillQueueAsync(MessagingNames.DlqQueue, total, _ =>
        {
            var envelope = TicketCreated(ticket);
            return (envelope.ToUtf8Json(), new Dictionary<string, object?>
            {
                [MessagingNames.MessageId] = envelope.MessageId.ToString(),
                [MessagingNames.CorrelationId] = envelope.CorrelationId,
                [MessagingNames.TenantId] = envelope.TenantId.ToString(),
                [MessagingNames.Attempt] = 3,
                [MessagingNames.FailedConsumer] = "EmailAckConsumer",
                [MessagingNames.FailureReason] = "SmtpCommandException: mailbox unavailable",
                [MessagingNames.OriginalExchange] = MessagingNames.EventsExchange,
                [MessagingNames.OriginalRoutingKey] = envelope.Type,
            });
        });
        await using var proxy = new TcpProxy(Broker.Uri);
        var env = new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = ConnectionString,
            ["ConnectionStrings__RabbitMq"] = proxy.Through(Broker.Uri),
        };

        var run = WorkerProcess.RunAsync(["replay-dlq"], env, timeoutSeconds: 120);
        await Poll.UntilAsync(async () => await Broker.CountAsync("test.sink") >= 5, "replay under way");
        proxy.Cut();
        var result = await run;

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Bilan", result.Output);
        Assert.Contains("RabbitMQ interrompu pendant le rejeu", result.Error);
        Assert.DoesNotContain("Unhandled exception", result.Output + result.Error);
        // Whatever was not confirmed and acknowledged is still in the DLQ: nothing lost (a duplicate is possible, not a loss).
        Assert.True(await Broker.CountAsync("test.sink") >= 5);
        await Poll.UntilAsync(async () => await Broker.CountAsync(MessagingNames.DlqQueue) + await Broker.CountAsync("test.sink") >= total, "no message lost", 30);
        Assert.True(await Broker.CountAsync(MessagingNames.DlqQueue) > 0, "the replay was really interrupted");
    }
}
