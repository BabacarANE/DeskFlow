using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Desk.IntegrationTests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // UseSetting (not ConfigureAppConfiguration): minimal hosting reads config before late overrides apply.
    // Dependencies are unreachable on purpose: readiness must fail, liveness must not depend on them.
    private HttpClient CreateClient() => factory.WithWebHostBuilder(b => b
        .UseSetting("Database:MigrateOnStartup", "false") // no database to migrate: dependencies are down on purpose
        .UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Timeout=1")
        .UseSetting("ConnectionStrings:RabbitMq", "amqp://127.0.0.1:1")).CreateClient();

    [Fact]
    public async Task Liveness_IsOk_WithoutDependencies()
    {
        var response = await CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_Is503_WhenDependenciesAreDown()
    {
        var response = await CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
