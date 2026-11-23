using System.Net;
using System.Text.Json;
using Desk.IntegrationTests.Support;

namespace Desk.IntegrationTests;

/// <summary>Real responses of the phase 2 routes must fit the schemas of the frozen contract.</summary>
public class TicketContractTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static readonly ContractSchema Contract = ContractSchema.Load();

    private static void AssertConforms(JsonElement body, string path, string method, HttpStatusCode status)
    {
        var errors = Contract.Validate(body, Contract.ResponseSchema(path, method, (int)status));
        Assert.True(errors.Count == 0, $"{method.ToUpperInvariant()} {path} {(int)status}: {string.Join("; ", errors)}\n{body.GetRawText()}");
    }

    private static async Task<JsonElement> ExpectAsync(Task<HttpResponseMessage> request, HttpStatusCode status, string path, string method)
    {
        var response = await request;
        Assert.Equal(status, response.StatusCode);
        if (status == HttpStatusCode.NoContent)
        {
            return default;
        }

        var body = await JsonAsync(response);
        AssertConforms(body, path, method, status);
        return body;
    }

    [Fact]
    public async Task SuccessResponses_FitTheContract()
    {
        var c = await BuildCompanyAsync("alpha");
        var admin = c.Admin.Session;
        var agent = c.Agent.Session;
        var requester = c.Requester.Session;

        var category = await ExpectAsync(admin.PostAsync("/api/v1/categories", new { name = "Réseau" }), HttpStatusCode.Created, "/categories", "post");
        var categoryId = category.GetProperty("id").GetGuid();
        await ExpectAsync(admin.PutAsync($"/api/v1/categories/{categoryId}", new { name = "Réseau et VPN" }), HttpStatusCode.OK, "/categories/{id}", "put");
        await ExpectAsync(requester.GetAsync("/api/v1/categories"), HttpStatusCode.OK, "/categories", "get");
        await ExpectAsync(agent.GetAsync("/api/v1/assignees"), HttpStatusCode.OK, "/assignees", "get");

        var created = await ExpectAsync(
            requester.PostAsync("/api/v1/tickets", new { title = "VPN", description = "KO", categoryId, priority = "High" }),
            HttpStatusCode.Created, "/tickets", "post");
        var id = created.GetProperty("id").GetGuid();
        await ExpectAsync(requester.GetAsync("/api/v1/tickets?sort=number&direction=asc&pageSize=5"), HttpStatusCode.OK, "/tickets", "get");
        await ExpectAsync(agent.GetAsync("/api/v1/tickets?status=New&unassigned=true"), HttpStatusCode.OK, "/tickets", "get");

        await ExpectAsync(
            PatchTicketAsync(agent, created, new { status = "Open", assigneeId = c.Agent.Id }), HttpStatusCode.OK, "/tickets/{id}", "patch");
        await ExpectAsync(agent.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "ok", isInternal = true }), HttpStatusCode.Created, "/tickets/{id}/comments", "post");
        await ExpectAsync(agent.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "public" }), HttpStatusCode.Created, "/tickets/{id}/comments", "post");
        await ExpectAsync(agent.GetAsync($"/api/v1/tickets/{id}"), HttpStatusCode.OK, "/tickets/{id}", "get");
        await ExpectAsync(requester.GetAsync($"/api/v1/tickets/{id}"), HttpStatusCode.OK, "/tickets/{id}", "get");
        await ExpectAsync(requester.GetAsync($"/api/v1/tickets/{id}/events"), HttpStatusCode.OK, "/tickets/{id}/events", "get");

        var spare = await CreateCategoryAsync(admin, "Spare");
        await ExpectAsync(admin.DeleteAsync($"/api/v1/categories/{spare}"), HttpStatusCode.NoContent, "/categories/{id}", "delete");
    }

    [Fact]
    public async Task ErrorResponses_AreProblemDetailsDeclaredByTheContract()
    {
        var c = await BuildCompanyAsync("alpha");
        var agent = c.Agent.Session;
        var requester = c.Requester.Session;
        var ticket = await CreateTicketAsync(requester);
        var id = ticket.GetProperty("id").GetGuid();
        var category = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        await CreateTicketAsync(requester, categoryId: category);

        await ExpectAsync(requester.PostAsync("/api/v1/tickets", new { title = "", description = "" }), HttpStatusCode.UnprocessableEntity, "/tickets", "post");
        await ExpectAsync(requester.GetAsync($"/api/v1/tickets/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "/tickets/{id}", "get");
        await ExpectAsync(requester.GetAsync("/api/v1/tickets?sort=nope"), HttpStatusCode.UnprocessableEntity, "/tickets", "get");
        await ExpectAsync(PatchTicketAsync(agent, ticket, new { status = "Closed" }), HttpStatusCode.Conflict, "/tickets/{id}", "patch");
        await ExpectAsync(PatchTicketAsync(requester, ticket, new { status = "Open" }), HttpStatusCode.Forbidden, "/tickets/{id}", "patch");
        await ExpectAsync(PatchTicketAsync(agent, ticket, new { assigneeId = c.Requester.Id }), HttpStatusCode.UnprocessableEntity, "/tickets/{id}", "patch");
        await ExpectAsync(requester.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "x", isInternal = true }), HttpStatusCode.Forbidden, "/tickets/{id}/comments", "post");
        await ExpectAsync(requester.GetAsync($"/api/v1/tickets/{Guid.NewGuid()}/events"), HttpStatusCode.NotFound, "/tickets/{id}/events", "get");
        await ExpectAsync(c.Admin.Session.PostAsync("/api/v1/categories", new { name = "Réseau" }), HttpStatusCode.Conflict, "/categories", "post");
        await ExpectAsync(c.Admin.Session.DeleteAsync($"/api/v1/categories/{category}"), HttpStatusCode.Conflict, "/categories/{id}", "delete");
        await ExpectAsync(requester.PostAsync("/api/v1/categories", new { name = "X" }), HttpStatusCode.Forbidden, "/categories", "post");
        await ExpectAsync(requester.GetAsync("/api/v1/assignees"), HttpStatusCode.Forbidden, "/assignees", "get");
        await ExpectAsync(c.Admin.Session.PutAsync($"/api/v1/categories/{Guid.NewGuid()}", new { name = "X" }), HttpStatusCode.NotFound, "/categories/{id}", "put");
    }

    [Fact]
    public async Task EnumsAndTimestamps_AreSerializedAsContractStrings()
    {
        var c = await BuildCompanyAsync("alpha");

        var ticket = await CreateTicketAsync(c.Requester.Session, priority: "Urgent");

        Assert.Equal(JsonValueKind.String, ticket.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.String, ticket.GetProperty("priority").ValueKind);
        Assert.Equal(JsonValueKind.String, ticket.GetProperty("rowVersion").ValueKind);
        Assert.Equal(JsonValueKind.Number, ticket.GetProperty("number").ValueKind);
    }
}
