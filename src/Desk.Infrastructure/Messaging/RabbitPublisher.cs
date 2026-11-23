using Desk.Core.Events;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Desk.Infrastructure.Messaging;

/// <summary>A message to publish. The four mandatory headers are always written.</summary>
public sealed record OutgoingMessage(
    string Exchange,
    string RoutingKey,
    ReadOnlyMemory<byte> Body,
    string MessageId,
    string CorrelationId,
    string TenantId,
    int Attempt = 0,
    IReadOnlyDictionary<string, object?>? ExtraHeaders = null,
    bool Mandatory = false)
{
    public static OutgoingMessage ForEvent(MessageEnvelope envelope, int attempt = 0) =>
        new(MessagingNames.EventsExchange, envelope.Type, envelope.ToUtf8Json(),
            envelope.MessageId.ToString(), envelope.CorrelationId, envelope.TenantId.ToString(), attempt, Mandatory: true);
}

/// <summary>A mandatory message that no queue received: the broker gave it back.</summary>
public sealed class UnroutableMessageException(string exchange, string routingKey)
    : Exception($"No queue is bound to {exchange}/{routingKey}: the message was returned by the broker.");

public static class MessagePublisherExtensions
{
    /// <summary>Publishes a mandatory message; an unroutable one is a failure, never a success.</summary>
    public static async Task PublishRoutedAsync(this IMessagePublisher publisher, OutgoingMessage message, CancellationToken ct)
    {
        if (!await publisher.PublishAsync(message with { Mandatory = true }, ct))
        {
            throw new UnroutableMessageException(message.Exchange, message.RoutingKey);
        }
    }
}

public interface IMessagePublisher
{
    /// <summary>
    /// Publishes with a publisher confirm: returns once the broker has taken responsibility for the message
    /// (persisted when the queue is durable). Returns false only for a mandatory message that no queue received.
    /// Throws when the broker refuses the message or is unreachable.
    /// </summary>
    Task<bool> PublishAsync(OutgoingMessage message, CancellationToken ct);
}

/// <summary>Publishes persistent JSON messages on one channel with publisher confirms (publishes are serialised).</summary>
public sealed class RabbitPublisher(RabbitConnections connections, ILogger<RabbitPublisher> logger) : IMessagePublisher, IAsyncDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private volatile string? _returnedMessageId;

    public async Task<bool> PublishAsync(OutgoingMessage message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(ct);
            var headers = new Dictionary<string, object?>
            {
                [MessagingNames.MessageId] = message.MessageId,
                [MessagingNames.CorrelationId] = message.CorrelationId,
                [MessagingNames.TenantId] = message.TenantId,
                [MessagingNames.Attempt] = message.Attempt,
            };
            foreach (var (key, value) in message.ExtraHeaders ?? new Dictionary<string, object?>())
            {
                headers[key] = value;
            }

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.MessageId,
                CorrelationId = message.CorrelationId,
                Headers = headers,
            };

            _returnedMessageId = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConfirmTimeout);
            try
            {
                await channel.BasicPublishAsync(message.Exchange, message.RoutingKey, message.Mandatory, properties, message.Body, timeout.Token);
            }
            catch (RabbitMQ.Client.Exceptions.PublishReturnException ex)
            {
                // With confirm tracking, a mandatory message that no queue received is reported as an exception
                // (basic.return, 312 NO_ROUTE). The channel is fine: it is a routing answer, not a failure of the broker.
                logger.LogWarning("Message {MessageId} returned by the broker ({ReplyText}) on {Exchange}/{RoutingKey}",
                    message.MessageId, ex.ReplyText, message.Exchange, message.RoutingKey);
                return false;
            }
            catch (Exception)
            {
                // The channel may be closed or in an unknown state: next call opens a new one.
                await DropChannelAsync();
                throw;
            }

            return !(message.Mandatory && _returnedMessageId == message.MessageId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await DropChannelAsync();
        var connection = await connections.PublisherAsync(ct);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        channel.BasicReturnAsync += (_, args) =>
        {
            _returnedMessageId = args.BasicProperties.MessageId;
            logger.LogWarning("Message {MessageId} returned by the broker ({ReplyText}): no queue is bound to {Exchange}/{RoutingKey}",
                args.BasicProperties.MessageId, args.ReplyText, args.Exchange, args.RoutingKey);
            return Task.CompletedTask;
        };
        _channel = channel;
        return channel;
    }

    private async Task DropChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is not null)
        {
            try
            {
                await channel.CloseAsync();
            }
            catch (Exception)
            {
                // Already broken.
            }

            channel.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropChannelAsync();
        _gate.Dispose();
    }
}
