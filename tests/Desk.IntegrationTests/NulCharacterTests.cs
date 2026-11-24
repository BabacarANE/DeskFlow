using System.Net;
using System.Text;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>
/// U+0000 cannot be stored in a PostgreSQL text column (22021): every text input that carries one must answer 422 with the
/// field in <c>errors</c>, never 500, and nothing must be written.
/// </summary>
public class NulCharacterTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private const string Nul = "\0";

    private static object Article(string? title = null, string? symptom = null, string? cause = null, string? solution = null, string[]? tags = null) =>
        new { title = title ?? "Titre valide", symptom = symptom ?? "Symptôme", cause = cause ?? "Cause", solution = solution ?? "Solution", tags = tags ?? ["tag"] };

    private static async Task AssertRefusedAsync(Task<HttpResponseMessage> request, string field, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{what}: expected 422, got {(int)response.StatusCode}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"{what}: errors.{field} in {text}");
    }

    [Fact]
    public async Task A_NUL_character_in_any_text_input_is_a_422_with_the_field_and_writes_nothing()
    {
        var c = await BuildCompanyAsync("nul");
        var root = await SignInSuperAdminAsync();
        var agent = c.Agent.Session;
        var admin = c.Admin.Session;
        var requester = c.Requester.Session;

        var ticket = await CreateTicketAsync(requester);
        var ticketId = ticket.GetProperty("id").GetGuid();
        var category = await CreateCategoryAsync(admin, "Réseau");
        var article = await JsonAsync(await agent.PostAsync("/api/v1/articles", Article()));
        var articleId = article.GetProperty("id").GetGuid();
        var ticketRowVersion = (await GetTicketAsync(agent, ticketId)).GetProperty("rowVersion").GetString();

        // Articles: the search text and every field of the body, on create and update.
        foreach (var session in new[] { agent, requester })
        {
            await AssertRefusedAsync(session.GetAsync("/api/v1/articles?q=%00ab"), "q", "articles q");
            await AssertRefusedAsync(session.GetAsync("/api/v1/articles?q=ab%00"), "q", "articles q end");
        }

        await AssertRefusedAsync(agent.GetAsync("/api/v1/articles?status=Dra%00ft"), "status", "articles status");
        await AssertRefusedAsync(agent.GetAsync("/api/v1/articles?page=1%00"), "page", "articles page");
        foreach (var (field, body) in new (string, object)[]
        {
            ("title", Article(title: "Ti" + Nul + "tre")),
            ("symptom", Article(symptom: "a" + Nul)),
            ("cause", Article(cause: Nul)),
            ("solution", Article(solution: "x" + Nul + "y")),
            ("tags[0]", Article(tags: ["ok" + Nul])),
        })
        {
            await AssertRefusedAsync(agent.PostAsync("/api/v1/articles", body), field, "POST article " + field);
            await AssertRefusedAsync(agent.PutAsync($"/api/v1/articles/{articleId}", body), field, "PUT article " + field);
        }

        // Tickets: title, description, comment, filters.
        await AssertRefusedAsync(requester.PostAsync("/api/v1/tickets", new { title = "VPN" + Nul, description = "ok" }), "title", "ticket title");
        await AssertRefusedAsync(requester.PostAsync("/api/v1/tickets", new { title = "VPN", description = "o" + Nul + "k" }), "description", "ticket description");
        await AssertRefusedAsync(requester.PostAsync("/api/v1/tickets", new { title = "VPN", description = "ok", priority = "High" + Nul }), "priority", "ticket priority");
        await AssertRefusedAsync(agent.PostAsync($"/api/v1/tickets/{ticketId}/comments", new { body = "texte" + Nul }), "body", "comment");
        await AssertRefusedAsync(agent.PatchAsync($"/api/v1/tickets/{ticketId}", new { rowVersion = ticketRowVersion + Nul, status = "Open" }), "rowVersion", "ticket rowVersion");
        await AssertRefusedAsync(requester.GetAsync("/api/v1/tickets?q=%00ab"), "q", "tickets q");
        await AssertRefusedAsync(agent.GetAsync("/api/v1/tickets?status=Op%00en"), "status", "tickets status");
        await AssertRefusedAsync(agent.GetAsync("/api/v1/tickets?sort=number%00"), "sort", "tickets sort");

        // Categories, theme, SLA, invitations, tenants.
        await AssertRefusedAsync(admin.PostAsync("/api/v1/categories", new { name = "Ré" + Nul + "seau" }), "name", "category create");
        await AssertRefusedAsync(admin.PutAsync($"/api/v1/categories/{category}", new { name = "Ré" + Nul }), "name", "category update");
        await AssertRefusedAsync(admin.PutAsync("/api/v1/theme", new { colorPrimary = "#112233" + Nul, colorSurface = "#FFFFFF", colorAccent = "#000000" }), "colorPrimary", "theme");
        await AssertRefusedAsync(admin.PutAsync("/api/v1/sla-policies", new
        {
            policies = new object[]
            {
                new { priority = "Low" + Nul, firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "Normal", firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "High", firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "Urgent", firstResponseMinutes = 1, resolutionMinutes = 2 },
            },
        }), "policies[0].priority", "sla");
        await AssertRefusedAsync(admin.PostAsync("/api/v1/invitations", new { email = "a" + Nul + "@nul.test", role = "Agent" }), "email", "invitation");
        await AssertRefusedAsync(root.PostAsync("/api/v1/tenants", new { name = "Société" + Nul, slug = "nul-co", adminEmail = "a@nul-co.test" }), "name", "tenant");
        await AssertRefusedAsync(root.PostAsync("/api/v1/tenants", new { name = "Société", slug = "nul-co", adminEmail = "a@nul-co.test" + Nul }), "adminEmail", "tenant e-mail");

        // The refusal is the same for the escaped form written by hand in the JSON text.
        var raw = new HttpRequestMessage(HttpMethod.Post, "/api/v1/articles")
        {
            Content = new StringContent("{\"title\":\"Titre\\u0000\",\"symptom\":\"s\",\"cause\":\"c\",\"solution\":\"v\",\"tags\":[]}", Encoding.UTF8, "application/json"),
        };
        raw.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agent.AccessToken);
        await AssertRefusedAsync(Http.SendAsync(raw), "title", "raw escaped JSON");

        // Nothing was written, and clean requests still work after all these refusals.
        Assert.Equal(1, await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(1, await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0, await QueryDbAsync(db => db.TicketComments.IgnoreQueryFilters().CountAsync()));
        Assert.Equal("Réseau", await QueryDbAsync(db => db.Categories.IgnoreQueryFilters().Select(x => x.Name).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/articles?q=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await requester.GetAsync("/api/v1/tickets?q=vpn")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await agent.PostAsync("/api/v1/articles", Article())).StatusCode);

        // Authorization still comes first: no token is a 401, not a 422.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/articles?q=%00ab")).StatusCode);
    }
}

/// <summary>Path reporting of the JSON scanner (pure, no container).</summary>
public class NulCharacterGuardTests
{
    private static string? Find(string json) => Desk.Api.Http.NulCharacterGuard.FindNul(System.Text.Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Finds_the_path_of_the_first_string_holding_a_NUL()
    {
        Assert.Equal("title", Find("{\"title\":\"a\\u0000b\"}"));
        Assert.Equal("tags[1]", Find("{\"title\":\"ok\",\"tags\":[\"a\",\"b\\u0000\"]}"));
        Assert.Equal("policies[2].priority", Find("{\"policies\":[{\"priority\":\"Low\"},{\"priority\":\"x\"},{\"n\":1,\"priority\":\"\\u0000\"}]}"));
        Assert.Equal("body", Find("\"\\u0000\""));
        Assert.Equal("a.b", Find("{\"a\":{\"b\":\"\\u0000\"}}"));
    }

    [Fact]
    public void Clean_malformed_and_empty_inputs_have_no_finding()
    {
        Assert.Null(Find("{\"title\":\"ok\",\"n\":[1,true,null,{\"x\":\"y\"}]}"));
        Assert.Null(Find("{\"title\":"));
        Assert.Null(Find("not json"));
        Assert.Null(Find(""));
        Assert.Null(Find("{\"a\":\"back\\\\u0000slash\"}"));
    }
}
