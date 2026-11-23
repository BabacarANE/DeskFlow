using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Desk.IntegrationTests.Support;

/// <summary>
/// One RabbitMQ container for the run, started the first time a test needs it (the other test classes of the shared
/// collection never pay for it). Every test gets its own virtual host: the real queue names are used, nothing leaks.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // Fixed host ports: a stopped and restarted container must come back where the Worker expects it.
    private readonly int _amqpPort = FreePort();
    private readonly int _managementPort = FreePort();
    private readonly RabbitMqContainer _container;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    public RabbitMqFixture() =>
        _container = new RabbitMqBuilder("rabbitmq:4-management-alpine").WithUsername("guest").WithPassword("guest")
            .WithPortBinding(_amqpPort, 5672).WithPortBinding(_managementPort, 15672).Build();

    public Task InitializeAsync() => Task.CompletedTask;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string ManagementUrl => $"http://{_container.Hostname}:{_managementPort}/api/";

    private HttpClient Management()
    {
        var http = new HttpClient { BaseAddress = new Uri(ManagementUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        return http;
    }

    /// <summary>Stops the broker (the container keeps its disk: durable queues and persistent messages survive).</summary>
    public Task StopBrokerAsync() => Docker("stop", "-t", "5");

    public async Task StartBrokerAsync()
    {
        await Docker("start");
        await Poll.UntilAsync(async () =>
        {
            try
            {
                using var http = Management();
                if (!(await http.GetAsync("overview")).IsSuccessStatusCode)
                {
                    return false;
                }

                var factory = new ConnectionFactory { Uri = new Uri($"amqp://guest:guest@{_container.Hostname}:{_amqpPort}/") };
                await using var connection = await factory.CreateConnectionAsync();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }, "broker back after restart", 90);
    }

    private async Task Docker(params string[] arguments)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add(_container.Id);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    public async Task DisposeAsync()
    {
        if (_started)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<TestBroker> NewBrokerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started)
            {
                await _container.StartAsync();
                _started = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        var vhost = "t" + Guid.NewGuid().ToString("N");
        using var http = Management();
        (await http.PutAsync($"vhosts/{vhost}", JsonContent.Create(new { }))).EnsureSuccessStatusCode();
        (await http.PutAsync($"permissions/{vhost}/guest", JsonContent.Create(new { configure = ".*", write = ".*", read = ".*" }))).EnsureSuccessStatusCode();

        return new TestBroker($"amqp://guest:guest@{_container.Hostname}:{_amqpPort}/{vhost}", ManagementUrl, vhost);
    }
}

/// <summary>A message read back from a queue, with its headers decoded.</summary>
public sealed record QueuedMessage(string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

/// <summary>Test-side view of one virtual host.</summary>
public sealed class TestBroker(string uri, string managementUrl, string vhost) : IAsyncDisposable
{
    private IConnection? _connection;

    public string Uri { get; } = uri;

    private async Task<IConnection> ConnectionAsync()
    {
        if (_connection is { IsOpen: false })
        {
            _connection.Dispose();
            _connection = null;
        }

        return _connection ??= await new ConnectionFactory { Uri = new Uri(Uri) }.CreateConnectionAsync();
    }

    /// <summary>Closes, from the broker side, the connections opened with this client-provided name (a server-side close).</summary>
    public async Task<int> CloseConnectionsAsync(string clientName)
    {
        using var http = new HttpClient { BaseAddress = new Uri(managementUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        // The management statistics lag by a few seconds (default emission interval 5 s): wait for the connection to be listed.
        var closed = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (closed == 0 && DateTime.UtcNow < deadline)
        {
            var connections = await http.GetFromJsonAsync<JsonElement>($"vhosts/{vhost}/connections");
            foreach (var connection in connections.EnumerateArray())
            {
                if (connection.GetProperty("client_properties").TryGetProperty("connection_name", out var name) && name.GetString() == clientName)
                {
                    (await http.DeleteAsync("connections/" + System.Uri.EscapeDataString(connection.GetProperty("name").GetString()!))).EnsureSuccessStatusCode();
                    closed++;
                }
            }

            if (closed == 0)
            {
                await Task.Delay(500);
            }
        }

        return closed;
    }

    /// <summary>Puts many messages in a queue at once (default exchange), without waiting for confirms.</summary>
    public async Task FillQueueAsync(string queue, int count, Func<int, (byte[] Body, Dictionary<string, object?> Headers)> factory)
    {
        await using var channel = await ChannelAsync();
        for (var i = 0; i < count; i++)
        {
            var (body, headers) = factory(i);
            await channel.BasicPublishAsync(string.Empty, queue, false, new BasicProperties { Persistent = true, Headers = headers }, body);
        }
    }

    public async Task<IChannel> ChannelAsync() => await (await ConnectionAsync()).CreateChannelAsync();

    public async Task PublishAsync(
        string exchange, string routingKey, byte[] body, int attempt = 0, string? tenantId = null, string? messageId = null,
        IReadOnlyDictionary<string, object?>? extraHeaders = null)
    {
        await using var channel = await (await ConnectionAsync()).CreateChannelAsync(new CreateChannelOptions(true, true));
        var headers = new Dictionary<string, object?> { ["attempt"] = attempt };
        if (tenantId is not null)
        {
            headers["tenant-id"] = tenantId;
        }

        if (messageId is not null)
        {
            headers["message-id"] = messageId;
        }

        foreach (var (key, value) in extraHeaders ?? new Dictionary<string, object?>())
        {
            headers[key] = value;
        }

        await channel.BasicPublishAsync(exchange, routingKey, false,
            new BasicProperties { Persistent = true, ContentType = "application/json", Headers = headers }, body);
    }

    public async Task<uint> CountAsync(string queue)
    {
        await using var channel = await ChannelAsync();
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0; // The queue does not exist (yet).
        }
    }

    public async Task<uint> ConsumersAsync(string queue)
    {
        await using var channel = await ChannelAsync();
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue)).ConsumerCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0;
        }
    }

    public async Task<bool> ExistsAsync(string queueOrExchange, bool exchange = false)
    {
        await using var channel = await ChannelAsync();
        try
        {
            if (exchange)
            {
                await channel.ExchangeDeclarePassiveAsync(queueOrExchange);
            }
            else
            {
                await channel.QueueDeclarePassiveAsync(queueOrExchange);
            }

            return true;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return false;
        }
    }

    /// <summary>Reads up to <paramref name="max"/> messages without removing them (they are requeued).</summary>
    public async Task<List<QueuedMessage>> PeekAsync(string queue, int max = 1000)
    {
        await using var channel = await ChannelAsync();
        var result = new List<QueuedMessage>();
        ulong last = 0;
        while (result.Count < max && await channel.BasicGetAsync(queue, autoAck: false) is { } got)
        {
            last = got.DeliveryTag;
            var headers = (got.BasicProperties.Headers ?? new Dictionary<string, object?>())
                .ToDictionary(h => h.Key, h => h.Value switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    null => "",
                    var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                });
            result.Add(new QueuedMessage(Encoding.UTF8.GetString(got.Body.Span), headers));
        }

        if (last > 0)
        {
            await channel.BasicNackAsync(last, multiple: true, requeue: true);
        }

        return result;
    }

    public async Task BindSinkAsync(string queue, string exchange, string routingKey)
    {
        await using var channel = await ChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, exchange, routingKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync();
            }
            catch (Exception)
            {
                // The broker may be down or restarting: nothing to close.
            }

            _connection.Dispose();
        }
    }
}

/// <summary>Mailpit (the development SMTP server): a real SMTP conversation and the same HTTP API as the docker-compose service.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.34.0")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private HttpClient? _http;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_started)
        {
            await _container.DisposeAsync();
        }
    }

    public string SmtpHost => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(1025);

    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started)
            {
                await _container.StartAsync();
                _http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8025)}/") };
                _started = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Messages received by this address (tests use unique addresses: the server is shared).</summary>
    public async Task<List<MailpitMessage>> MessagesToAsync(string address)
    {
        var json = await _http!.GetFromJsonAsync<JsonElement>("api/v1/messages?limit=500");
        var found = new List<MailpitMessage>();
        foreach (var item in json.GetProperty("messages").EnumerateArray())
        {
            if (item.GetProperty("To").EnumerateArray().Any(t => string.Equals(t.GetProperty("Address").GetString(), address, StringComparison.OrdinalIgnoreCase)))
            {
                var detail = await _http!.GetFromJsonAsync<JsonElement>($"api/v1/message/{item.GetProperty("ID").GetString()}");
                found.Add(new MailpitMessage(
                    item.GetProperty("Subject").GetString() ?? "",
                    detail.GetProperty("Text").GetString() ?? "",
                    detail.GetProperty("HTML").GetString() ?? ""));
            }
        }

        return found;
    }
}

public sealed record MailpitMessage(string Subject, string Text, string Html);

public static class Poll
{
    /// <summary>Polls until the condition holds; fails the test with the description when the time is up.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string description, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timeout ({seconds}s): {description}");
    }

    public static Task UntilAsync(Func<bool> condition, string description, int seconds = 20) =>
        UntilAsync(() => Task.FromResult(condition()), description, seconds);
}
