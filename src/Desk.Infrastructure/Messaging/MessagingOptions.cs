using System.Globalization;

namespace Desk.Infrastructure.Messaging;

/// <summary>Section <c>Messaging</c> of the configuration.</summary>
public sealed class MessagingOptions
{
    /// <summary>Name of a consumer (class name or queue name) forced to fail: demonstrates retries and the DLQ.</summary>
    public string? FailConsumer { get; set; }

    /// <summary>Retry delays as TimeSpan strings (default 00:00:05, 00:00:30, 00:02:00). Null array on purpose: the binder appends to arrays.</summary>
    public string[]? RetryDelays { get; set; }

    public ushort Prefetch { get; set; } = 10;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int OutboxBatchSize { get; set; } = 50;

    public RetryPolicy BuildRetryPolicy() =>
        new(RetryDelays is { Length: > 0 }
            ? RetryDelays.Select(d => TimeSpan.Parse(d, CultureInfo.InvariantCulture)).ToArray()
            : RetryPolicy.DefaultDelays);

    public bool ShouldFail(ConsumerDefinition consumer) =>
        !string.IsNullOrWhiteSpace(FailConsumer)
        && (string.Equals(FailConsumer, consumer.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FailConsumer, consumer.QueueSuffix, StringComparison.OrdinalIgnoreCase));
}
