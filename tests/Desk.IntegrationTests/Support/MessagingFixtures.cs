using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management-alpine").WithUsername("guest").WithPassword("guest").WithPortBinding(15672, true).Build();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    public Task InitializeAsync() => Task.CompletedTask;

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
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}/api/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        (await http.PutAsync($"vhosts/{vhost}", JsonContent.Create(new { }))).EnsureSuccessStatusCode();
        (await http.PutAsync($"permissions/{vhost}/guest", JsonContent.Create(new { configure = ".*", write = ".*", read = ".*" }))).EnsureSuccessStatusCode();

        var uri = $"amqp://guest:guest@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}/{vhost}";
        return new TestBroker(uri);
    }
}

/// <summary>A message read back from a queue, with its headers decoded.</summary>
public sealed record QueuedMessage(string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

/// <summary>Test-side view of one virtual host.</summary>
public sealed class TestBroker(string uri) : IAsyncDisposable
{
    private IConnection? _connection;

    public string Uri { get; } = uri;

    private async Task<IConnection> ConnectionAsync() =>
        _connection ??= await new ConnectionFactory { Uri = new Uri(Uri) }.CreateConnectionAsync();

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
            await _connection.CloseAsync();
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
