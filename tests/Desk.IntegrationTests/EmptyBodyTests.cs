using System.Net;
using System.Text;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>
/// A route that requires a JSON body answers 400 (ProblemDetails) when the body is empty, blank, the literal null or not
/// JSON, never 500. A well-formed document with bad content stays a 422 (field errors). One mechanism covers every route.
/// </summary>
public class EmptyBodyTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    /// <summary>Every route that binds a required body: method, route template, role of the caller.</summary>
    private static readonly (string Method, string Route, string Role)[] BodyRoutes =
    [
        ("POST", "/api/v1/auth/demo", "anonymous"),
        ("POST", "/api/v1/tenants", "root"),
        ("PATCH", "/api/v1/tenants/{id}", "root"),
        ("PATCH", "/api/v1/users/{id}", "admin"),
        ("POST", "/api/v1/invitations", "admin"),
        ("POST", "/api/v1/categories", "admin"),
        ("PUT", "/api/v1/categories/{id}", "admin"),
        ("POST", "/api/v1/tickets", "requester"),
        ("PATCH", "/api/v1/tickets/{id}", "agent"),
        ("POST", "/api/v1/tickets/{id}/comments", "agent"),
        ("POST", "/api/v1/articles", "agent"),
        ("PUT", "/api/v1/articles/{id}", "agent"),
        ("PUT", "/api/v1/theme", "admin"),
        ("PUT", "/api/v1/sla-policies", "admin"),
    ];

    private static readonly string[] Kinds = ["empty", "emptyNoContentType", "null", "spaces", "newlines", "bom", "nullSpaces", "notJson", "truncated"];

    public static IEnumerable<object[]> Cases() => BodyRoutes.Select(r => new object[] { r.Method, r.Route, r.Role });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_missing_or_unreadable_body_is_a_400_problem_never_a_500(string method, string route, string role)
    {
        var c = await BuildCompanyAsync("body");
        var root = await SignInSuperAdminAsync();
        var session = role switch
        {
            "root" => root,
            "admin" => c.Admin.Session,
            "agent" => c.Agent.Session,
            "requester" => c.Requester.Session,
            _ => null,
        };

        var ticket = await CreateTicketAsync(c.Requester.Session);
        var url = route;
        if (route.Contains("{id}"))
        {
            var id = route switch
            {
                var r when r.Contains("/tenants/") => c.TenantId.ToString(),
                var r when r.Contains("/users/") => c.Requester.Id.ToString(),
                var r when r.Contains("/tickets/") => ticket.GetProperty("id").GetGuid().ToString(),
                var r when r.Contains("/categories/") => (await CreateCategoryAsync(c.Admin.Session, "Existante")).ToString(),
                _ => (await JsonAsync(await c.Agent.Session.PostAsync("/api/v1/articles",
                    new { title = "Titre valide", symptom = "s", cause = "c", solution = "v", tags = Array.Empty<string>() }))).GetProperty("id").GetString()!,
            };
            url = route.Replace("{id}", id);
        }


        var failures = new List<string>();
        foreach (var kind in Kinds)
        {
            var request = session is null
                ? new HttpRequestMessage(new HttpMethod(method), url)
                : session.Request(new HttpMethod(method), url);
            var bytes = kind switch
            {
                "empty" or "emptyNoContentType" => [],
                "null" => "null"u8.ToArray(),
                "spaces" => "   "u8.ToArray(),
                "newlines" => "\r\n\t \n"u8.ToArray(),
                "bom" => new byte[] { 0xEF, 0xBB, 0xBF },
                "nullSpaces" => " null \n"u8.ToArray(),
                "notJson" => "pas du json"u8.ToArray(),
                _ => "{\"title\":"u8.ToArray(),
            };
            request.Content = new ByteArrayContent(bytes);
            if (kind != "emptyNoContentType")
            {
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var ok = response.StatusCode == HttpStatusCode.BadRequest
                && response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                && !text.Contains("Exception", StringComparison.Ordinal);
            if (ok)
            {
                using var json = JsonDocument.Parse(text);
                ok = json.RootElement.GetProperty("status").GetInt32() == 400
                    && !string.IsNullOrWhiteSpace(json.RootElement.GetProperty("detail").GetString());
            }

            if (!ok)
            {
                failures.Add($"[{kind}] {(int)response.StatusCode} {text}");
            }
        }

        Assert.True(failures.Count == 0, $"{method} {route}: expected 400 problem for every body kind:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Every_route_with_a_required_body_is_listed_above_and_unauthenticated_calls_still_get_401()
    {
        var actual = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAcceptsMetadata>() is { IsOptional: false })
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} /{e.RoutePattern.RawText!.TrimStart('/')}"))
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"\{id:guid\}", "{id}").TrimEnd('/'))
            .Order()
            .ToArray();
        var expected = BodyRoutes.Select(r => $"{r.Method} {r.Route}").Order().ToArray();
        Assert.Equal(expected, actual);

        // Authorization still comes first: an empty body without a token is a 401, not a 400.
        var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/v1/articles") { Content = new ByteArrayContent([]) };
        anonymous.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.SendAsync(anonymous)).StatusCode);
    }

    [Fact]
    public async Task Well_formed_bodies_keep_their_answers_and_body_less_routes_ignore_the_check()
    {
        var c = await BuildCompanyAsync("body-ok");
        var agent = c.Agent.Session;

        // A well-formed but incomplete document is still a 422 with field errors.
        var empty = await agent.PostAsync("/api/v1/articles", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Admin.Session.PostAsync("/api/v1/categories", new { name = "  " })).StatusCode);

        // A body of a different JSON type is a 400 from the binder, not a 500.
        foreach (var body in new[] { "[]", "42", "\"texte\"", "true" })
        {
            var request = agent.Request(HttpMethod.Post, "/api/v1/articles");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.SendAsync(request)).StatusCode);
        }

        // Routes without a body keep working with an empty JSON body (publish, logout, refresh), and a valid body is created.
        var created = await agent.PostAsync("/api/v1/articles", new { title = "Titre", symptom = "s", cause = "c", solution = "v", tags = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var publish = agent.Request(HttpMethod.Post, $"/api/v1/articles/{id}/publish");
        publish.Content = new ByteArrayContent([]);
        publish.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(publish)).StatusCode);
    }
}
