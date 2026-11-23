using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Desk.Infrastructure.Messaging;

/// <summary>Correlation id of the current operation: the HTTP request in the API, the consumed message in the Worker.</summary>
public sealed class CorrelationContext
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Adds an <see cref="OutboxMessage"/> to the current <see cref="DeskDbContext"/>: it is written by the same
/// <c>SaveChanges</c> (hence the same transaction) as the business data, or not at all.
/// <c>OutboxMessage.Payload</c> holds the complete envelope; its <c>Id</c> is the <c>messageId</c>.
/// </summary>
public sealed class OutboxWriter(DeskDbContext db, CorrelationContext correlation)
{
    public MessageEnvelope Add<TPayload>(string type, Guid tenantId, TPayload payload, DateTimeOffset at)
    {
        // PostgreSQL keeps microseconds: what we publish equals what is stored.
        var occurredAt = new DateTimeOffset(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);
        var envelope = MessageEnvelope.Create(Guid.NewGuid(), type, tenantId, occurredAt, correlation.Id, payload);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = envelope.MessageId,
            Type = type,
            Payload = envelope.ToJson(),
            CreatedAt = occurredAt,
        });
        return envelope;
    }
}

/// <summary>
/// Publishes pending outbox rows. One batch = one transaction: rows are locked with <c>FOR UPDATE SKIP LOCKED</c>
/// (concurrent dispatchers take disjoint batches), published with confirms, then marked dispatched. If the process dies
/// between the publish and the commit the batch is published again: at-least-once, consumers are idempotent.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IMessagePublisher publisher,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Dispatches one batch and returns the number of messages published.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var batch = options.Value.OutboxBatchSize;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM "OutboxMessages"
                WHERE "DispatchedAt" IS NULL
                ORDER BY "CreatedAt", "Id"
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var published = 0;
        try
        {
            foreach (var row in rows)
            {
                if (!MessageEnvelope.TryParse(System.Text.Encoding.UTF8.GetBytes(row.Payload), out var envelope, out var reason))
                {
                    // Stays pending and visible: it is a bug of the writer, not something to drop silently.
                    logger.LogError("Outbox message {OutboxId} has an invalid envelope ({Reason}); skipped", row.Id, reason);
                    continue;
                }

                using (LogContext.PushProperty("CorrelationId", envelope.CorrelationId))
                {
                    await publisher.PublishAsync(OutgoingMessage.ForEvent(envelope), ct);
                    row.DispatchedAt = clock.GetUtcNow();
                    published++;
                    logger.LogInformation("Outbox message {MessageId} ({Type}) published", envelope.MessageId, envelope.Type);
                }
            }
        }
        finally
        {
            // Rows published before a failure must not be published again: commit them, then let the error surface.
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        return published;
    }
}

/// <summary>Polls the outbox (every second by default) and publishes what is pending.</summary>
public sealed class OutboxDispatcherService(
    OutboxDispatcher dispatcher, MessagingTopology topology, IOptions<MessagingOptions> options, ILogger<OutboxDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Publishing to an exchange that does not exist yet would close the channel.
            await topology.EnsureDeclaredAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await dispatcher.DispatchBatchAsync(stoppingToken);
                failures = 0;
                if (count >= options.Value.OutboxBatchSize)
                {
                    continue; // More is probably waiting: do not sleep.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning(ex, "Outbox dispatch failed ({Failures} in a row); will retry", failures);
            }

            // Growing pause while failing (database not migrated yet, broker down), capped at 10 s.
            var pause = options.Value.OutboxPollInterval * Math.Min(Math.Max(failures, 1), 10);
            await Task.Delay(pause, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
