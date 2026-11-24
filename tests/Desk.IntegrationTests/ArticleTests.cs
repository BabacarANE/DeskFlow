using System.Net;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>Knowledge base routes (US-17, US-18): roles, visibility, isolation, search, publication, contract.</summary>
public class ArticleTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static readonly ContractSchema Contract = ContractSchema.Load().AsStrict();

    private static object Body(
        string title = "Imprimante hors ligne", string symptom = "La file d'impression est bloquée.",
        string cause = "Le spouleur est planté.", string solution = "Redémarrer le service.", string[]? tags = null) =>
        new { title, symptom, cause, solution, tags = tags ?? ["imprimante"] };

    private static void AssertConforms(JsonElement body, string path, string method, HttpStatusCode status)
    {
        var errors = Contract.Validate(body, Contract.ResponseSchema(path, method, (int)status));
        Assert.True(errors.Count == 0, $"{method.ToUpperInvariant()} {path} {(int)status}: {string.Join("; ", errors)}\n{body.GetRawText()}");
    }

    private static async Task<JsonElement> CreateAsync(Session session, object body)
    {
        var response = await session.PostAsync("/api/v1/articles", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> PublishedAsync(Session session, object body)
    {
        var created = await CreateAsync(session, body);
        var response = await session.PostAsync($"/api/v1/articles/{created.GetProperty("id").GetGuid()}/publish", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> ListAsync(Session session, string query = "")
    {
        var response = await session.GetAsync("/api/v1/articles" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static List<string> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();

    private static async Task AssertStatusAsync(Task<HttpResponseMessage> request, HttpStatusCode expected, string what)
    {
        var response = await request;
        Assert.True(expected == response.StatusCode, $"{what}: expected {(int)expected}, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Role_matrix_and_response_shapes_of_every_route()
    {
        var c = await BuildCompanyAsync("kb-roles");
        var root = await SignInSuperAdminAsync();
        var draft = await CreateAsync(c.Agent.Session, Body(title: "Brouillon visible des agents"));
        var published = await PublishedAsync(c.Admin.Session, Body(title: "Article publié"));
        var draftId = draft.GetProperty("id").GetGuid();
        var publishedId = published.GetProperty("id").GetGuid();

        (HttpMethod Method, string Url, object? Body)[] routes =
        [
            (HttpMethod.Get, "/api/v1/articles", null),
            (HttpMethod.Post, "/api/v1/articles", Body()),
            (HttpMethod.Get, $"/api/v1/articles/{publishedId}", null),
            (HttpMethod.Put, $"/api/v1/articles/{publishedId}", Body()),
            (HttpMethod.Post, $"/api/v1/articles/{publishedId}/publish", new { }),
            (HttpMethod.Post, $"/api/v1/articles/{publishedId}/unpublish", new { }),
        ];

        foreach (var (method, url, body) in routes)
        {
            // No token: 401 everywhere.
            var anonymous = new HttpRequestMessage(method, url);
            if (body is not null) { anonymous.Content = System.Net.Http.Json.JsonContent.Create(body); }
            await AssertStatusAsync(Http.SendAsync(anonymous), HttpStatusCode.Unauthorized, $"anonymous {method} {url}");

            // The SuperAdmin has no company: 403 everywhere.
            await AssertStatusAsync(root.Http.SendAsync(root.Request(method, url, body)), HttpStatusCode.Forbidden, $"superadmin {method} {url}");

            // A Requester only reads.
            var writes = method != HttpMethod.Get;
            await AssertStatusAsync(c.Requester.Session.Http.SendAsync(c.Requester.Session.Request(method, url, body)),
                writes ? HttpStatusCode.Forbidden : HttpStatusCode.OK, $"requester {method} {url}");
        }

        // The requester did not change anything: the published article is still published.
        Assert.Equal("Published", (await JsonAsync(await c.Agent.Session.GetAsync($"/api/v1/articles/{publishedId}"))).GetProperty("status").GetString());

        // Visibility: a draft is invisible to the Requester (list and detail), visible to Agent and TenantAdmin.
        var requesterList = await ListAsync(c.Requester.Session);
        Assert.Equal(["Article publié"], Titles(requesterList));
        Assert.Equal(1, requesterList.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Requester.Session.GetAsync($"/api/v1/articles/{draftId}")).StatusCode);
        foreach (var staff in new[] { c.Agent.Session, c.Admin.Session })
        {
            Assert.Equal(2, (await ListAsync(staff)).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/articles/{draftId}")).StatusCode);
        }

        // The status filter works for staff and is ignored for a Requester.
        Assert.Equal(["Brouillon visible des agents"], Titles(await ListAsync(c.Agent.Session, "?status=Draft")));
        Assert.Equal(["Article publié"], Titles(await ListAsync(c.Agent.Session, "?status=Published")));
        Assert.Equal(["Article publié"], Titles(await ListAsync(c.Requester.Session, "?status=Draft")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Agent.Session.GetAsync("/api/v1/articles?status=Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Requester.Session.GetAsync("/api/v1/articles?status=Nope")).StatusCode);

        // Agent and TenantAdmin write; responses fit the frozen contract exactly (strict).
        foreach (var writer in new[] { c.Agent.Session, c.Admin.Session })
        {
            var created = await writer.PostAsync("/api/v1/articles", Body(tags: ["a", "b"]));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var json = await JsonAsync(created);
            AssertConforms(json, "/articles", "post", HttpStatusCode.Created);
            Assert.Equal("Draft", json.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("sourceTicketId").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("publishedAt").ValueKind);
        }

        AssertConforms(await ListAsync(c.Agent.Session), "/articles", "get", HttpStatusCode.OK);
        AssertConforms(await JsonAsync(await c.Requester.Session.GetAsync($"/api/v1/articles/{publishedId}")), "/articles/{id}", "get", HttpStatusCode.OK);
        var put = await c.Agent.Session.PutAsync($"/api/v1/articles/{draftId}", Body(title: "Titre modifié"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertConforms(await JsonAsync(put), "/articles/{id}", "put", HttpStatusCode.OK);
        var publish = await c.Agent.Session.PostAsync($"/api/v1/articles/{draftId}/publish", new { });
        AssertConforms(await JsonAsync(publish), "/articles/{id}/publish", "post", HttpStatusCode.OK);
        var unpublish = await c.Admin.Session.PostAsync($"/api/v1/articles/{draftId}/unpublish", new { });
        AssertConforms(await JsonAsync(unpublish), "/articles/{id}/unpublish", "post", HttpStatusCode.OK);
        await AssertStatusAsync(c.Agent.Session.GetAsync($"/api/v1/articles/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "unknown id");
    }

    [Fact]
    public async Task A_company_never_sees_nor_changes_the_articles_of_another()
    {
        var alpha = await BuildCompanyAsync("kb-alpha");
        var (_, betaAdmin) = await CreateTenantWithAdminAsync("kb-beta", "admin@kb-beta.test");
        var secret = await PublishedAsync(alpha.Agent.Session, Body(title: "Procédure secrète alpha"));
        var draft = await CreateAsync(alpha.Agent.Session, Body(title: "Brouillon secret alpha"));
        var secretId = secret.GetProperty("id").GetGuid();
        var draftId = draft.GetProperty("id").GetGuid();

        // Reads: lists (with and without search) are empty, details are 404.
        Assert.Equal(0, (await ListAsync(betaAdmin)).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ListAsync(betaAdmin, "?q=" + Uri.EscapeDataString("secrète"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ListAsync(betaAdmin, "?status=Draft")).GetProperty("total").GetInt32());
        foreach (var id in new[] { secretId, draftId })
        {
            await AssertStatusAsync(betaAdmin.GetAsync($"/api/v1/articles/{id}"), HttpStatusCode.NotFound, $"read {id}");
            // Writes: 404 for publish, unpublish and update, with no effect.
            await AssertStatusAsync(betaAdmin.PutAsync($"/api/v1/articles/{id}", Body(title: "Piraté")), HttpStatusCode.NotFound, $"put {id}");
            await AssertStatusAsync(betaAdmin.PostAsync($"/api/v1/articles/{id}/publish", new { }), HttpStatusCode.NotFound, $"publish {id}");
            await AssertStatusAsync(betaAdmin.PostAsync($"/api/v1/articles/{id}/unpublish", new { }), HttpStatusCode.NotFound, $"unpublish {id}");
        }

        var afterSecret = await JsonAsync(await alpha.Agent.Session.GetAsync($"/api/v1/articles/{secretId}"));
        var afterDraft = await JsonAsync(await alpha.Agent.Session.GetAsync($"/api/v1/articles/{draftId}"));
        Assert.Equal(secret.GetRawText(), afterSecret.GetRawText());
        Assert.Equal(draft.GetRawText(), afterDraft.GetRawText());

        // What beta writes stays in beta.
        var own = await CreateAsync(betaAdmin, Body(title: "Article beta"));
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.Admin.Session.GetAsync($"/api/v1/articles/{own.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.DoesNotContain("Article beta", Titles(await ListAsync(alpha.Admin.Session)));
        var rows = await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().Select(a => new { a.Title, a.TenantId }).ToListAsync());
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.Title == "Article beta" && r.TenantId != alpha.TenantId);
    }

    [Fact]
    public async Task A_draft_proposed_from_a_ticket_is_listed_for_agents_with_its_source_ticket()
    {
        var c = await BuildCompanyAsync("kb-source");
        var ticket = await CreateTicketAsync(c.Requester.Session, "Imprimante hors ligne");
        var ticketId = ticket.GetProperty("id").GetGuid();
        // The shape KbDraftConsumer writes (covered by its own tests): ticket title and description, empty cause and
        // solution, author = assignee. The author is disabled afterwards: the API must still display him.
        var articleId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.KnowledgeArticles.Add(new KnowledgeArticle
            {
                Id = articleId,
                TenantId = c.TenantId,
                Title = "Imprimante hors ligne",
                Symptom = "Impossible de me connecter.",
                Cause = "",
                Solution = "",
                Tags = "",
                Status = ArticleStatus.Draft,
                SourceTicketId = ticketId,
                AuthorId = c.Agent.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == c.Agent.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        });

        var listed = (await ListAsync(c.Admin.Session, "?status=Draft")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(articleId, listed.GetProperty("id").GetGuid());
        var detail = await JsonAsync(await c.Admin.Session.GetAsync($"/api/v1/articles/{articleId}"));
        Assert.Equal(ticketId, detail.GetProperty("sourceTicketId").GetGuid());
        Assert.Equal(c.Agent.Id, detail.GetProperty("author").GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("author").GetProperty("displayName").GetString()));
        Assert.Empty(detail.GetProperty("tags").EnumerateArray());
        AssertConforms(detail, "/articles/{id}", "get", HttpStatusCode.OK);

        // The requester sees nothing of it.
        Assert.Equal(0, (await ListAsync(c.Requester.Session)).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Requester.Session.GetAsync($"/api/v1/articles/{articleId}")).StatusCode);

        // An empty solution blocks publication until it is written; the source ticket survives the edit and the publication.
        var blocked = await c.Admin.Session.PostAsync($"/api/v1/articles/{articleId}/publish", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.True((await JsonAsync(blocked)).GetProperty("errors").TryGetProperty("solution", out _));
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.Session.PutAsync($"/api/v1/articles/{articleId}", Body(solution: "Redémarrer le spouleur."))).StatusCode);
        var published = await JsonAsync(await c.Admin.Session.PostAsync($"/api/v1/articles/{articleId}/publish", new { }));
        Assert.Equal(ticketId, published.GetProperty("sourceTicketId").GetGuid());
        Assert.Equal(c.Agent.Id, published.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(1, (await ListAsync(c.Requester.Session)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Search_matches_every_word_case_insensitively_titles_first_and_treats_wildcards_as_text()
    {
        var c = await BuildCompanyAsync("kb-search");
        var agent = c.Agent.Session;
        await PublishedAsync(agent, Body("Imprimante hors ligne", "La file d'impression reste bloquée.", "Spouleur planté", "Redémarrer le service", ["imprimante", "réseau"]));
        await PublishedAsync(agent, Body("Problème réseau", "Imprimante inaccessible depuis le Wifi", "Câble débranché", "Rebrancher", ["wifi"]));
        await PublishedAsync(agent, Body("Taux de 100% fiable", "Symptôme A", "Cause A", "Solution A", []));
        await PublishedAsync(agent, Body("Chemin C:\\temp et snake_case", "Symptôme B", "Cause B", "Solution B", []));
        await PublishedAsync(agent, Body("Texte 100 fiable", "Symptôme C", "Cause C", "Solution C", []));
        await CreateAsync(agent, Body("Brouillon imprimante", "Symptôme D", "Cause D", "Solution D", []));

        async Task<List<string>> Search(string q, Session? who = null) =>
            Titles(await ListAsync(who ?? agent, "?q=" + Uri.EscapeDataString(q)));

        // Case, then title matches first (even though the second article is more recent), drafts after published.
        Assert.Equal(["Imprimante hors ligne", "Brouillon imprimante", "Problème réseau"], await Search("imprimante"));
        Assert.Equal(await Search("imprimante"), await Search("IMPRIMANTE"));
        Assert.Equal(["Imprimante hors ligne", "Problème réseau"], await Search("imprimante", c.Requester.Session));

        // Every word must appear, in any of the fields (title, symptom, cause, solution, tags).
        Assert.Equal(["Imprimante hors ligne"], await Search("imprimante spouleur"));
        Assert.Equal(["Problème réseau"], await Search("imprimante wifi"));
        Assert.Equal(["Imprimante hors ligne"], await Search("redémarrer bloquée"));
        Assert.Equal(["Problème réseau"], await Search("câble débranché"));
        // Both match "imprimante réseau" without the title holding both words: the most recent publication comes first.
        Assert.Equal(["Problème réseau", "Imprimante hors ligne"], await Search("imprimante réseau"));
        Assert.Empty(await Search("imprimante inexistant"));

        // Words under 2 characters are ignored; a query with no useful word is the unfiltered list.
        Assert.Equal(await Search("imprimante"), await Search("x imprimante é"));
        Assert.Equal(6, (await ListAsync(agent, "?q=a")).GetProperty("total").GetInt32());
        Assert.Equal(6, (await ListAsync(agent, "?q=%20%20")).GetProperty("total").GetInt32());

        // %, _ and \ are plain text.
        Assert.Equal(["Taux de 100% fiable"], await Search("100%"));
        Assert.Equal(["Texte 100 fiable", "Taux de 100% fiable"], await Search("100 fiable"));
        Assert.Empty(await Search("%%"));
        Assert.Equal(["Chemin C:\\temp et snake_case"], await Search("snake_case"));
        Assert.Empty(await Search("snake_c_se"));
        Assert.Empty(await Search("sn_ke"));
        Assert.Equal(["Chemin C:\\temp et snake_case"], await Search("C:\\temp"));
        Assert.Empty(await Search("\\\\"));
        Assert.Empty(await Search("C:\\\\temp"));
        // A quote or a SQL fragment is just text.
        Assert.Empty(await Search("'; DROP TABLE \"KnowledgeArticles\"; --"));
        Assert.Equal(6, (await ListAsync(agent)).GetProperty("total").GetInt32());

        // The search applies to the visible set only.
        Assert.Empty(await Search("Brouillon", c.Requester.Session));
    }

    [Fact]
    public async Task Listing_is_paginated_sorted_without_search_and_validates_its_parameters()
    {
        var c = await BuildCompanyAsync("kb-page");
        var agent = c.Agent.Session;
        var first = await PublishedAsync(agent, Body("Premier publié"));
        await PublishedAsync(agent, Body("Deuxième publié"));
        await PublishedAsync(agent, Body("Troisième publié"));
        await CreateAsync(agent, Body("Brouillon ancien"));
        await CreateAsync(agent, Body("Brouillon récent"));

        // Published by PublishedAt desc, then drafts by creation date desc.
        Assert.Equal(["Troisième publié", "Deuxième publié", "Premier publié", "Brouillon récent", "Brouillon ancien"], Titles(await ListAsync(agent)));
        // Re-publishing an article does not move it (idempotent); un-publishing then publishing again does.
        await agent.PostAsync($"/api/v1/articles/{first.GetProperty("id").GetGuid()}/publish", new { });
        Assert.Equal("Troisième publié", Titles(await ListAsync(agent))[0]);

        var page2 = await ListAsync(agent, "?pageSize=2&page=2");
        Assert.Equal(["Premier publié", "Brouillon récent"], Titles(page2));
        Assert.Equal(5, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal(2, page2.GetProperty("pageSize").GetInt32());
        Assert.Equal(["Brouillon ancien"], Titles(await ListAsync(agent, "?pageSize=2&page=3")));
        Assert.Empty((await ListAsync(agent, "?pageSize=2&page=99")).GetProperty("items").EnumerateArray());
        Assert.Equal(3, (await ListAsync(c.Requester.Session)).GetProperty("total").GetInt32());
        Assert.Equal(5, (await ListAsync(agent, "?pageSize=100")).GetProperty("items").GetArrayLength());

        foreach (var bad in new[] { "?page=0", "?page=abc", "?page=-1", "?pageSize=0", "?pageSize=101", "?pageSize=x", "?q=" + new string('a', 101) })
        {
            await AssertStatusAsync(agent.GetAsync("/api/v1/articles" + bad), HttpStatusCode.UnprocessableEntity, bad);
        }

        // A 100 character query is accepted.
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/articles?q=" + new string('a', 100))).StatusCode);

        // The summary carries a 200 character excerpt of the symptom and the tags as an array.
        var long200 = await CreateAsync(agent, Body("Long symptôme", new string('s', 300), tags: ["x1", "x2"]));
        var summary = (await ListAsync(agent, "?q=" + Uri.EscapeDataString("Long symptôme"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(200, summary.GetProperty("excerpt").GetString()!.Length);
        Assert.Equal(["x1", "x2"], summary.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal(300, long200.GetProperty("symptom").GetString()!.Length);
    }

    [Fact]
    public async Task Writing_validates_input_normalises_tags_and_publication_is_idempotent()
    {
        var c = await BuildCompanyAsync("kb-write");
        var agent = c.Agent.Session;
        var admin = c.Admin.Session;

        // Tags: trimmed, case-insensitive duplicates removed, order kept.
        var created = await CreateAsync(agent, Body(tags: [" VPN ", "réseau", "vpn", "Wifi"]));
        Assert.Equal(["VPN", "réseau", "Wifi"], created.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(c.Agent.Id, created.GetProperty("author").GetProperty("id").GetGuid());
        var stored = await QueryDbAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().Where(a => a.Id == id).Select(a => a.Tags).SingleAsync());
        Assert.Equal("VPN,réseau,Wifi", stored);

        // 422 with errors by field.
        var invalid = new (object Body, string Field)[]
        {
            (Body(title: "ab"), "title"),
            (Body(title: "   "), "title"),
            (Body(title: new string('t', 201)), "title"),
            (Body(symptom: new string('s', 4001)), "symptom"),
            (Body(cause: new string('c', 4001)), "cause"),
            (Body(solution: new string('s', 8001)), "solution"),
            (Body(tags: Enumerable.Range(0, 11).Select(i => "t" + i).ToArray()), "tags"),
            (Body(tags: ["a,b"]), "tags"),
            (Body(tags: [new string('x', 31)]), "tags"),
            (Body(tags: [" "]), "tags"),
            (new { title = "Titre valide", cause = "", solution = "", tags = Array.Empty<string>() }, "symptom"),
            (new { title = "Titre valide", symptom = "", cause = "", solution = "" }, "tags"),
            (new { symptom = "", cause = "", solution = "", tags = Array.Empty<string>() }, "title"),
        };
        foreach (var (body, field) in invalid)
        {
            foreach (var request in new[] { agent.PostAsync("/api/v1/articles", body), agent.PutAsync($"/api/v1/articles/{id}", body) })
            {
                var response = await request;
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), $"errors.{field}");
            }
        }

        // Boundaries are accepted: 10 tags of 30 characters, 4000/4000/8000 characters, 3 character title.
        var limits = await CreateAsync(agent, Body(
            "abc", new string('s', 4000), new string('c', 4000), new string('v', 8000),
            Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 30)).ToArray()));
        Assert.Equal(8000, limits.GetProperty("solution").GetString()!.Length);
        Assert.Equal(10, limits.GetProperty("tags").GetArrayLength());
        Assert.Equal(2, (await ListAsync(agent)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Publishing_and_unpublishing_are_idempotent_and_updates_keep_status_author_and_source()
    {
        var c = await BuildCompanyAsync("kb-publish");
        var agent = c.Agent.Session;
        var admin = c.Admin.Session;
        var withoutCause = await CreateAsync(agent, Body(cause: ""));
        var id = withoutCause.GetProperty("id").GetGuid();

        // Unpublish of a draft: 200, unchanged.
        var again = await JsonAsync(await agent.PostAsync($"/api/v1/articles/{id}/unpublish", new { }));
        Assert.Equal(withoutCause.GetRawText(), again.GetRawText());

        // Publish (an empty cause is allowed), then publish again: same publishedAt.
        var first = await agent.PostAsync($"/api/v1/articles/{id}/publish", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var published = await JsonAsync(first);
        Assert.Equal("Published", published.GetProperty("status").GetString());
        var at = published.GetProperty("publishedAt").GetDateTimeOffset();
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - at).TotalMinutes) < 5);
        var second = await JsonAsync(await admin.PostAsync($"/api/v1/articles/{id}/publish", new { }));
        Assert.Equal(published.GetRawText(), second.GetRawText());

        // A Requester can read it now.
        Assert.Equal(HttpStatusCode.OK, (await c.Requester.Session.GetAsync($"/api/v1/articles/{id}")).StatusCode);

        // An update by someone else keeps status, publishedAt and author (last write wins on the text).
        var put = await JsonAsync(await admin.PutAsync($"/api/v1/articles/{id}", Body(title: "Titre révisé", tags: ["x"])));
        Assert.Equal("Titre révisé", put.GetProperty("title").GetString());
        Assert.Equal("Published", put.GetProperty("status").GetString());
        Assert.Equal(at, put.GetProperty("publishedAt").GetDateTimeOffset());
        Assert.Equal(c.Agent.Id, put.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, put.GetProperty("sourceTicketId").ValueKind);

        // Unpublish: back to draft, publishedAt cleared, hidden from the Requester; twice is the same.
        var unpublished = await JsonAsync(await admin.PostAsync($"/api/v1/articles/{id}/unpublish", new { }));
        Assert.Equal("Draft", unpublished.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, unpublished.GetProperty("publishedAt").ValueKind);
        var unpublishedAgain = await JsonAsync(await agent.PostAsync($"/api/v1/articles/{id}/unpublish", new { }));
        Assert.Equal(unpublished.GetRawText(), unpublishedAgain.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Requester.Session.GetAsync($"/api/v1/articles/{id}")).StatusCode);

        // Publication needs title, symptom and solution: whitespace-only fields are empty.
        var noSymptom = await CreateAsync(agent, Body(symptom: ""));
        var blocked = await agent.PostAsync($"/api/v1/articles/{noSymptom.GetProperty("id").GetGuid()}/publish", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.True((await JsonAsync(blocked)).GetProperty("errors").TryGetProperty("symptom", out _));
        var noSolution = await CreateAsync(agent, Body(solution: "   "));
        var blockedSolution = await agent.PostAsync($"/api/v1/articles/{noSolution.GetProperty("id").GetGuid()}/publish", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedSolution.StatusCode);
        Assert.True((await JsonAsync(blockedSolution)).GetProperty("errors").TryGetProperty("solution", out _));
        Assert.Equal("Draft", (await JsonAsync(await agent.GetAsync($"/api/v1/articles/{noSymptom.GetProperty("id").GetGuid()}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_published_article_cannot_lose_its_symptom_or_solution_but_a_draft_can()
    {
        var c = await BuildCompanyAsync("kb-complete");
        var agent = c.Agent.Session;
        var article = await PublishedAsync(agent, Body());
        var id = article.GetProperty("id").GetGuid();

        foreach (var (body, field) in new (object, string)[]
        {
            (Body(symptom: ""), "symptom"),
            (Body(symptom: "   "), "symptom"),
            (Body(solution: ""), "solution"),
            (Body(solution: " 	 "), "solution"),
        })
        {
            var response = await agent.PutAsync($"/api/v1/articles/{id}", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), field);
        }

        // Nothing changed; an empty cause and a title still of 3 characters are fine; the title rule is unchanged.
        Assert.Equal(article.GetRawText(), (await JsonAsync(await agent.GetAsync($"/api/v1/articles/{id}"))).GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await agent.PutAsync($"/api/v1/articles/{id}", Body(title: "abc", cause: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PutAsync($"/api/v1/articles/{id}", Body(title: "ab"))).StatusCode);

        // Once unpublished it is a draft again: free to be emptied.
        await agent.PostAsync($"/api/v1/articles/{id}/unpublish", new { });
        var emptied = await agent.PutAsync($"/api/v1/articles/{id}", Body(symptom: "", solution: ""));
        Assert.Equal(HttpStatusCode.OK, emptied.StatusCode);
        var draft = await CreateAsync(agent, Body(symptom: "", solution: ""));
        Assert.Equal(HttpStatusCode.OK, (await agent.PutAsync($"/api/v1/articles/{draft.GetProperty("id").GetGuid()}", Body(symptom: "", solution: ""))).StatusCode);
    }
}
