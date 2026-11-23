using System.Globalization;
using Desk.Core.Events;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Events;
using Serilog.Context;

namespace Desk.Infrastructure.Messaging;

public enum DeliveryOutcome
{
    Processed,
    Duplicate,
    Retried,
    DeadLettered,
}

/// <summary>
/// What every consumer shares, whatever its business effect. For one delivery:
/// 1. validate the envelope (invalid, no tenant: poison, straight to desk.dlq, no retry);
/// 2. open a DI scope and set the tenant of the message BEFORE any data access;
/// 3. in one transaction: record (MessageId, Consumer) in ProcessedMessage (a duplicate stops here), run the handler, commit;
/// 4. on failure: retry through the delay queue of the next tier, or desk.dlq when permanent / out of retries.
/// The caller acknowledges the delivery when this method returns, and requeues it when it throws.
/// </summary>
public sealed class MessageProcessor(
    IServiceScopeFactory scopes,
    IMessagePublisher publisher,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    ILogger<MessageProcessor> logger)
{
    private readonly RetryPolicy _retry = options.Value.BuildRetryPolicy();

    public async Task<DeliveryOutcome> ProcessAsync(ConsumerDefinition consumer, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers;
        var attempt = MessageHeaders.GetInt(headers, MessagingNames.Attempt) ?? 0;
        var origin = ResolveOrigin(delivery);

        if (!MessageEnvelope.TryParse(delivery.Body.Span, out var envelope, out var reason))
        {
            return await DeadLetterAsync(consumer, delivery, attempt, origin, null, reason, MessagingNames.FailureKindPoison, ct);
        }

        // The tenant header is redundant with the envelope; a disagreement means the message was tampered with or mis-built.
        var headerTenant = MessageHeaders.GetString(headers, MessagingNames.TenantId);
        if (headerTenant is not null && !(Guid.TryParse(headerTenant, out var parsed) && parsed == envelope.TenantId))
        {
            return await DeadLetterAsync(consumer, delivery, attempt, origin, envelope, "tenant-id header differs from the envelope tenantId.",
                MessagingNames.FailureKindPoison, ct);
        }

        using var _ = LogContext.PushProperty("CorrelationId", envelope.CorrelationId);
        using var __ = LogContext.PushProperty("MessageId", envelope.MessageId);
        using var ___ = LogContext.PushProperty("Consumer", consumer.Name);
        using var ____ = LogContext.PushProperty("TenantId", envelope.TenantId);

        try
        {
            if (options.Value.ShouldFail(consumer))
            {
                throw new SimulatedFailureException(consumer.Name);
            }

            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<TenantContext>().UseTenant(envelope.TenantId);
            sp.GetRequiredService<CorrelationContext>().Id = envelope.CorrelationId;
            var db = sp.GetRequiredService<DeskDbContext>();
            var handler = (DeskConsumer)sp.GetRequiredService(consumer.Type);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // INSERT ... ON CONFLICT DO NOTHING: a concurrent duplicate waits for the first transaction, then sees 0 rows.
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ProcessedMessages" ("MessageId", "Consumer", "ProcessedAt")
                VALUES ({envelope.MessageId}, {consumer.Name}, {clock.GetUtcNow()})
                ON CONFLICT DO NOTHING
                """, ct);
            if (inserted == 0)
            {
                logger.LogInformation("Duplicate message {MessageId} ignored by {Consumer}", envelope.MessageId, consumer.Name);
                return DeliveryOutcome.Duplicate;
            }

            await handler.HandleAsync(envelope, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Message {MessageId} ({Type}) processed by {Consumer} (attempt {Attempt})",
                envelope.MessageId, envelope.Type, consumer.Name, attempt);
            return DeliveryOutcome.Processed;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var failure = $"{ex.GetType().Name}: {ex.Message}";
            if (FailureClassifier.IsPermanent(ex))
            {
                logger.LogError(ex, "Message {MessageId} cannot be processed by {Consumer}; sent to the DLQ", envelope.MessageId, consumer.Name);
                return await DeadLetterAsync(consumer, delivery, attempt, origin, envelope, failure, MessagingNames.FailureKindPoison, ct);
            }

            var decision = _retry.Decide(attempt);
            if (!decision.Retry)
            {
                logger.LogError(ex, "Message {MessageId} failed in {Consumer} after {Attempts} attempts; sent to the DLQ",
                    envelope.MessageId, consumer.Name, attempt + 1);
                return await DeadLetterAsync(consumer, delivery, attempt, origin, envelope, failure, MessagingNames.FailureKindRetriesExhausted, ct);
            }

            logger.LogWarning(ex, "Message {MessageId} failed in {Consumer} (attempt {Attempt}); retry in {Delay}",
                envelope.MessageId, consumer.Name, attempt, decision.Delay);
            await publisher.PublishAsync(new OutgoingMessage(
                MessagingNames.RetryExchange,
                MessagingNames.DelayRoutingKeyFor(consumer.QueueSuffix, decision.Delay),
                delivery.Body,
                envelope.MessageId.ToString(),
                envelope.CorrelationId,
                envelope.TenantId.ToString(),
                decision.NextAttempt,
                OriginHeaders(origin)), ct);
            return DeliveryOutcome.Retried;
        }
    }

    /// <summary>The first delivery comes from desk.events; one that went through a delay queue carries its origin in headers.</summary>
    private static (string Exchange, string RoutingKey) ResolveOrigin(BasicDeliverEventArgs delivery)
    {
        var exchange = MessageHeaders.GetString(delivery.BasicProperties.Headers, MessagingNames.OriginalExchange);
        var routingKey = MessageHeaders.GetString(delivery.BasicProperties.Headers, MessagingNames.OriginalRoutingKey);
        return exchange is not null && routingKey is not null
            ? (exchange, routingKey)
            : (delivery.Exchange, delivery.RoutingKey);
    }

    private static Dictionary<string, object?> OriginHeaders((string Exchange, string RoutingKey) origin) => new()
    {
        [MessagingNames.OriginalExchange] = origin.Exchange,
        [MessagingNames.OriginalRoutingKey] = origin.RoutingKey,
    };

    private async Task<DeliveryOutcome> DeadLetterAsync(
        ConsumerDefinition consumer,
        BasicDeliverEventArgs delivery,
        int attempt,
        (string Exchange, string RoutingKey) origin,
        MessageEnvelope? envelope,
        string reason,
        string kind,
        CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers;
        var messageId = envelope?.MessageId.ToString() ?? MessageHeaders.GetString(headers, MessagingNames.MessageId) ?? "unknown";
        var correlationId = envelope?.CorrelationId ?? MessageHeaders.GetString(headers, MessagingNames.CorrelationId) ?? "unknown";
        var tenantId = envelope?.TenantId.ToString() ?? MessageHeaders.GetString(headers, MessagingNames.TenantId) ?? "unknown";

        var extra = OriginHeaders(origin);
        extra[MessagingNames.FailedConsumer] = consumer.Name;
        extra[MessagingNames.FailureReason] = reason.Length > 1000 ? reason[..1000] : reason;
        extra[MessagingNames.FailureKind] = kind;
        extra[MessagingNames.FailedAt] = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        if (kind == MessagingNames.FailureKindPoison && envelope is null)
        {
            logger.LogError("Poisoned message for {Consumer} ({Reason}); sent to the DLQ without retry", consumer.Name, reason);
        }

        await publisher.PublishAsync(new OutgoingMessage(
            MessagingNames.DlxExchange, MessagingNames.DlqRoutingKey, delivery.Body,
            messageId, correlationId, tenantId, attempt, extra), ct);
        return DeliveryOutcome.DeadLettered;
    }
}
