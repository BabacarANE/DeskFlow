using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Infrastructure.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Connections, publisher, topology and outbox writer. Enough for a process that only writes to the outbox
    /// (the API); consumers and the dispatcher are added separately.
    /// </summary>
    public static IServiceCollection AddDeskMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MessagingOptions>(configuration.GetSection("Messaging"));
        services.TryAddSingleton<RabbitConnections>();
        services.TryAddSingleton<RabbitPublisher>();
        services.TryAddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitPublisher>());
        services.TryAddSingleton<MessagingTopology>();
        services.TryAddScoped<CorrelationContext>();
        services.TryAddScoped<OutboxWriter>();
        return services;
    }

    /// <summary>Declares the topology in the background at startup (API). The broker being down never blocks the host.</summary>
    public static IServiceCollection AddDeskTopologyStartup(this IServiceCollection services)
    {
        services.AddHostedService<TopologyStartupService>();
        return services;
    }

    /// <summary>
    /// Hosts a consumer in this process. A consumer is one class deriving from <see cref="DeskConsumer"/> with a
    /// <see cref="ConsumerAttribute"/>: this call is its only other declaration.
    /// </summary>
    public static IServiceCollection AddDeskConsumer<T>(this IServiceCollection services) where T : DeskConsumer =>
        services.AddDeskConsumer(typeof(T));

    public static IServiceCollection AddDeskConsumer(this IServiceCollection services, Type consumerType)
    {
        var definition = ConsumerDefinition.For(consumerType);
        services.AddScoped(consumerType);
        services.AddSingleton(definition);
        EnsureConsumerHost(services);
        return services;
    }

    /// <summary>Hosts every consumer of the product declared for this process (<see cref="ConsumerHost"/>).</summary>
    public static IServiceCollection AddDeskConsumers(this IServiceCollection services, ConsumerHost host)
    {
        foreach (var definition in ConsumerDefinition.Scan(typeof(MessagingExtensions).Assembly).Where(d => d.Host == host))
        {
            services.AddDeskConsumer(definition.Type);
        }

        return services;
    }

    /// <summary>Outbox polling loop (every second, <c>FOR UPDATE SKIP LOCKED</c>).</summary>
    public static IServiceCollection AddDeskOutboxDispatcher(this IServiceCollection services)
    {
        services.TryAddSingleton<OutboxDispatcher>();
        services.AddHostedService<OutboxDispatcherService>();
        return services;
    }

    /// <summary>Everything the Worker runs: persistence, e-mail, messaging, its consumers and the outbox dispatcher.</summary>
    public static IServiceCollection AddDeskWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDeskPersistence(configuration);
        services.AddDeskEmail(configuration);
        services.AddDeskMessaging(configuration);
        services.AddDeskConsumers(ConsumerHost.Worker);
        services.AddDeskOutboxDispatcher();
        return services;
    }

    /// <summary>The <c>replay-dlq</c> command only needs the broker: no database, no background service.</summary>
    public static IServiceCollection AddDeskReplay(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDeskMessaging(configuration);
        services.TryAddSingleton<DlqReplayer>();
        return services;
    }

    private static void EnsureConsumerHost(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ConsumerReadiness)))
        {
            return;
        }

        services.AddSingleton<ConsumerReadiness>();
        services.AddSingleton<MessageProcessor>();
        services.AddHostedService<ConsumerHostedService>();
    }
}
