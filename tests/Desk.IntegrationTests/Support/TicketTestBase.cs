using System.Net;
using System.Text.Json;

namespace Desk.IntegrationTests.Support;

public sealed record Member(Guid Id, Session Session);

/// <summary>One company: an admin, an agent and two requesters.</summary>
public sealed record Company(Guid TenantId, Member Admin, Member Agent, Member Requester, Member Requester2);

/// <summary>Helpers shared by the ticket tests.</summary>
public abstract class TicketTestBase(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected async Task<Company> BuildCompanyAsync(string slug)
    {
        var (tenantId, admin) = await CreateTenantWithAdminAsync(slug, $"admin@{slug}.test");
        var adminId = (await JsonAsync(await admin.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid();
        var (agentId, agent) = await InviteAndSignInAsync(admin, $"agent@{slug}.test", "Agent");
        var (req1Id, req1) = await InviteAndSignInAsync(admin, $"req1@{slug}.test", "Requester");
        var (req2Id, req2) = await InviteAndSignInAsync(admin, $"req2@{slug}.test", "Requester");
        return new Company(tenantId, new Member(adminId, admin), new Member(agentId, agent),
            new Member(req1Id, req1), new Member(req2Id, req2));
    }

    protected static async Task<JsonElement> CreateTicketAsync(
        Session session, string title = "VPN en panne", string? priority = null, Guid? categoryId = null)
    {
        var response = await session.PostAsync("/api/v1/tickets",
            new { title, description = "Impossible de me connecter.", priority, categoryId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    protected static async Task<Guid> CreateCategoryAsync(Session admin, string name)
    {
        var response = await admin.PostAsync("/api/v1/categories", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    protected static async Task<JsonElement> GetTicketAsync(Session session, Guid id)
    {
        var response = await session.GetAsync($"/api/v1/tickets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    protected static Task<HttpResponseMessage> PatchTicketAsync(Session session, JsonElement ticket, object changes)
    {
        var body = new Dictionary<string, object?> { ["rowVersion"] = ticket.GetProperty("rowVersion").GetString() };
        foreach (var property in changes.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(changes);
        }

        return session.PatchAsync($"/api/v1/tickets/{ticket.GetProperty("id").GetGuid()}", body);
    }

    protected static async Task<JsonElement> PatchOkAsync(Session session, JsonElement ticket, object changes)
    {
        var response = await PatchTicketAsync(session, ticket, changes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    protected static async Task<List<JsonElement>> EventsAsync(Session session, Guid ticketId)
    {
        var response = await session.GetAsync($"/api/v1/tickets/{ticketId}/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).EnumerateArray().ToList();
    }

    protected static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("type").GetString();
}
