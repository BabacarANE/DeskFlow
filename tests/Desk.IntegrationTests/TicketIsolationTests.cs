using System.Net;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>Spec section 4: company A can neither read, list nor modify tickets, comments, events or categories of company B.</summary>
public class TicketIsolationTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private sealed record World(Company A, Company B, JsonElement TicketB, Guid TicketBId, Guid CategoryB, Guid CategoryA);

    private async Task<World> BuildWorldAsync()
    {
        var a = await BuildCompanyAsync("alpha");
        var b = await BuildCompanyAsync("beta");
        var categoryA = await CreateCategoryAsync(a.Admin.Session, "Réseau");
        var categoryB = await CreateCategoryAsync(b.Admin.Session, "Messagerie B");
        var ticketB = await CreateTicketAsync(b.Requester.Session, "Secret de B", categoryId: categoryB);
        var ticketBId = ticketB.GetProperty("id").GetGuid();
        await b.Agent.Session.PostAsync($"/api/v1/tickets/{ticketBId}/comments", new { body = "réponse B" });
        await b.Agent.Session.PostAsync($"/api/v1/tickets/{ticketBId}/comments", new { body = "note interne B", isInternal = true });
        await CreateTicketAsync(a.Requester.Session, "Ticket de A", categoryId: categoryA);
        ticketB = await GetTicketAsync(b.Agent.Session, ticketBId);
        return new World(a, b, ticketB, ticketBId, categoryB, categoryA);
    }

    [Fact]
    public async Task ReadTicketOfOtherCompany_Is404_ForEveryRole()
    {
        var w = await BuildWorldAsync();

        foreach (var session in new[] { w.A.Admin.Session, w.A.Agent.Session, w.A.Requester.Session })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await session.GetAsync($"/api/v1/tickets/{w.TicketBId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await session.GetAsync($"/api/v1/tickets/{w.TicketBId}/events")).StatusCode);
        }
    }

    [Fact]
    public async Task ListTickets_NeverLeaksTheOtherCompany_WithAnyFilter()
    {
        var w = await BuildWorldAsync();

        foreach (var query in new[]
        {
            "", "?q=Secret", "?q=1", "?status=New", $"?categoryId={w.CategoryB}", "?priority=Normal", "?unassigned=true", $"?assigneeId={w.B.Agent.Id}",
            "?sort=number", "?pageSize=100",
        })
        {
            var page = await JsonAsync(await w.A.Agent.Session.GetAsync("/api/v1/tickets" + query));
            var text = page.GetRawText();
            Assert.DoesNotContain("Secret de B", text);
            Assert.DoesNotContain(w.TicketBId.ToString(), text);
            Assert.DoesNotContain("Messagerie B", text);
        }

        var all = await JsonAsync(await w.A.Agent.Session.GetAsync("/api/v1/tickets"));
        Assert.Equal(1, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ModifyTicketOfOtherCompany_Is404_AndChangesNothing()
    {
        var w = await BuildWorldAsync();

        var patch = await PatchTicketAsync(w.A.Agent.Session, w.TicketB, new { priority = "Urgent", status = "Open", assigneeId = w.A.Agent.Id });
        var adminPatch = await PatchTicketAsync(w.A.Admin.Session, w.TicketB, new { categoryId = w.CategoryA });

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, adminPatch.StatusCode);
        var stored = await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == w.TicketBId));
        Assert.Equal(Desk.Core.Domain.TicketStatus.New, stored.Status);
        Assert.Equal(Desk.Core.Domain.Priority.Normal, stored.Priority);
        Assert.Null(stored.AssigneeId);
        Assert.Equal(w.CategoryB, stored.CategoryId);
    }

    [Fact]
    public async Task CommentOnTicketOfOtherCompany_Is404_AndStoresNothing()
    {
        var w = await BuildWorldAsync();
        var before = await QueryDbAsync(db => db.TicketComments.IgnoreQueryFilters().CountAsync());

        foreach (var session in new[] { w.A.Admin.Session, w.A.Agent.Session, w.A.Requester.Session })
        {
            var response = await session.PostAsync($"/api/v1/tickets/{w.TicketBId}/comments", new { body = "intrus" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(before, await QueryDbAsync(db => db.TicketComments.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task CreatingATicket_WithACategoryOfAnotherCompany_Is422()
    {
        var w = await BuildWorldAsync();

        var response = await w.A.Requester.Session.PostAsync("/api/v1/tickets", new { title = "x", description = "y", categoryId = w.CategoryB });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Categories_AreListedPerCompany_AndForeignOnesAre404()
    {
        var w = await BuildWorldAsync();

        var list = await JsonAsync(await w.A.Requester.Session.GetAsync("/api/v1/categories"));
        Assert.Equal(["Réseau"], list.EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await w.A.Admin.Session.PutAsync($"/api/v1/categories/{w.CategoryB}", new { name = "Piraté" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.Admin.Session.DeleteAsync($"/api/v1/categories/{w.CategoryB}")).StatusCode);
        var stored = await QueryDbAsync(db => db.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == w.CategoryB));
        Assert.Equal("Messagerie B", stored.Name);
    }

    [Fact]
    public async Task SameCategoryNameInTwoCompanies_IsAllowed()
    {
        var w = await BuildWorldAsync();

        var response = await w.B.Admin.Session.PostAsync("/api/v1/categories", new { name = "Réseau" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Assignees_NeverListUsersOfAnotherCompany()
    {
        var w = await BuildWorldAsync();

        var text = await (await w.A.Agent.Session.GetAsync("/api/v1/assignees")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(w.B.Agent.Id.ToString(), text);
        Assert.DoesNotContain(w.B.Admin.Id.ToString(), text);
    }

    [Fact]
    public async Task RequesterOfOtherRequester_AndOtherCompany_CannotSeeInternalNotesOrEvents()
    {
        var w = await BuildWorldAsync();
        var mine = await CreateTicketAsync(w.A.Requester.Session, "Mon ticket");
        var id = mine.GetProperty("id").GetGuid();
        await w.A.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "interne A", isInternal = true });

        var other = await w.A.Requester2.Session.GetAsync($"/api/v1/tickets/{id}");
        var foreign = await w.B.Requester.Session.GetAsync($"/api/v1/tickets/{id}");

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_SeesNoCategoryAndNoTicket()
    {
        var w = await BuildWorldAsync();
        var root = await SignInSuperAdminAsync();

        var categories = await JsonAsync(await root.GetAsync("/api/v1/categories"));
        Assert.Equal(0, categories.GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await root.PostAsync("/api/v1/categories", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.DeleteAsync($"/api/v1/categories/{w.CategoryA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/v1/assignees")).StatusCode);
    }
}
