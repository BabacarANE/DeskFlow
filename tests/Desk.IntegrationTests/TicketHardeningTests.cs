using System.Net;
using System.Text;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>Audit follow-ups: paging overflow, malformed ids, exact response shapes, concurrent comments, number rollback.</summary>
public class TicketHardeningTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static readonly ContractSchema Strict = ContractSchema.Load().AsStrict();

    [Fact]
    public async Task HugePageNumber_GivesAnEmptyPage_NotA500_OnTicketsAndUsers()
    {
        var c = await BuildCompanyAsync("alpha");
        await CreateTicketAsync(c.Requester.Session);

        foreach (var (session, url) in new[]
        {
            (c.Agent.Session, "/api/v1/tickets?page=2147483647&pageSize=100"),
            (c.Requester.Session, "/api/v1/tickets?page=2147483647&pageSize=1"),
            (c.Admin.Session, "/api/v1/users?page=2147483647&pageSize=100"),
            (c.Admin.Session, "/api/v1/invitations?page=2147483647&pageSize=100"),
        })
        {
            var response = await session.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await JsonAsync(response);
            Assert.Equal(0, page.GetProperty("items").GetArrayLength());
            Assert.True(page.GetProperty("total").GetInt32() >= 1);
            Assert.Equal(int.MaxValue, page.GetProperty("page").GetInt32());
        }
    }

    [Theory]
    [InlineData("\"assigneeId\":\"not-a-guid\"", "assigneeId")]
    [InlineData("\"categoryId\":\"nope\"", "categoryId")]
    [InlineData("\"assigneeId\":123", "assigneeId")]
    [InlineData("\"categoryId\":{\"a\":1}", "categoryId")]
    public async Task Patch_WithMalformedId_Is422_WithFieldErrors(string fragment, string field)
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var request = c.Agent.Session.Request(HttpMethod.Patch, $"/api/v1/tickets/{ticket.GetProperty("id").GetGuid()}");
        request.Content = new StringContent($"{{\"rowVersion\":\"{ticket.GetProperty("rowVersion").GetString()}\",{fragment}}}", Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Responses_HaveExactlyTheContractProperties_ForEveryRole()
    {
        var c = await BuildCompanyAsync("alpha");
        var category = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var ticket = await CreateTicketAsync(c.Requester.Session, categoryId: category);
        var id = ticket.GetProperty("id").GetGuid();
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open", assigneeId = c.Agent.Id });
        await PatchOkAsync(c.Agent.Session, ticket, new { status = "Pending" });
        var comment = await JsonAsync(await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "public" }));
        await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "merci" });
        await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "note", isInternal = true });

        void Conforms(JsonElement body, string schema)
        {
            var errors = Strict.Validate(body, Strict.Component(schema));
            Assert.True(errors.Count == 0, $"{schema}: {string.Join("; ", errors)}\n{body.GetRawText()}");
        }

        Conforms(comment, "Comment");
        foreach (var session in new[] { c.Agent.Session, c.Requester.Session, c.Admin.Session })
        {
            var detail = await JsonAsync(await session.GetAsync($"/api/v1/tickets/{id}"));
            Conforms(detail, "TicketDetail");
            Assert.True(detail.GetProperty("comments").GetArrayLength() >= 2);
            var page = await JsonAsync(await session.GetAsync("/api/v1/tickets"));
            Conforms(page, "TicketPage");
            foreach (var item in (await JsonAsync(await session.GetAsync($"/api/v1/tickets/{id}/events"))).EnumerateArray())
            {
                Conforms(item, "TicketEvent");
            }
        }

        var created = await JsonAsync(await c.Requester.Session.PostAsync("/api/v1/tickets", new { title = "x", description = "y" }));
        Conforms(created, "TicketDetail");
    }

    [Fact]
    public async Task ConcurrentComments_AllSucceed_AndAreAllStored()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = (await CreateTicketAsync(c.Requester.Session)).GetProperty("id").GetGuid();
        var authors = new[] { c.Requester.Session, c.Agent.Session, c.Admin.Session };

        var responses = await Task.WhenAll(Enumerable.Range(0, 9).Select(i =>
            authors[i % 3].PostAsync($"/api/v1/tickets/{id}/comments", new { body = $"c{i}" })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var comments = (await GetTicketAsync(c.Agent.Session, id)).GetProperty("comments");
        Assert.Equal(9, comments.GetArrayLength());
        Assert.Equal(9, comments.EnumerateArray().Select(x => x.GetProperty("body").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentRequesterReplies_OnAPendingTicket_ReopenItExactlyOnce()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });
        await PatchOkAsync(c.Agent.Session, ticket, new { status = "Pending" });

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = $"r{i}" })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var reopenings = (await EventsAsync(c.Agent.Session, id))
            .Count(e => e.GetProperty("type").GetString() == "StatusChanged" && e.GetProperty("oldValue").GetString() == "Pending");
        Assert.Equal(1, reopenings);
        Assert.Equal("Open", (await GetTicketAsync(c.Agent.Session, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FailedTicketInsert_RollsTheNumberBack_NoGap()
    {
        var c = await BuildCompanyAsync("alpha");
        Assert.Equal(1, (await CreateTicketAsync(c.Requester.Session, "un")).GetProperty("number").GetInt32());
        // Make the INSERT of the ticket itself fail, after the counter has been incremented in the same transaction.
        await WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION refuse_boom() RETURNS trigger AS $$
                BEGIN
                  IF NEW."Title" = 'boom' THEN RAISE EXCEPTION 'refused'; END IF;
                  RETURN NEW;
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER refuse_boom BEFORE INSERT ON "Tickets" FOR EACH ROW EXECUTE FUNCTION refuse_boom();
                """);
        });

        var failed = await c.Requester.Session.PostAsync("/api/v1/tickets", new { title = "boom", description = "d" });
        var next = await CreateTicketAsync(c.Requester.Session, "deux");

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(2, next.GetProperty("number").GetInt32());
        Assert.Equal(2, await QueryDbAsync(db => db.TicketCounters.IgnoreQueryFilters().Where(t => t.TenantId == c.TenantId).Select(t => t.LastNumber).SingleAsync()));
        Assert.Equal(2, await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == c.TenantId)));
    }
}
