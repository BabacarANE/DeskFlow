using System.Text;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace Desk.Infrastructure.Messaging;

/// <summary>
/// Two lazily opened connections (best practice: publishers and consumers do not share one, a blocked publisher
/// must not stall consumption). Both recover automatically after a network failure.
/// </summary>
public sealed class RabbitConnections(IConfiguration configuration) : IAsyncDisposable
{
    private readonly Slot _publisher = new("deskflow-publisher", recovers: true);
    private readonly Slot _consumer = new("deskflow-consumer", recovers: false);

    private Uri Uri => new(configuration.GetConnectionString("RabbitMq")
        ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not configured."));

    public Task<IConnection> PublisherAsync(CancellationToken ct) => GetAsync(_publisher, ct);

    public Task<IConnection> ConsumerAsync(CancellationToken ct) => GetAsync(_consumer, ct);

    private async Task<IConnection> GetAsync(Slot slot, CancellationToken ct)
    {
        if (slot.Connection is { IsOpen: true } open)
        {
            return open;
        }

        if (slot.Connection is not null && !slot.Recovers)
        {
            // The consumer connection does not recover by itself: ConsumerHostedService re-subscribes on a fresh one,
            // so that a recovered channel and a re-created one never consume the same queue twice.
            await slot.Gate.WaitAsync(ct);
            try
            {
                if (slot.Connection is { IsOpen: false } dead)
                {
                    slot.Connection = null;
                    dead.Dispose();
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        await slot.Gate.WaitAsync(ct);
        try
        {
            // A connection that is closed but recovering is kept: the client reopens it by itself.
            if (slot.Connection is null)
            {
                var factory = new ConnectionFactory
                {
                    Uri = Uri,
                    ClientProvidedName = slot.Name,
                    AutomaticRecoveryEnabled = slot.Recovers,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                };
                slot.Connection = await factory.CreateConnectionAsync(ct);
            }

            return slot.Connection;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private sealed class Slot(string name, bool recovers)
    {
        public string Name { get; } = name;

        public bool Recovers { get; } = recovers;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IConnection? Connection { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in new[] { _publisher.Connection, _consumer.Connection })
        {
            if (connection is not null)
            {
                try
                {
                    await connection.CloseAsync();
                }
                catch (Exception)
                {
                    // Closing a broken connection: nothing left to clean up.
                }

                connection.Dispose();
            }
        }

        _publisher.Gate.Dispose();
        _consumer.Gate.Dispose();
    }
}

/// <summary>Reads and writes AMQP headers (RabbitMQ.Client 7 returns string values as byte arrays).</summary>
public static class MessageHeaders
{
    public static string? GetString(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                null => null,
                _ => value.ToString(),
            }
            : null;

    public static int? GetInt(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value)
            ? value switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                byte b => b,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => null,
            }
            : null;
}
