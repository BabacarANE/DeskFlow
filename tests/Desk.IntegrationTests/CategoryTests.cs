using System.Net;
using Desk.IntegrationTests.Support;

namespace Desk.IntegrationTests;

public class CategoryTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    [Fact]
    public async Task Admin_CreatesRenamesAndDeletes_AndListIsSortedByName()
    {
        var c = await BuildCompanyAsync("alpha");
        var b = await CreateCategoryAsync(c.Admin.Session, "Bureautique");
        await CreateCategoryAsync(c.Admin.Session, "Accès");

        var renamed = await c.Admin.Session.PutAsync($"/api/v1/categories/{b}", new { name = "Zèbre" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Zèbre", (await JsonAsync(renamed)).GetProperty("name").GetString());

        var names = (await JsonAsync(await c.Agent.Session.GetAsync("/api/v1/categories"))).EnumerateArray().Select(x => x.GetProperty("name").GetString());
        Assert.Equal(["Accès", "Zèbre"], names);

        Assert.Equal(HttpStatusCode.NoContent, (await c.Admin.Session.DeleteAsync($"/api/v1/categories/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Admin.Session.DeleteAsync($"/api/v1/categories/{b}")).StatusCode);
    }

    [Fact]
    public async Task OnlyTenantAdmin_CanWrite()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = await CreateCategoryAsync(c.Admin.Session, "Réseau");

        foreach (var session in new[] { c.Agent.Session, c.Requester.Session })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await session.PostAsync("/api/v1/categories", new { name = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await session.PutAsync($"/api/v1/categories/{id}", new { name = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await session.DeleteAsync($"/api/v1/categories/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/categories")).StatusCode);
        }
    }

    [Fact]
    public async Task DuplicateName_Is409_CaseInsensitive_OnCreateAndRename()
    {
        var c = await BuildCompanyAsync("alpha");
        await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var other = await CreateCategoryAsync(c.Admin.Session, "Matériel");

        var create = await c.Admin.Session.PostAsync("/api/v1/categories", new { name = " réseau " });
        var rename = await c.Admin.Session.PutAsync($"/api/v1/categories/{other}", new { name = "RÉSEAU" });
        var sameName = await c.Admin.Session.PutAsync($"/api/v1/categories/{other}", new { name = "Matériel" });

        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameName.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyName_Is422(string name)
    {
        var c = await BuildCompanyAsync("alpha");
        var id = await CreateCategoryAsync(c.Admin.Session, "Réseau");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Admin.Session.PostAsync("/api/v1/categories", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Admin.Session.PutAsync($"/api/v1/categories/{id}", new { name })).StatusCode);
    }

    [Fact]
    public async Task TooLongName_Is422()
    {
        var c = await BuildCompanyAsync("alpha");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Admin.Session.PostAsync("/api/v1/categories", new { name = new string('x', 61) })).StatusCode);
    }

    [Fact]
    public async Task DeletingACategoryUsedByATicket_Is409_AndAllowedOnceUnused()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var ticket = await CreateTicketAsync(c.Requester.Session, categoryId: id);

        var refused = await c.Admin.Session.DeleteAsync($"/api/v1/categories/{id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.EndsWith("/category-in-use", await ProblemTypeAsync(refused));
        Assert.Equal("Réseau", (await GetTicketAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).GetProperty("category").GetProperty("name").GetString());

        await PatchOkAsync(c.Agent.Session, ticket, new { categoryId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NoContent, (await c.Admin.Session.DeleteAsync($"/api/v1/categories/{id}")).StatusCode);
    }
}
