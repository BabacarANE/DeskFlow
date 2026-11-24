using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>SLA table (US-20): roles, validation, order, effect on new tickets only, isolation.</summary>
public class SlaPolicyTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static readonly ContractSchema Contract = ContractSchema.Load().AsStrict();

    private static readonly string[] Order = ["Low", "Normal", "High", "Urgent"];

    private static object Line(string priority, int first, int resolution) =>
        new { priority, firstResponseMinutes = first, resolutionMinutes = resolution };

    private static object Table(params object[] lines) => new { policies = lines };

    private static object Valid(int urgentFirst = 5, int urgentResolution = 15) => Table(
        Line("Urgent", urgentFirst, urgentResolution), Line("Low", 100, 200), Line("High", 20, 40), Line("Normal", 60, 120));

    private static void AssertConforms(JsonElement body, string method, HttpStatusCode status)
    {
        var errors = Contract.Validate(body, Contract.ResponseSchema("/sla-policies", method, (int)status));
        Assert.True(errors.Count == 0, $"{method} /sla-policies {(int)status}: {string.Join("; ", errors)}\n{body.GetRawText()}");
    }

    private static async Task<List<(string Priority, int First, int Resolution)>> ReadAsync(Session session)
    {
        var response = await session.GetAsync("/api/v1/sla-policies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        AssertConforms(json, "get", HttpStatusCode.OK);
        return json.EnumerateArray()
            .Select(l => (l.GetProperty("priority").GetString()!, l.GetProperty("firstResponseMinutes").GetInt32(), l.GetProperty("resolutionMinutes").GetInt32()))
            .ToList();
    }

    private static readonly List<(string, int, int)> DefaultTable =
        [("Low", 1440, 4320), ("Normal", 480, 1440), ("High", 120, 480), ("Urgent", 30, 240)];

    private static async Task<(DateTimeOffset Created, DateTimeOffset Response, DateTimeOffset Resolution)> DuesAsync(Session session, Guid id)
    {
        var t = await GetTicketAsync(session, id);
        return (t.GetProperty("createdAt").GetDateTimeOffset(), t.GetProperty("responseDueAt").GetDateTimeOffset(), t.GetProperty("resolutionDueAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Only_the_tenant_admin_reads_and_writes_the_table_which_comes_ordered_with_four_lines()
    {
        var c = await BuildCompanyAsync("sla-roles");
        var root = await SignInSuperAdminAsync();

        Assert.Equal(DefaultTable, await ReadAsync(c.Admin.Session));
        Assert.Equal(Order, (await ReadAsync(c.Admin.Session)).Select(l => l.Priority));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/sla-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PutAsJsonAsync("/api/v1/sla-policies", Valid())).StatusCode);
        foreach (var other in new[] { c.Agent.Session, c.Requester.Session, root })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/sla-policies")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync("/api/v1/sla-policies", Valid())).StatusCode);
        }

        Assert.Equal(DefaultTable, await ReadAsync(c.Admin.Session));

        // The lines may come in any order; the answer is always Low, Normal, High, Urgent.
        var put = await c.Admin.Session.PutAsync("/api/v1/sla-policies", Valid());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var json = await JsonAsync(put);
        AssertConforms(json, "put", HttpStatusCode.OK);
        List<(string, int, int)> expected = [("Low", 100, 200), ("Normal", 60, 120), ("High", 20, 40), ("Urgent", 5, 15)];
        Assert.Equal(expected, json.EnumerateArray()
            .Select(l => (l.GetProperty("priority").GetString()!, l.GetProperty("firstResponseMinutes").GetInt32(), l.GetProperty("resolutionMinutes").GetInt32())).ToList());
        Assert.Equal(expected, await ReadAsync(c.Admin.Session));
        Assert.Equal(4, await QueryDbAsync(db => db.SlaPolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == c.TenantId)));

        // Boundaries: 1 and 43200 are accepted, equal first response and resolution too.
        var edge = await c.Admin.Session.PutAsync("/api/v1/sla-policies", Table(
            Line("Low", 1, 1), Line("Normal", 43200, 43200), Line("High", 1, 43200), Line("Urgent", 30, 30)));
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
    }

    [Fact]
    public async Task An_invalid_table_is_refused_with_errors_and_changes_nothing()
    {
        var c = await BuildCompanyAsync("sla-invalid");
        var admin = c.Admin.Session;
        var bad = new (object Body, string Key)[]
        {
            (new { policies = (object?)null }, "policies"),
            (new { }, "policies"),
            (Table(Line("Low", 1, 2), Line("Normal", 1, 2), Line("High", 1, 2)), "policies"),
            (Table(Line("Low", 1, 2), Line("Normal", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2), Line("Urgent", 1, 2)), "policies"),
            (Table(Line("Low", 1, 2), Line("Low", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies"),
            (Table(Line("Low", 1, 2), Line("Normal", 1, 2), Line("High", 1, 2), Line("Critical", 1, 2)), "policies[3].priority"),
            (Table(Line("Low", 0, 2), Line("Normal", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[0].firstResponseMinutes"),
            (Table(Line("Low", 1, 0), Line("Normal", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[0].resolutionMinutes"),
            (Table(Line("Low", 1, 2), Line("Normal", 43201, 43201), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[1].firstResponseMinutes"),
            (Table(Line("Low", 1, 2), Line("Normal", 1, 43201), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[1].resolutionMinutes"),
            (Table(Line("Low", 1, 2), Line("Normal", 1, 2), Line("High", 10, 9), Line("Urgent", 1, 2)), "policies[2].resolutionMinutes"),
            (Table(Line("Low", -5, 2), Line("Normal", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[0].firstResponseMinutes"),
            (Table(new { priority = "Low" }, Line("Normal", 1, 2), Line("High", 1, 2), Line("Urgent", 1, 2)), "policies[0].firstResponseMinutes"),
        };

        foreach (var (body, key) in bad)
        {
            var response = await admin.PutAsync("/api/v1/sla-policies", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var errors = (await JsonAsync(response)).GetProperty("errors");
            Assert.True(errors.TryGetProperty(key, out _), $"errors.{key} in {errors.GetRawText()}");
        }

        Assert.Equal(DefaultTable, await ReadAsync(admin));
    }

    [Fact]
    public async Task New_deadlines_apply_to_tickets_created_afterwards_and_never_move_existing_ones()
    {
        var c = await BuildCompanyAsync("sla-effect");
        var (betaId, betaAdmin) = await CreateTenantWithAdminAsync("sla-beta", "admin@sla-beta.test");
        var betaRequesterEmail = "req@sla-beta.test";
        var (_, betaRequester) = await InviteAndSignInAsync(betaAdmin, betaRequesterEmail, "Requester");

        var before = await CreateTicketAsync(c.Requester.Session, "Avant le changement", "Urgent");
        var beforeId = before.GetProperty("id").GetGuid();
        var (created0, response0, resolution0) = await DuesAsync(c.Admin.Session, beforeId);
        Assert.Equal(created0.AddMinutes(30), response0);
        Assert.Equal(created0.AddMinutes(240), resolution0);

        Assert.Equal(HttpStatusCode.OK, (await c.Admin.Session.PutAsync("/api/v1/sla-policies", Valid())).StatusCode);

        // Existing ticket: due dates untouched.
        Assert.Equal((created0, response0, resolution0), await DuesAsync(c.Admin.Session, beforeId));

        // New tickets read the new table (default priority Normal, then Urgent).
        var normal = await CreateTicketAsync(c.Requester.Session, "Après, normal");
        var (created1, response1, resolution1) = await DuesAsync(c.Admin.Session, normal.GetProperty("id").GetGuid());
        Assert.Equal(created1.AddMinutes(60), response1);
        Assert.Equal(created1.AddMinutes(120), resolution1);
        var urgent = await CreateTicketAsync(c.Requester.Session, "Après, urgent", "Urgent");
        var (created2, response2, resolution2) = await DuesAsync(c.Admin.Session, urgent.GetProperty("id").GetGuid());
        Assert.Equal(created2.AddMinutes(5), response2);
        Assert.Equal(created2.AddMinutes(15), resolution2);

        // A priority change recomputes from the current table of the company.
        var changed = await PatchOkAsync(c.Agent.Session, await GetTicketAsync(c.Agent.Session, beforeId), new { priority = "High" });
        Assert.Equal(created0.AddMinutes(20), changed.GetProperty("responseDueAt").GetDateTimeOffset());
        Assert.Equal(created0.AddMinutes(40), changed.GetProperty("resolutionDueAt").GetDateTimeOffset());

        // The other company keeps the defaults and cannot move this table.
        Assert.Equal(DefaultTable, await ReadAsync(betaAdmin));
        var betaTicket = await CreateTicketAsync(betaRequester, "Chez beta", "Urgent");
        var (betaCreated, betaResponse, betaResolution) = await DuesAsync(betaAdmin, betaTicket.GetProperty("id").GetGuid());
        Assert.Equal(betaCreated.AddMinutes(30), betaResponse);
        Assert.Equal(betaCreated.AddMinutes(240), betaResolution);

        var betaPut = await betaAdmin.PutAsync("/api/v1/sla-policies", Table(
            Line("Low", 7, 8), Line("Normal", 7, 8), Line("High", 7, 8), Line("Urgent", 7, 8)));
        Assert.Equal(HttpStatusCode.OK, betaPut.StatusCode);
        List<(string, int, int)> alpha = [("Low", 100, 200), ("Normal", 60, 120), ("High", 20, 40), ("Urgent", 5, 15)];
        Assert.Equal(alpha, await ReadAsync(c.Admin.Session));
        var rows = await QueryDbAsync(db => db.SlaPolicies.IgnoreQueryFilters().Where(p => p.TenantId == betaId).Select(p => p.FirstResponseMinutes).ToListAsync());
        Assert.Equal([7, 7, 7, 7], rows);
    }
}
