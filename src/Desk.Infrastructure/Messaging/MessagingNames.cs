namespace Desk.Infrastructure.Messaging;

/// <summary>Names of the RabbitMQ topology and of the AMQP headers (docs/api/events.md, SPEC section 8).</summary>
public static class MessagingNames
{
    public const string EventsExchange = "desk.events";
    public const string RetryExchange = "desk.retry";
    public const string DlxExchange = "desk.dlx";
    public const string DlqQueue = "desk.dlq";
    public const string DlqRoutingKey = "dlq";

    // Mandatory headers of every message.
    public const string MessageId = "message-id";
    public const string CorrelationId = "correlation-id";
    public const string TenantId = "tenant-id";
    public const string Attempt = "attempt";

    // Headers added by this layer: where the message came from, so that replay can send it back.
    public const string OriginalExchange = "x-original-exchange";
    public const string OriginalRoutingKey = "x-original-routing-key";

    // Headers added when a message is abandoned in desk.dlq.
    public const string FailedConsumer = "x-failed-consumer";
    public const string FailureReason = "x-failure-reason";
    public const string FailureKind = "x-failure-kind";
    public const string FailedAt = "x-failed-at";

    // Set by replay-dlq.
    public const string ReplayCount = "x-replay-count";

    public const string FailureKindPoison = "poison";
    public const string FailureKindRetriesExhausted = "retries-exhausted";

    public static string QueueFor(string queueSuffix) => "desk." + queueSuffix;

    /// <summary>Delay queue of one consumer for one tier, e.g. <c>desk.email-ack.retry.30s</c>.</summary>
    public static string DelayQueueFor(string queueSuffix, TimeSpan delay) => $"desk.{queueSuffix}.retry.{RetryPolicy.Label(delay)}";

    /// <summary>Routing key of the delay queue on <see cref="RetryExchange"/>.</summary>
    public static string DelayRoutingKeyFor(string queueSuffix, TimeSpan delay) => $"{queueSuffix}.retry.{RetryPolicy.Label(delay)}";
}
