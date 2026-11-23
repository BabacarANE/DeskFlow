using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Desk.Infrastructure.Messaging;

/// <summary>Completes once every hosted consumer is subscribed (used by tests and by readiness logs).</summary>
public sealed class ConsumerReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    internal void SetReady() => _ready.TrySetResult();
}

/// <summary>
/// Subscribes every consumer registered in this process to its queue: manual ack, prefetch 10 (configurable),
/// one channel per consumer. A delivery is acknowledged only after the processor has settled it (processed, duplicate,
/// republished to a delay queue or to the DLQ); if even that fails the delivery is requeued.
/// </summary>
public sealed class ConsumerHostedService(
    RabbitConnections connections,
    MessagingTopology topology,
    MessageProcessor processor,
    IEnumerable<ConsumerDefinition> hosted,
    ConsumerReadiness readiness,
    IOptions<MessagingOptions> options,
    ILogger<ConsumerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = new List<IChannel>();
        try
        {
            await topology.EnsureDeclaredAsync(stoppingToken);
            var connection = await connections.ConsumerAsync(stoppingToken);
            var prefetch = options.Value.Prefetch;

            foreach (var definition in hosted)
            {
                // Dispatch concurrency = prefetch: the 10 unacknowledged messages are really processed in parallel.
                var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false, consumerDispatchConcurrency: prefetch),
                    stoppingToken);
                channels.Add(channel);
                await channel.BasicQosAsync(0, prefetch, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, definition, delivery, stoppingToken);
                await channel.BasicConsumeAsync(definition.Queue, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("Consuming {Queue} ({Consumer}, prefetch {Prefetch})", definition.Queue, definition.Name, prefetch);
            }

            readiness.SetReady();
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            foreach (var channel in channels)
            {
                try
                {
                    await channel.CloseAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // Connection already gone.
                }

                channel.Dispose();
            }
        }
    }

    private async Task HandleAsync(IChannel channel, ConsumerDefinition definition, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try
        {
            await processor.ProcessAsync(definition, delivery, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Could not settle the message (broker unreachable, shutdown): give it back, it will be redelivered.
            logger.LogError(ex, "Delivery {DeliveryTag} of {Queue} could not be settled; requeued", delivery.DeliveryTag, definition.Queue);
            try
            {
                // Short pause so that a broken dependency does not turn into a tight redelivery loop.
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, CancellationToken.None);
            }
            catch (Exception)
            {
                // Channel closed: the broker requeues unacknowledged messages by itself.
            }
        }
    }
}
