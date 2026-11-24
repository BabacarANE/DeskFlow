using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Desk.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.IntegrationTests.Support;

/// <summary>Wraps the real publisher and refuses (throws) the publications selected by <see cref="FailWhen"/>.</summary>
public sealed class FaultyPublisher(IMessagePublisher inner) : IMessagePublisher
{
    private int _failures;

    public Func<OutgoingMessage, bool> FailWhen { get; set; } = _ => false;

    public int Failures => Volatile.Read(ref _failures);

    public Task<bool> PublishAsync(OutgoingMessage message, CancellationToken ct)
    {
        if (FailWhen(message))
        {
            Interlocked.Increment(ref _failures);
            throw new InvalidOperationException("Injected publication failure (broker nack).");
        }

        return inner.PublishAsync(message, ct);
    }

    /// <summary>Replaces the publisher of a test host by a faulty one and hands it back.</summary>
    public static Action<IServiceCollection> Install(out Func<FaultyPublisher> get)
    {
        FaultyPublisher? created = null;
        get = () => created!;
        return services =>
        {
            services.RemoveAll<IMessagePublisher>();
            services.AddSingleton<IMessagePublisher>(sp => created = new FaultyPublisher(sp.GetRequiredService<RabbitPublisher>()));
        };
    }
}

/// <summary>A TCP relay in front of the broker that can be cut at any moment (a network failure in the middle of a command).</summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<TcpClient> _sockets = [];
    private readonly string _host;
    private readonly int _port;
    private readonly Task _accept;

    public TcpProxy(string brokerUri)
    {
        var uri = new Uri(brokerUri);
        (_host, _port) = (uri.Host, uri.Port);
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The same URI as the broker's, through the proxy.</summary>
    public string Through(string brokerUri) => new UriBuilder(brokerUri) { Host = "127.0.0.1", Port = Port }.Uri.ToString().TrimEnd('/');

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                var upstream = new TcpClient();
                await upstream.ConnectAsync(_host, _port);
                _sockets.Add(client);
                _sockets.Add(upstream);
                _ = Pump(client, upstream);
                _ = Pump(upstream, client);
            }
        }
        catch (Exception)
        {
            // Listener stopped.
        }
    }

    private static async Task Pump(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception)
        {
            // Cut.
        }
        finally
        {
            from.Close();
            to.Close();
        }
    }

    /// <summary>Drops every connection and refuses new ones.</summary>
    public void Cut()
    {
        _listener.Stop();
        foreach (var socket in _sockets)
        {
            socket.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cut();
        await _accept;
    }
}
