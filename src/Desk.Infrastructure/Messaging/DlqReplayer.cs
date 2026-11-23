using Desk.Core.Events;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Desk.Infrastructure.Messaging;

public sealed record ReplayOptions(int? Max = null, bool DryRun = false);

public sealed record ReplayEntry(string MessageId, string Consumer, string Reason, string Target, string Status);

public sealed record ReplayReport(int Read, int Replayed, int Skipped, int Failed, IReadOnlyList<ReplayEntry> Entries)
{
    public bool HasProblems => Skipped > 0 || Failed > 0;
}

/// <summary>The broker went away (or stopped answering) during the replay. <see cref="Partial"/> is what had been done.</summary>
public sealed class ReplayInterruptedException(ReplayReport partial, Exception inner)
    : Exception("The connection to RabbitMQ was lost during the replay: " + inner.Message, inner)
{
    public ReplayReport Partial { get; } = partial;
}

/// <summary>
/// <c>replay-dlq</c>: republishes the messages of <c>desk.dlq</c> to the exchange and routing key they came from, with
/// <c>attempt</c> reset to 0 and the same <c>message-id</c> and <c>correlation-id</c>. A message leaves the DLQ only after the
/// broker confirmed its republication. Consumers that already processed it ignore it (idempotence): the replay is safe.
/// </summary>
public sealed class DlqReplayer(RabbitConnections connections, ILogger<DlqReplayer> logger)
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);

    private static bool IsBrokerFailure(Exception ex) =>
        ex is RabbitMQ.Client.Exceptions.AlreadyClosedException or RabbitMQ.Client.Exceptions.OperationInterruptedException
            or RabbitMQ.Client.Exceptions.BrokerUnreachableException or System.IO.IOException or System.Net.Sockets.SocketException
            or TimeoutException or ObjectDisposedException;

    public async Task<ReplayReport> ReplayAsync(ReplayOptions options, CancellationToken ct)
    {
        var progress = new Progress();
        try
        {
            await ReplayCoreAsync(options, progress, ct);
            return progress.ToReport();
        }
        catch (Exception ex) when (IsBrokerFailure(ex))
        {
            // Not confirmed means not acknowledged: those messages are still in the DLQ.
            throw new ReplayInterruptedException(progress.ToReport(), ex);
        }
    }

    private sealed class Progress
    {
        public int Read;
        public int Replayed;
        public int Skipped;
        public int Failed;
        public List<ReplayEntry> Entries { get; } = [];

        public ReplayReport ToReport() => new(Read, Replayed, Skipped, Failed, Entries.ToList());
    }

    private async Task ReplayCoreAsync(ReplayOptions options, Progress progress, CancellationToken ct)
    {
        var connection = await connections.PublisherAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        string? returned = null;
        channel.BasicReturnAsync += (_, args) =>
        {
            returned = args.BasicProperties.MessageId;
            return Task.CompletedTask;
        };

        ulong lastKept = 0;

        // Only what is in the queue now: a message that fails again while we run comes back to the tail and is not taken twice.
        var declared = await channel.QueueDeclareAsync(MessagingNames.DlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        var limit = (int)Math.Min(declared.MessageCount, (uint)(options.Max ?? int.MaxValue));

        while (progress.Read < limit)
        {
            var result = await channel.BasicGetAsync(MessagingNames.DlqQueue, autoAck: false, ct);
            if (result is null)
            {
                break;
            }

            progress.Read++;
            var headers = result.BasicProperties.Headers;
            var messageId = MessageHeaders.GetString(headers, MessagingNames.MessageId) ?? "unknown";
            var consumer = MessageHeaders.GetString(headers, MessagingNames.FailedConsumer) ?? "unknown";
            var reason = MessageHeaders.GetString(headers, MessagingNames.FailureReason) ?? "";

            var target = ResolveTarget(result.Body.Span, headers);
            if (target is null)
            {
                progress.Skipped++;
                lastKept = result.DeliveryTag;
                progress.Entries.Add(new ReplayEntry(messageId, consumer, reason, "?", "skipped: origin unknown"));
                continue;
            }

            var label = $"{target.Value.Exchange}/{target.Value.RoutingKey}";
            if (options.DryRun)
            {
                lastKept = result.DeliveryTag;
                progress.Entries.Add(new ReplayEntry(messageId, consumer, reason, label, "would replay"));
                continue;
            }

            try
            {
                var correlationId = MessageHeaders.GetString(headers, MessagingNames.CorrelationId) ?? "unknown";
                var tenantId = MessageHeaders.GetString(headers, MessagingNames.TenantId) ?? "unknown";
                var replayCount = (MessageHeaders.GetInt(headers, MessagingNames.ReplayCount) ?? 0) + 1;
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    MessageId = messageId,
                    CorrelationId = correlationId,
                    Headers = new Dictionary<string, object?>
                    {
                        [MessagingNames.MessageId] = messageId,
                        [MessagingNames.CorrelationId] = correlationId,
                        [MessagingNames.TenantId] = tenantId,
                        [MessagingNames.Attempt] = 0,
                        [MessagingNames.ReplayCount] = replayCount,
                    },
                };

                returned = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConfirmTimeout);
                try
                {
                    await channel.BasicPublishAsync(target.Value.Exchange, target.Value.RoutingKey, mandatory: true, properties, result.Body, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No publisher confirm within {ConfirmTimeout.TotalSeconds:0} s.");
                }
                if (returned is not null)
                {
                    throw new InvalidOperationException($"No queue is bound to {label}.");
                }

                // Confirmed by the broker: only now does the message leave the DLQ.
                await channel.BasicAckAsync(result.DeliveryTag, false, ct);
                progress.Replayed++;
                progress.Entries.Add(new ReplayEntry(messageId, consumer, reason, label, "replayed"));
                logger.LogInformation("DLQ message {MessageId} replayed to {Target}", messageId, label);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !IsBrokerFailure(ex))
            {
                progress.Failed++;
                lastKept = result.DeliveryTag;
                progress.Entries.Add(new ReplayEntry(messageId, consumer, reason, label, "failed: " + ex.Message));
                logger.LogError(ex, "DLQ message {MessageId} could not be replayed", messageId);
            }
        }

        // Everything read but not replayed (dry run, unknown origin, failure) goes back to the DLQ, in place.
        if (lastKept > 0)
        {
            await channel.BasicNackAsync(lastKept, multiple: true, requeue: true, ct);
        }

    }

    /// <summary>Headers written when the message was abandoned; for a message that reached the DLQ otherwise, the envelope type is the routing key.</summary>
    private static (string Exchange, string RoutingKey)? ResolveTarget(ReadOnlySpan<byte> body, IDictionary<string, object?>? headers)
    {
        var exchange = MessageHeaders.GetString(headers, MessagingNames.OriginalExchange);
        var routingKey = MessageHeaders.GetString(headers, MessagingNames.OriginalRoutingKey);
        if (!string.IsNullOrEmpty(exchange) && !string.IsNullOrEmpty(routingKey))
        {
            return (exchange, routingKey);
        }

        return MessageEnvelope.TryParse(body, out var envelope, out _) ? (MessagingNames.EventsExchange, envelope.Type) : null;
    }
}
