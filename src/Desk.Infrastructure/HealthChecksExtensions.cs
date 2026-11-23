using Desk.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Desk.Infrastructure;

public static class HealthChecksExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>Registers PostgreSQL and RabbitMQ checks, tagged "ready".</summary>
    public static IServiceCollection AddDeskHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
        var rabbit = configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not configured.");

        services.AddHealthChecks()
            .AddCheck("postgres", new PostgresHealthCheck(postgres), HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck("rabbitmq", new RabbitMqHealthCheck(rabbit), HealthStatus.Unhealthy, [ReadyTag]);
        return services;
    }

    /// <summary>/health = liveness (no dependency), /health/ready = dependencies.</summary>
    public static IEndpointRouteBuilder MapDeskHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag) });
        return endpoints;
    }
}
