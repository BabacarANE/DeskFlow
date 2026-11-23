using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Desk.Infrastructure.Messaging;

/// <summary>
/// Declares the whole topology; every declaration is idempotent, so the API and the Worker both run it at startup.
/// <code>
/// desk.events (topic)  --routing keys-->  desk.{consumer}                       (durable queue of the consumer)
/// desk.retry  (direct) --{consumer}.retry.{delay}--> desk.{consumer}.retry.{delay}  (one TTL queue per tier, no consumer)
///                       the TTL queue dead-letters to desk.retry with key {consumer}, bound to desk.{consumer}
/// desk.dlx    (direct) --dlq--> desk.dlq
/// </code>
/// One delay queue per tier keeps every message of a queue at the same TTL: RabbitMQ only expires from the head of a
/// queue, so mixed TTLs in one queue would make a 5 s message wait behind a 2 min one.
/// </summary>
public sealed class MessagingTopology(
    RabbitConnections connections,
    IEnumerable<ConsumerDefinition> hosted,
    IOptions<MessagingOptions> options,
    ILogger<MessagingTopology> logger)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _declared;

    /// <summary>Every consumer of the product (whatever its host) plus those registered explicitly: the topology is complete from either process.</summary>
    public IReadOnlyList<ConsumerDefinition> Consumers { get; } =
        ConsumerDefinition.Scan(typeof(MessagingTopology).Assembly)
            .Concat(hosted)
            .GroupBy(c => c.QueueSuffix)
            .Select(g => g.Last())
            .ToList();

    /// <summary>Declares once (retrying with a growing pause while the broker is unreachable), then returns immediately.</summary>
    public async Task EnsureDeclaredAsync(CancellationToken ct)
    {
        if (_declared)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; !_declared; attempt++)
            {
                try
                {
                    await DeclareAsync(ct);
                    _declared = true;
                    logger.LogInformation("RabbitMQ topology declared ({Consumers} consumers)", Consumers.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var pause = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                    logger.LogWarning("RabbitMQ topology not declared ({Error}); retrying in {Pause}", ex.Message, pause);
                    await Task.Delay(pause, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeclareAsync(CancellationToken ct)
    {
        var connection = await connections.PublisherAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var delays = options.Value.BuildRetryPolicy().Delays;

        await channel.ExchangeDeclareAsync(MessagingNames.EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(MessagingNames.RetryExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(MessagingNames.DlxExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync(MessagingNames.DlqQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(MessagingNames.DlqQueue, MessagingNames.DlxExchange, MessagingNames.DlqRoutingKey, cancellationToken: ct);

        foreach (var consumer in Consumers)
        {
            await channel.QueueDeclareAsync(consumer.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            foreach (var key in consumer.RoutingKeys)
            {
                await channel.QueueBindAsync(consumer.Queue, MessagingNames.EventsExchange, key, cancellationToken: ct);
            }

            // Return path of the delay queues: desk.retry/{consumer} -> desk.{consumer}.
            await channel.QueueBindAsync(consumer.Queue, MessagingNames.RetryExchange, consumer.QueueSuffix, cancellationToken: ct);

            foreach (var delay in delays.Distinct())
            {
                var delayQueue = MessagingNames.DelayQueueFor(consumer.QueueSuffix, delay);
                var arguments = new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = (int)delay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = MessagingNames.RetryExchange,
                    ["x-dead-letter-routing-key"] = consumer.QueueSuffix,
                };
                await channel.QueueDeclareAsync(delayQueue, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: ct);
                await channel.QueueBindAsync(delayQueue, MessagingNames.RetryExchange,
                    MessagingNames.DelayRoutingKeyFor(consumer.QueueSuffix, delay), cancellationToken: ct);
            }
        }
    }
}

/// <summary>Declares the topology in the background at startup (API): a broker that is down never blocks the host.</summary>
public sealed class TopologyStartupService(MessagingTopology topology) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await topology.EnsureDeclaredAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
    }
}
