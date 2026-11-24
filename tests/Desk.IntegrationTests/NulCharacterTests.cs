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
    public void Invalid_utf8_in_a_value_or_a_property_name_is_left_to_the_binder()
    {
        byte[] inValue = [.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8];
        byte[] inName = [.. "{\"a"u8, 0xFF, .. "\":\"x\"}"u8];
        Assert.Null(Desk.Api.Http.NulCharacterGuard.FindNul(inValue));
        Assert.Null(Desk.Api.Http.NulCharacterGuard.FindNul(inName));
    }

    [Fact]
    public void Clean_malformed_and_empty_inputs_have_no_finding()
    {
        Assert.Null(Find("{\"title\":\"ok\",\"n\":[1,true,null,{\"x\":\"y\"}]}"));
        Assert.Null(Find("{\"title\":"));
        Assert.Null(Find("not json"));
        Assert.Null(Find(""));
        Assert.Null(Find(new string('[', 200) + new string(']', 200)));
        Assert.Null(Find("{\"a\":\"back\\\\u0000slash\"}"));
    }
}

public class NulCharacterEdgeTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static Task<HttpResponseMessage> Raw(Session s, string url, byte[] body, HttpMethod? method = null)
    {
        var request = s.Request(method ?? HttpMethod.Post, url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return s.Http.SendAsync(request);
    }

    private static byte[] Bytes(string prefix, byte[] middle, string suffix) =>
        [.. Encoding.UTF8.GetBytes(prefix), .. middle, .. Encoding.UTF8.GetBytes(suffix)];

    [Fact]
    public async Task Invalid_utf8_deep_or_huge_bodies_and_NUL_names_never_give_a_500()
    {
        var c = await BuildCompanyAsync("nul-edge");
        var agent = c.Agent.Session;
        byte[] bad = [0xFF, 0xFE];

        // Invalid UTF-8 in a value, in a property name, and at the top level: the binder answers 400.
        foreach (var body in new[]
        {
            Bytes("{\"title\":\"", bad, "\",\"symptom\":\"s\",\"cause\":\"c\",\"solution\":\"v\",\"tags\":[]}"),
            Bytes("{\"ti", bad, "tle\":\"x\"}"),
            Bytes("{\"a\":\"", bad, "\"}"),
            Bytes("\"", bad, "\""),
        })
        {
            var response = await Raw(agent, "/api/v1/articles", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // A document nested beyond the reader limit: 400 as well.
        var deep = new string('[', 200) + new string(']', 200);
        Assert.Equal(HttpStatusCode.BadRequest, (await Raw(agent, "/api/v1/articles", Encoding.UTF8.GetBytes(deep))).StatusCode);
        var deepObject = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        Assert.Equal(HttpStatusCode.BadRequest, (await Raw(agent, "/api/v1/articles", Encoding.UTF8.GetBytes(deepObject))).StatusCode);

        // A body over 1 MB is refused with 413.
        var huge = Encoding.UTF8.GetBytes("{\"title\":\"" + new string('a', 1_100_000) + "\"}");
        var tooBig = await Raw(agent, "/api/v1/articles", huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

        // A NUL in a property name and in a query key: 422 with the cleaned name.
        var nameResponse = await Raw(agent, "/api/v1/articles", Encoding.UTF8.GetBytes("{\"ti\\u0000tle\":\"x\"}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nameResponse.StatusCode);
        Assert.True((await JsonAsync(nameResponse)).GetProperty("errors").TryGetProperty("title", out _));
        var keyResponse = await agent.GetAsync("/api/v1/articles?%00q=1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, keyResponse.StatusCode);
        Assert.True((await JsonAsync(keyResponse)).GetProperty("errors").TryGetProperty("query", out _));

        // Nothing was written and the API still answers.
        Assert.Equal(0, await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/articles")).StatusCode);
    }

    /// <summary>A body whose length is unknown: sent chunked, no Content-Length.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Empty_bodies_a_byte_order_mark_and_chunked_bodies_are_handled_like_the_binder_would()
    {
        var c = await BuildCompanyAsync("nul-empty");
        var agent = c.Agent.Session;
        var valid = "{\"title\":\"Titre valide\",\"symptom\":\"s\",\"cause\":\"c\",\"solution\":\"v\",\"tags\":[]}"u8.ToArray();
        var article = await JsonAsync(await agent.PostAsync("/api/v1/articles", new { title = "Titre", symptom = "s", cause = "c", solution = "v", tags = Array.Empty<string>() }));
        var id = article.GetProperty("id").GetGuid();

        // Empty body with a JSON content type: endpoints without a body behave as before (no 400 from the guard).
        var publish = await Raw(agent, $"/api/v1/articles/{id}/publish", []);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var unpublish = await Raw(agent, $"/api/v1/articles/{id}/unpublish", []);
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);
        var list = await Raw(agent, "/api/v1/articles", [], HttpMethod.Get);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var logout = agent.Request(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", $"deskflow_refresh={agent.RefreshCookie}");
        logout.Content = new ByteArrayContent([]);
        logout.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Equal(HttpStatusCode.NoContent, (await Http.SendAsync(logout)).StatusCode);

        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = new ByteArrayContent([]) };
        refresh.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        refresh.Headers.Add("Cookie", $"deskflow_refresh={c.Admin.Session.RefreshCookie}");
        Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(refresh)).StatusCode);

        // An endpoint that needs a body keeps the framework's answer for a missing one: the guard stays out of it. Measured
        // with the guard unregistered, the answer is the same (a 500 of the framework itself, not caused by the guard).
        var missing = await Raw(agent, "/api/v1/articles", []);
        Assert.Equal(HttpStatusCode.InternalServerError, missing.StatusCode);
        Assert.DoesNotContain("UTF-8", await missing.Content.ReadAsStringAsync());
        Assert.DoesNotContain("JSON valide", await missing.Content.ReadAsStringAsync());

        // A byte order mark before a valid document is accepted by the guard and the binder.
        var withBom = await Raw(agent, "/api/v1/articles", [0xEF, 0xBB, 0xBF, .. valid]);
        Assert.Equal(HttpStatusCode.Created, withBom.StatusCode);
        var bomNul = await Raw(agent, "/api/v1/articles", [0xEF, 0xBB, 0xBF, .. "{\"title\":\"a\\u0000b\"}"u8]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bomNul.StatusCode);

        // Chunked (no Content-Length): a valid body goes through, an oversized one is a 413.
        var chunked = agent.Request(HttpMethod.Post, "/api/v1/articles");
        chunked.Content = new UnknownLengthContent(valid);
        chunked.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Null(chunked.Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.Created, (await agent.Http.SendAsync(chunked)).StatusCode);

        var chunkedHuge = agent.Request(HttpMethod.Post, "/api/v1/articles");
        chunkedHuge.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes("{\"title\":\"" + new string('a', 1_100_000) + "\"}"));
        chunkedHuge.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await agent.Http.SendAsync(chunkedHuge)).StatusCode);
    }
}
