using System.Net;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>The routes exposed by the API must be exactly the paths and methods of the frozen contract.</summary>
public class OpenApiContractTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly HashSet<string> HttpMethods = ["get", "post", "put", "patch", "delete"];

    private static string FindContractFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "api", "openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/api/openapi.json not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> Operations(JsonElement document, string prefix)
    {
        var result = new HashSet<string>();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject().Where(m => HttpMethods.Contains(m.Name)))
            {
                result.Add($"{method.Name.ToUpperInvariant()} {prefix}{path.Name.TrimEnd('/')}");
            }
        }

        return result;
    }

    [Fact]
    public async Task ExposedOpenApi_MatchesTheFrozenContract()
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(FindContractFile()));
        var response = await Http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var exposed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var server = contract.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()!;
        var expected = Operations(contract.RootElement, server);
        var actual = Operations(exposed.RootElement, "")
            .Where(o => o.Split(' ')[1].StartsWith(server + "/", StringComparison.Ordinal))
            .ToHashSet();

        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public async Task EveryContractRoute_IsRoutedByTheApi()
    {
        // Independent of the generated document: ask the real routing table.
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(FindContractFile()));
        var endpoints = Factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(m => $"{m} {Normalize(e.RoutePattern.RawText!)}"))
            .ToHashSet();

        foreach (var operation in Operations(contract.RootElement, "/api/v1"))
        {
            Assert.Contains(operation, endpoints);
        }
    }

    private static string Normalize(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+):\w+\}", "{$1}").TrimEnd('/');
}
