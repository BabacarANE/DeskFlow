using System.Text;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;

namespace Desk.IntegrationTests;

/// <summary>
/// <c>dotnet Desk.Worker.dll replay-dlq</c>, run as a real process against the test broker. The messages reach the DLQ
/// the way they do in production (a failing consumer, a poisoned body), or are written there directly when the path
/// does not matter.
/// </summary>
public class ReplayDlqTests(PostgresFixture postgres, RabbitMqFixture rabbit) : MessagingTestBase(postgres, rabbit)
{
    private Dictionary<string, string> Env() => new()
    {
        ["ConnectionStrings__Postgres"] = ConnectionString,
        ["ConnectionStrings__RabbitMq"] = Broker.Uri,
    };

    private static void OutputContains(ProcessResult result, string expected) =>
        Assert.True(result.Output.Contains(expected, StringComparison.Ordinal),
            $"Expected '{expected}' in the output:{Environment.NewLine}{result.Output}{Environment.NewLine}stderr:{Environment.NewLine}{result.Error}");

    private Task<ProcessResult> ReplayAsync(params string[] arguments) => WorkerProcess.RunAsync(["replay-dlq", .. arguments], Env());

    /// <summary>A message abandoned by EmailAckConsumer, as the pipeline writes it.</summary>
    private Task PutInDlqAsync(MessageEnvelope envelope, int attempt = 3) =>
        Broker.PublishAsync(MessagingNames.DlxExchange, MessagingNames.DlqRoutingKey, envelope.ToUtf8Json(), attempt,
            envelope.TenantId.ToString(), envelope.MessageId.ToString(), new Dictionary<string, object?>
            {
                [MessagingNames.CorrelationId] = envelope.CorrelationId,
                [MessagingNames.FailedConsumer] = "EmailAckConsumer",
                [MessagingNames.FailureReason] = "SmtpCommandException: mailbox unavailable",
                [MessagingNames.FailureKind] = "retries-exhausted",
                [MessagingNames.OriginalExchange] = MessagingNames.EventsExchange,
                [MessagingNames.OriginalRoutingKey] = envelope.Type,
            });

    [Fact]
    public async Task A_message_dead_lettered_by_a_failing_consumer_is_replayed_and_finally_processed()
    {
        var ticket = await SeedTicketAsync("replay-full");
        var envelope = TicketCreated(ticket, correlationId: "corr-replay-full");

        // 1. The consumer is forced to fail: three retries, then the DLQ.
        var failing = await StartWorkerAsync(new Dictionary<string, string?> { ["Messaging:FailConsumer"] = "EmailAckConsumer" });
        await PublishAsync(envelope);
        await WaitForDlqAsync(1);
        await failing.DisposeAsync();

        // 2. The cause is fixed (no more forced failure) and the command is run.
        await using var healthy = await StartWorkerAsync();
        var result = await ReplayAsync();

        Assert.Equal(0, result.ExitCode);
        OutputContains(result, envelope.MessageId.ToString());
        OutputContains(result, "EmailAckConsumer");
        OutputContains(result, "Failure forced by Messaging:FailConsumer");
        OutputContains(result, "1 message(s) lu(s), 1 rejoué(s), 0 ignoré(s), 0 en échec");

        // 3. The message is processed, and only then is the DLQ empty.
        await Poll.UntilAsync(() => healthy.Email.Sent.Count == 1, "e-mail after the replay");
        Assert.Equal(ticket.RequesterEmail, healthy.Email.Sent.Single().To);
        await Poll.UntilAsync(async () => await Broker.CountAsync(MessagingNames.DlqQueue) == 0, "DLQ emptied");
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.ProcessedMessage>(db => db.ProcessedMessages.Where(p => p.MessageId == envelope.MessageId)));
    }

    [Fact]
    public async Task Replay_sends_the_message_back_to_its_origin_with_attempt_zero_and_the_same_ids()
    {
        var ticket = await SeedTicketAsync("replay-origin");
        var envelope = TicketCreated(ticket, correlationId: "corr-origin");
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        await PutInDlqAsync(envelope, attempt: 3);

        var result = await ReplayAsync();

        Assert.Equal(0, result.ExitCode);
        var back = Assert.Single(await Broker.PeekAsync("test.sink"));
        Assert.Equal("0", back.Header("attempt"));
        Assert.Equal(envelope.MessageId.ToString(), back.Header("message-id"));
        Assert.Equal("corr-origin", back.Header("correlation-id"));
        Assert.Equal(ticket.TenantId.ToString(), back.Header("tenant-id"));
        Assert.Equal("1", back.Header("x-replay-count"));
        Assert.Equal("", back.Header("x-failure-reason"));
        Assert.Equal(envelope.ToJson(), back.Body);
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task Dry_run_lists_the_messages_and_changes_nothing()
    {
        var ticket = await SeedTicketAsync("replay-dry");
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        var envelope = TicketCreated(ticket);
        await PutInDlqAsync(envelope);

        var result = await ReplayAsync("--dry-run");

        Assert.Equal(0, result.ExitCode);
        OutputContains(result, "simulation");
        OutputContains(result, "would replay");
        OutputContains(result, envelope.MessageId.ToString());
        OutputContains(result, "0 rejoué(s)");
        Assert.Equal(1u, await Broker.CountAsync(MessagingNames.DlqQueue));
        Assert.Equal(0u, await Broker.CountAsync("test.sink"));
        Assert.Equal("EmailAckConsumer", Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue)).Header("x-failed-consumer"));
    }

    [Fact]
    public async Task Max_limits_the_number_of_replayed_messages()
    {
        var ticket = await SeedTicketAsync("replay-max");
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);
        var envelopes = Enumerable.Range(0, 3).Select(_ => TicketCreated(ticket)).ToArray();
        foreach (var envelope in envelopes)
        {
            await PutInDlqAsync(envelope);
        }

        var result = await ReplayAsync("--max", "2");

        Assert.Equal(0, result.ExitCode);
        OutputContains(result, "2 message(s) lu(s), 2 rejoué(s)");
        Assert.Equal(2u, await Broker.CountAsync("test.sink"));
        Assert.Equal(1u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task A_poisoned_message_is_replayed_to_its_origin_and_abandoned_again()
    {
        await using var worker = await StartWorkerAsync();
        await Broker.PublishAsync(MessagingNames.EventsExchange, EventTypes.TicketCreated, Encoding.UTF8.GetBytes("{ not json"));
        await WaitForDlqAsync(1, seconds: 10);

        var result = await ReplayAsync();

        Assert.Equal(0, result.ExitCode);
        OutputContains(result, "1 rejoué(s)");
        // Still poisoned: it comes back to the DLQ (and the replay never loops by itself).
        await Poll.UntilAsync(async () => await Broker.CountAsync(MessagingNames.DlqQueue) == 1, "poisoned message back in the DLQ", 10);
        Assert.Equal("poison", Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue)).Header("x-failure-kind"));
    }

    [Fact]
    public async Task A_message_whose_origin_is_unknown_stays_in_the_dlq_and_the_exit_code_says_so()
    {
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        await Broker.PublishAsync(MessagingNames.DlxExchange, MessagingNames.DlqRoutingKey, Encoding.UTF8.GetBytes("garbage"));

        var result = await ReplayAsync();

        Assert.Equal(1, result.ExitCode);
        OutputContains(result, "skipped: origin unknown");
        OutputContains(result, "1 ignoré(s)");
        Assert.Equal(1u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task An_empty_dlq_gives_an_empty_report()
    {
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);

        var result = await ReplayAsync();

        Assert.Equal(0, result.ExitCode);
        OutputContains(result, "0 message(s) lu(s), 0 rejoué(s)");
    }

    [Fact]
    public async Task The_command_exits_by_itself_without_starting_the_web_host_or_the_background_services()
    {
        await using var topology = await StartWorkerAsync(consumers: false, dispatcher: false);
        // A row waiting in the outbox would be published by the dispatcher if the Worker's services were started.
        var ticket = await SeedTicketAsync("replay-quiet");
        await AddToOutboxAsync(TicketCreated(ticket));
        await Broker.BindSinkAsync("test.sink", MessagingNames.EventsExchange, EventTypes.TicketCreated);

        var result = await ReplayAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Now listening", result.Output + result.Error);
        Assert.DoesNotContain("Application started", result.Output + result.Error);
        Assert.Equal(0u, await Broker.CountAsync("test.sink"));
        Assert.Equal(1, await CountAsync<Desk.Core.Domain.OutboxMessage>(db => db.OutboxMessages.Where(o => o.DispatchedAt == null)));
    }

    [Theory]
    [InlineData("--max", "abc")]
    [InlineData("--max", "0")]
    [InlineData("--max")]
    [InlineData("--unknown")]
    public async Task Bad_arguments_print_the_usage_and_exit_with_code_2(params string[] arguments)
    {
        var result = await ReplayAsync(arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage : dotnet Desk.Worker.dll replay-dlq [--max N] [--dry-run]", result.Error);
    }

    [Fact]
    public async Task An_unreachable_broker_exits_with_code_3()
    {
        var env = Env();
        env["ConnectionStrings__RabbitMq"] = "amqp://guest:guest@127.0.0.1:1";

        var result = await WorkerProcess.RunAsync(["replay-dlq"], env);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("RabbitMQ injoignable", result.Error);
    }
}
