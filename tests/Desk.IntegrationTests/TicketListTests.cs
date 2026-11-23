using System.Net;
using System.Text.Json;
using Desk.IntegrationTests.Support;

namespace Desk.IntegrationTests;

/// <summary>US-07: filters, sort and pagination of the ticket list; numbering.</summary>
public class TicketListTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static async Task<JsonElement> ListAsync(Session session, string query = "")
    {
        var response = await session.GetAsync("/api/v1/tickets" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static List<string> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToList();

    private async Task<(Company Company, Guid Network)> SeedAsync()
    {
        var c = await BuildCompanyAsync("alpha");
        var network = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var t1 = await CreateTicketAsync(c.Requester.Session, "VPN lent", "Low", network);
        var t2 = await CreateTicketAsync(c.Requester.Session, "Imprimante HS", "Urgent");
        var t3 = await CreateTicketAsync(c.Requester2.Session, "Mot de passe 100% expiré", "High");
        var t4 = await CreateTicketAsync(c.Requester2.Session, "Accès au partage", "Normal", network);
        await PatchOkAsync(c.Agent.Session, t2, new { status = "Open", assigneeId = c.Agent.Id });
        await PatchOkAsync(c.Agent.Session, t3, new { assigneeId = c.Admin.Id });
        return (c, network);
    }

    [Fact]
    public async Task NumbersAreSequentialPerCompany()
    {
        var (c, _) = await SeedAsync();
        var other = await BuildCompanyAsync("beta");

        var first = await CreateTicketAsync(other.Requester.Session);

        Assert.Equal(1, first.GetProperty("number").GetInt32());
        var numbers = (await ListAsync(c.Agent.Session, "?sort=number&direction=asc")).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("number").GetInt32());
        Assert.Equal([1, 2, 3, 4], numbers);
    }

    [Fact]
    public async Task ParallelCreations_GetDistinctConsecutiveNumbers()
    {
        var c = await BuildCompanyAsync("alpha");
        var other = await BuildCompanyAsync("beta");
        const int count = 25;

        var responses = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            (i % 2 == 0 ? c.Requester : c.Requester2).Session.PostAsync("/api/v1/tickets", new { title = $"T{i}", description = "d" })));
        var otherResponse = await other.Requester.Session.PostAsync("/api/v1/tickets", new { title = "B", description = "d" });

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<int>();
        foreach (var response in responses)
        {
            numbers.Add((await JsonAsync(response)).GetProperty("number").GetInt32());
        }

        Assert.Equal(Enumerable.Range(1, count), numbers.Order());
        Assert.Equal(1, (await JsonAsync(otherResponse)).GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task List_FiltersByStatusPriorityCategoryAndAssignee()
    {
        var (c, network) = await SeedAsync();
        var s = c.Agent.Session;

        Assert.Equal(["Imprimante HS"], Titles(await ListAsync(s, "?status=Open")));
        Assert.Equal(3, (await ListAsync(s, "?status=New")).GetProperty("total").GetInt32());
        Assert.Equal(["Imprimante HS"], Titles(await ListAsync(s, "?priority=Urgent")));
        Assert.Equal(["Accès au partage", "VPN lent"], Titles(await ListAsync(s, $"?categoryId={network}")).Order());
        Assert.Equal(["Imprimante HS"], Titles(await ListAsync(s, $"?assigneeId={c.Agent.Id}")));
        Assert.Equal(["Mot de passe 100% expiré"], Titles(await ListAsync(s, $"?assigneeId={c.Admin.Id}")));
        Assert.Equal(2, (await ListAsync(s, "?unassigned=true")).GetProperty("total").GetInt32());
        // unassigned wins over assigneeId
        Assert.Equal(2, (await ListAsync(s, $"?unassigned=true&assigneeId={c.Agent.Id}")).GetProperty("total").GetInt32());
        Assert.Equal(["Imprimante HS"], Titles(await ListAsync(s, "?status=Open&priority=Urgent&unassigned=false")));
        Assert.Equal(0, (await ListAsync(s, "?status=Open&priority=Low")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_SearchesTitleOrNumber_CaseInsensitive_AndTreatsWildcardsLiterally()
    {
        var (c, _) = await SeedAsync();
        var s = c.Agent.Session;

        Assert.Equal(["VPN lent"], Titles(await ListAsync(s, "?q=vpn")));
        Assert.Equal(["Accès au partage"], Titles(await ListAsync(s, "?q=" + Uri.EscapeDataString("ACCÈS"))));
        Assert.Equal(["Accès au partage"], Titles(await ListAsync(s, "?q=4")));
        Assert.Equal(["Accès au partage"], Titles(await ListAsync(s, "?q=" + Uri.EscapeDataString("#4"))));
        Assert.Equal(["Mot de passe 100% expiré"], Titles(await ListAsync(s, "?q=" + Uri.EscapeDataString("100%"))));
        Assert.Equal(["Mot de passe 100% expiré"], Titles(await ListAsync(s, "?q=" + Uri.EscapeDataString("%"))));
        Assert.Empty(Titles(await ListAsync(s, "?q=zzz")));
    }

    [Fact]
    public async Task List_Sorts_ByEveryAllowedField_BothDirections()
    {
        var (c, _) = await SeedAsync();
        var s = c.Agent.Session;

        Assert.Equal(["Imprimante HS", "Mot de passe 100% expiré", "Accès au partage", "VPN lent"], Titles(await ListAsync(s, "?sort=priority&direction=desc")));
        Assert.Equal(["VPN lent", "Accès au partage", "Mot de passe 100% expiré", "Imprimante HS"], Titles(await ListAsync(s, "?sort=priority&direction=asc")));
        Assert.Equal(["VPN lent", "Imprimante HS", "Mot de passe 100% expiré", "Accès au partage"], Titles(await ListAsync(s, "?sort=number&direction=asc")));
        Assert.Equal(["Accès au partage", "Mot de passe 100% expiré", "Imprimante HS", "VPN lent"], Titles(await ListAsync(s)));
        // updatedAt desc: the two patched tickets moved last, the latest patch first
        Assert.Equal(["Mot de passe 100% expiré", "Imprimante HS"], Titles(await ListAsync(s, "?sort=updatedAt&direction=desc")).Take(2));
        Assert.Equal(["Accès au partage", "VPN lent"], Titles(await ListAsync(s, "?sort=updatedAt&direction=asc")).Take(2).Order());
        Assert.Equal(["VPN lent", "Imprimante HS", "Mot de passe 100% expiré", "Accès au partage"], Titles(await ListAsync(s, "?sort=createdAt&direction=asc")));
    }

    [Fact]
    public async Task List_Paginates_AndCapsThePageSizeAt100()
    {
        var c = await BuildCompanyAsync("alpha");
        for (var i = 1; i <= 5; i++)
        {
            await CreateTicketAsync(c.Requester.Session, $"Ticket {i}");
        }

        var page2 = await ListAsync(c.Agent.Session, "?pageSize=2&page=2&sort=number&direction=asc");
        var beyond = await ListAsync(c.Agent.Session, "?pageSize=2&page=9");

        Assert.Equal(["Ticket 3", "Ticket 4"], Titles(page2));
        Assert.Equal(5, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal(2, page2.GetProperty("pageSize").GetInt32());
        Assert.Empty(Titles(beyond));
        Assert.Equal(20, (await ListAsync(c.Agent.Session)).GetProperty("pageSize").GetInt32());
        Assert.Equal(100, (await ListAsync(c.Agent.Session, "?pageSize=100")).GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Agent.Session.GetAsync("/api/v1/tickets?pageSize=101")).StatusCode);
    }

    [Theory]
    [InlineData("status=Nope")]
    [InlineData("status=1")]
    [InlineData("priority=Critical")]
    [InlineData("assigneeId=not-a-guid")]
    [InlineData("categoryId=nope")]
    [InlineData("unassigned=maybe")]
    [InlineData("sort=title")]
    [InlineData("direction=up")]
    [InlineData("page=0")]
    [InlineData("page=abc")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=1000")]
    [InlineData("pageSize=-1")]
    public async Task List_WithInvalidParameter_Is422(string query)
    {
        var c = await BuildCompanyAsync("alpha");

        var response = await c.Agent.Session.GetAsync("/api/v1/tickets?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Paramètres invalides", (await JsonAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task List_WithTooLongSearch_Is422()
    {
        var c = await BuildCompanyAsync("alpha");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Agent.Session.GetAsync("/api/v1/tickets?q=" + new string('x', 101))).StatusCode);
    }

    [Fact]
    public async Task List_ForRequester_IgnoresAssigneeFilter_AndStaysScopedToHisTickets()
    {
        var (c, _) = await SeedAsync();

        var page = await ListAsync(c.Requester.Session, $"?assigneeId={c.Agent.Id}");

        // The assignee filter is ignored for a requester: both of his tickets, none of the other requester's.
        Assert.Equal(["Imprimante HS", "VPN lent"], Titles(page).Order());
        Assert.Equal(["VPN lent"], Titles(await ListAsync(c.Requester.Session, "?priority=Low")));
        Assert.Equal(0, (await ListAsync(c.Requester.Session, "?q=partage")).GetProperty("total").GetInt32());
    }
}
