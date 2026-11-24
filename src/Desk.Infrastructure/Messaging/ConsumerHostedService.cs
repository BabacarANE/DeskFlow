using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Desk.Infrastructure.Messaging;

/// <summary>
/// Which consumers are subscribed right now. Completes <see cref="Ready"/> once all of them have been; the health check
/// reports unhealthy while one of them is not (broker restart, channel closed by the server).
/// </summary>
public sealed class ConsumerReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, bool> _subscribed = new();

    public Task Ready => _ready.Task;

    public IReadOnlyList<string> NotSubscribed => _subscribed.Where(p => !p.Value).Select(p => p.Key).Order().ToList();

    internal void Expect(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            _subscribed.TryAdd(name, false);
        }
    }

    internal void Set(string name, bool subscribed)
    {
        _subscribed[name] = subscribed;
        if (_subscribed.Values.All(v => v))
        {
            _ready.TrySetResult();
        }
    }
}

/// <summary>Part of <c>/health/ready</c> of the Worker: every hosted consumer must be subscribed to its queue.</summary>
public sealed class ConsumersHealthCheck(ConsumerReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var missing = readiness.NotSubscribed;
        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Consumers not subscribed: " + string.Join(", ", missing)));
    }
}

/// <summary>
/// Keeps every consumer registered in this process subscribed to its queue (manual ack, prefetch 10, one channel each).
/// Each consumer is supervised: if its channel or connection closes (broker restart, queue deleted, server-side close) the
/// subscription is rebuilt on a fresh connection, after re-declaring the topology. A delivery is acknowledged only after the
/// processor has settled it (processed, duplicate, republished to a delay queue or to the DLQ); if even that fails it is
/// requeued and delivered again, which idempotence makes harmless. On shutdown consumption stops first, in-flight handlers
/// get <see cref="MessagingOptions.ShutdownTimeout"/> to finish, then channels close.
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
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly List<ConsumerDefinition> _definitions = hosted.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        readiness.Expect(_definitions.Select(d => d.Name));
        await Task.WhenAll(_definitions.Select(d => SuperviseAsync(d, stoppingToken)));
    }

    private async Task SuperviseAsync(ConsumerDefinition definition, CancellationToken stopping)
    {
        for (var attempt = 0; !stopping.IsCancellationRequested; attempt++)
        {
            Subscription? subscription = null;
            try
            {
                await topology.DeclareAsync(stopping); // idempotent; also restores the queues if the broker lost them
                subscription = await SubscribeAsync(definition, stopping);
                readiness.Set(definition.Name, true);
                attempt = 0;
                await subscription.Closed.WaitAsync(stopping);
                logger.LogWarning("Consumer {Consumer} lost its subscription to {Queue}; re-subscribing", definition.Name, definition.Queue);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Host stopping.
            }
            catch (Exception ex)
            {
                logger.LogWarning("Consumer {Consumer} could not subscribe to {Queue} ({Error}); retrying", definition.Name, definition.Queue, ex.Message);
            }
            finally
            {
                readiness.Set(definition.Name, false);
                if (subscription is not null)
                {
                    await subscription.StopAsync(options.Value.ShutdownTimeout, logger);
                }
            }

            if (!stopping.IsCancellationRequested)
            {
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<Subscription> SubscribeAsync(ConsumerDefinition definition, CancellationToken stopping)
    {
        var connection = await connections.ConsumerAsync(stopping);
        var prefetch = options.Value.Prefetch;

        // Dispatch concurrency = prefetch: the 10 unacknowledged messages are really processed in parallel.
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false, consumerDispatchConcurrency: prefetch),
            stopping);
        var subscription = new Subscription(channel);
        try
        {
            await channel.BasicQosAsync(0, prefetch, false, stopping);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => HandleAsync(subscription, definition, delivery);
            consumer.UnregisteredAsync += (_, _) => subscription.MarkClosed();
            channel.ChannelShutdownAsync += (_, _) => subscription.MarkClosed();
            connection.ConnectionShutdownAsync += (_, _) => subscription.MarkClosed();
            subscription.ConsumerTag = await channel.BasicConsumeAsync(definition.Queue, autoAck: false, consumer, stopping);
            logger.LogInformation("Consuming {Queue} ({Consumer}, prefetch {Prefetch})", definition.Queue, definition.Name, prefetch);
            return subscription;
        }
        catch
        {
            await subscription.StopAsync(TimeSpan.Zero, logger);
            throw;
        }
    }

    private async Task HandleAsync(Subscription subscription, ConsumerDefinition definition, BasicDeliverEventArgs delivery)
    {
        subscription.Enter();
        try
        {
            // Not the host's stopping token: a message being processed finishes (see Subscription.StopAsync for the limit).
            await processor.ProcessAsync(definition, delivery, subscription.HandlerToken);
            await subscription.Channel.BasicAckAsync(delivery.DeliveryTag, false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Could not settle the message (broker unreachable, channel closed, shutdown): give it back, it is redelivered.
            logger.LogError(ex, "Delivery {DeliveryTag} of {Queue} could not be settled; requeued", delivery.DeliveryTag, definition.Queue);
            try
            {
                if (!subscription.HandlerToken.IsCancellationRequested)
                {
                    // Short pause so that a broken dependency does not turn into a tight redelivery loop.
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                }

                await subscription.Channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, CancellationToken.None);
            }
            catch (Exception)
            {
                // Channel closed: the broker requeues unacknowledged messages by itself.
            }
        }
        finally
        {
            subscription.Leave();
        }
    }

    /// <summary>One live subscription: its channel, its in-flight handlers and the signal that it was closed.</summary>
    private sealed class Subscription(IChannel channel)
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _handlers = new();
        private int _inFlight;

        public IChannel Channel { get; } = channel;

        public string? ConsumerTag { get; set; }

        public Task Closed => _closed.Task;

        public CancellationToken HandlerToken => _handlers.Token;

        public Task MarkClosed()
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public void Enter() => Interlocked.Increment(ref _inFlight);

        public void Leave() => Interlocked.Decrement(ref _inFlight);

        public async Task StopAsync(TimeSpan grace, ILogger logger)
        {
            try
            {
                // 1. Stop receiving: no new delivery, the ones in flight stay ours.
                if (ConsumerTag is not null && Channel.IsOpen)
                {
                    await Channel.BasicCancelAsync(ConsumerTag, noWait: false, CancellationToken.None);
                }

                // 2. Let the running handlers finish (bounded), then 3. interrupt the stragglers: they are requeued.
                var deadline = DateTime.UtcNow + grace;
                while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }

                if (Volatile.Read(ref _inFlight) > 0)
                {
                    logger.LogWarning("{Count} message(s) still in flight after {Grace}; interrupting them (they will be redelivered)",
                        Volatile.Read(ref _inFlight), grace);
                    await _handlers.CancelAsync();
                    var interruptDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < interruptDeadline)
                    {
                        await Task.Delay(50);
                    }
                }

                // 4. Close the channel.
                if (Channel.IsOpen)
                {
                    await Channel.CloseAsync(CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // Channel or connection already gone.
            }
            finally
            {
                Channel.Dispose();
            }
        }
    }
}
