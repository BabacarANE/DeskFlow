using System.Collections.Concurrent;
using System.Diagnostics;
using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Core.Tenancy;
using Desk.Infrastructure;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Messaging;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Desk.IntegrationTests.Support;

/// <summary>Collects log lines: lets a test wait for "duplicate ignored" instead of sleeping.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(Lines);

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>What a test consumer saw: the tenant of its scope and the tickets it could read.</summary>
public sealed class ProbeLog
{
    public ConcurrentQueue<(Guid? Tenant, Guid MessageTenant, int VisibleTickets)> Seen { get; } = new();
}

/// <summary>Test-only consumer: reports the ambient tenant and counts the tickets visible through the global filter.</summary>
[Consumer("probe", ConsumerHost.Worker, EventTypes.TicketCreated)]
public sealed class ProbeConsumer(DeskDbContext db, ITenantContext tenant, ProbeLog log) : DeskConsumer
{
    public override async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var payload = message.GetPayload<TicketCreatedPayload>();
        var visible = await db.Tickets.CountAsync(t => t.Id == payload.TicketId, ct);
        log.Seen.Enqueue((tenant.TenantId, message.TenantId, visible));
    }
}

/// <summary>The Worker's wiring (AddDeskWorker), in process, on the test's database and virtual host.</summary>
public sealed class TestWorker : IAsyncDisposable
{
    private readonly IHost _host;

    private TestWorker(IHost host) => _host = host;

    public FakeEmailSender Email => _host.Services.GetRequiredService<FakeEmailSender>();

    public CapturingLoggerProvider Logs => _host.Services.GetRequiredService<CapturingLoggerProvider>();

    public ProbeLog Probe => _host.Services.GetRequiredService<ProbeLog>();

    public static async Task<TestWorker> StartAsync(
        string postgres, TestBroker broker, Dictionary<string, string?>? settings = null,
        bool consumers = true, bool dispatcher = true, bool probe = false, bool waitForConsumers = true)
    {
        var builder = Host.CreateApplicationBuilder();
        var all = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres,
            ["ConnectionStrings:RabbitMq"] = broker.Uri,
            ["Messaging:RetryDelays:0"] = "00:00:00.4",
            ["Messaging:RetryDelays:1"] = "00:00:00.8",
            ["Messaging:RetryDelays:2"] = "00:00:01.2",
            ["App:PublicBaseUrl"] = "http://deskflow.test",
        };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(all);
        builder.Services.AddDeskWorker(builder.Configuration);

        var email = new FakeEmailSender();
        builder.Services.AddSingleton(email);
        builder.Services.RemoveAll<IEmailSender>();
        builder.Services.AddSingleton<IEmailSender>(email);

        var logs = new CapturingLoggerProvider();
        builder.Services.AddSingleton(logs);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<ProbeLog>();

        if (probe)
        {
            builder.Services.AddDeskConsumer<ProbeConsumer>();
        }

        if (!consumers)
        {
            builder.Services.RemoveAll<ConsumerDefinition>();
            RemoveHosted<ConsumerHostedService>(builder.Services);
        }

        if (!dispatcher)
        {
            RemoveHosted<OutboxDispatcherService>(builder.Services);
        }

        var host = builder.Build();
        await host.StartAsync();
        var worker = new TestWorker(host);
        if (consumers && waitForConsumers)
        {
            await host.Services.GetRequiredService<ConsumerReadiness>().Ready.WaitAsync(TimeSpan.FromSeconds(30));
        }

        return worker;
    }

    private static void RemoveHosted<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    public async Task<T> WithDbAsync<T>(Func<DeskDbContext, Task<T>> action)
    {
        using var scope = _host.Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginUnscoped();
        return await action(scope.ServiceProvider.GetRequiredService<DeskDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

public sealed record SeededTicket(Guid TenantId, Guid RequesterId, string RequesterEmail, Guid TicketId, int Number, string Title);

/// <summary>Base of the messaging tests: own database (migrated), own virtual host.</summary>
[Collection("postgres")]
public abstract class MessagingTestBase(PostgresFixture postgres, RabbitMqFixture rabbit) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = [];

    protected string ConnectionString { get; private set; } = "";

    protected TestBroker Broker { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        ConnectionString = await postgres.CreateDatabaseAsync();
        Broker = await rabbit.NewBrokerAsync();
        _disposables.Add(Broker);

        var options = new DbContextOptionsBuilder<DeskDbContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new DeskDbContext(options, new TenantContext());
        await db.Database.MigrateAsync();
    }

    public virtual async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }
    }

    protected async Task<TestWorker> StartWorkerAsync(
        Dictionary<string, string?>? settings = null, bool consumers = true, bool dispatcher = true, bool probe = false)
    {
        var worker = await TestWorker.StartAsync(ConnectionString, Broker, settings, consumers, dispatcher, probe);
        _disposables.Add(worker);
        return worker;
    }

    protected DeskDbContext OpenDb(out IDisposable scope)
    {
        var tenant = new TenantContext();
        scope = tenant.BeginUnscoped();
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseNpgsql(ConnectionString)
            .AddInterceptors(new TenantSaveChangesInterceptor(tenant)).Options;
        return new DeskDbContext(options, tenant);
    }

    /// <summary>A company, its requester and one ticket, written directly (no API).</summary>
    protected async Task<SeededTicket> SeedTicketAsync(string slug, string title = "VPN en panne")
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        var tenant = new Tenant { Name = "Entreprise " + slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
        var requester = new User
        {
            TenantId = tenant.Id, Email = $"req@{slug}.test", DisplayName = "Camille Demandeur", Role = Role.Requester, CreatedAt = DateTimeOffset.UtcNow,
        };
        var ticket = new Ticket
        {
            TenantId = tenant.Id, Number = 1, Title = title, Description = "Impossible de me connecter.",
            RequesterId = requester.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        db.Users.Add(requester);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return new SeededTicket(tenant.Id, requester.Id, requester.Email, ticket.Id, ticket.Number, ticket.Title);
    }

    protected static MessageEnvelope TicketCreated(SeededTicket ticket, Guid? messageId = null, Guid? tenantId = null, string correlationId = "corr-test") =>
        MessageEnvelope.Create(messageId ?? Guid.NewGuid(), EventTypes.TicketCreated, tenantId ?? ticket.TenantId,
            DateTimeOffset.UtcNow, correlationId,
            new TicketCreatedPayload(ticket.TicketId, ticket.Number, ticket.Title, Priority.Normal, ticket.RequesterId));

    protected Task PublishAsync(MessageEnvelope envelope, int attempt = 0) =>
        Broker.PublishAsync(MessagingNames.EventsExchange, envelope.Type, envelope.ToUtf8Json(), attempt,
            envelope.TenantId.ToString(), envelope.MessageId.ToString());

    protected async Task AddToOutboxAsync(params MessageEnvelope[] envelopes)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        foreach (var envelope in envelopes)
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = envelope.MessageId, Type = envelope.Type, Payload = envelope.ToJson(), CreatedAt = envelope.OccurredAt,
            });
        }

        await db.SaveChangesAsync();
    }

    protected async Task<int> CountAsync<T>(Func<DeskDbContext, IQueryable<T>> query)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        return await query(db).CountAsync();
    }

    protected async Task WaitForDlqAsync(int count, int seconds = 30) =>
        await Poll.UntilAsync(async () => await Broker.CountAsync(MessagingNames.DlqQueue) >= count, $"{count} message(s) in {MessagingNames.DlqQueue}", seconds);

    protected static Stopwatch StartTimer() => Stopwatch.StartNew();
}
