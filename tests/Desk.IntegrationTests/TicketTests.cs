using System.Net;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>US-05 to US-11: creation, comments, workflow, SLA, events.</summary>
public class TicketTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    [Fact]
    public async Task Create_ReturnsNewTicketWithNumberSlaAndCreatedEvent()
    {
        var c = await BuildCompanyAsync("alpha");
        var categoryId = await CreateCategoryAsync(c.Admin.Session, "Réseau");

        var ticket = await CreateTicketAsync(c.Requester.Session, "VPN en panne", "Urgent", categoryId);

        Assert.Equal(1, ticket.GetProperty("number").GetInt32());
        Assert.Equal("New", ticket.GetProperty("status").GetString());
        Assert.Equal("Urgent", ticket.GetProperty("priority").GetString());
        Assert.Equal("Réseau", ticket.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal(c.Requester.Id, ticket.GetProperty("requester").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("assignee").ValueKind);
        Assert.Equal(0, ticket.GetProperty("comments").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(ticket.GetProperty("rowVersion").GetString()));

        var created = ticket.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(created.AddMinutes(30), ticket.GetProperty("responseDueAt").GetDateTimeOffset());
        Assert.Equal(created.AddMinutes(240), ticket.GetProperty("resolutionDueAt").GetDateTimeOffset());

        var events = await EventsAsync(c.Requester.Session, ticket.GetProperty("id").GetGuid());
        var only = Assert.Single(events);
        Assert.Equal("Created", only.GetProperty("type").GetString());
        Assert.Equal(c.Requester.Id, only.GetProperty("actor").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Create_DefaultsToNormalPriority_AndAgentOrAdminMayCreate()
    {
        var c = await BuildCompanyAsync("alpha");

        var byRequester = await CreateTicketAsync(c.Requester.Session);
        var byAgent = await CreateTicketAsync(c.Agent.Session);
        var byAdmin = await CreateTicketAsync(c.Admin.Session);

        Assert.Equal("Normal", byRequester.GetProperty("priority").GetString());
        Assert.Equal(new[] { 1, 2, 3 }, new[] { byRequester, byAgent, byAdmin }.Select(t => t.GetProperty("number").GetInt32()));
        Assert.Equal(c.Agent.Id, byAgent.GetProperty("requester").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Create_UsesTheTenantSlaPolicy()
    {
        var c = await BuildCompanyAsync("alpha");
        await WithDbAsync(async db =>
        {
            var policy = await db.SlaPolicies.IgnoreQueryFilters().SingleAsync(p => p.TenantId == c.TenantId && p.Priority == Priority.High);
            policy.FirstResponseMinutes = 10;
            policy.ResolutionMinutes = 20;
            await db.SaveChangesAsync();
        });

        var ticket = await CreateTicketAsync(c.Requester.Session, priority: "High");

        var created = ticket.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(created.AddMinutes(10), ticket.GetProperty("responseDueAt").GetDateTimeOffset());
        Assert.Equal(created.AddMinutes(20), ticket.GetProperty("resolutionDueAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("", "desc", null, "title")]
    [InlineData("  ", "desc", null, "title")]
    [InlineData("T", "", null, "description")]
    [InlineData("T", "d", "Critical", "priority")]
    [InlineData("T", "d", "2", "priority")]
    public async Task Create_WithInvalidData_Is422(string title, string description, string? priority, string field)
    {
        var c = await BuildCompanyAsync("alpha");

        var response = await c.Requester.Session.PostAsync("/api/v1/tickets", new { title, description, priority });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Create_WithTooLongTitleOrUnknownCategory_Is422()
    {
        var c = await BuildCompanyAsync("alpha");

        var tooLong = await c.Requester.Session.PostAsync("/api/v1/tickets", new { title = new string('x', 201), description = "d" });
        var unknown = await c.Requester.Session.PostAsync("/api/v1/tickets", new { title = "T", description = "d", categoryId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    [Fact]
    public async Task Requester_SeesOnlyOwnTickets_ListAndDetail()
    {
        var c = await BuildCompanyAsync("alpha");
        var mine = await CreateTicketAsync(c.Requester.Session, "Le mien");
        var theirs = await CreateTicketAsync(c.Requester2.Session, "Celui de l'autre");

        var list = await JsonAsync(await c.Requester.Session.GetAsync("/api/v1/tickets"));
        var titles = list.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString()).ToList();

        Assert.Equal(["Le mien"], titles);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Requester.Session.GetAsync($"/api/v1/tickets/{theirs.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Requester.Session.GetAsync($"/api/v1/tickets/{theirs.GetProperty("id").GetGuid()}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Requester.Session.GetAsync($"/api/v1/tickets/{mine.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Requester_CannotCommentOrEditAnotherRequestersTicket()
    {
        var c = await BuildCompanyAsync("alpha");
        var theirs = await CreateTicketAsync(c.Requester2.Session);
        var id = theirs.GetProperty("id").GetGuid();

        var comment = await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "intrus" });
        var patch = await PatchTicketAsync(c.Requester.Session, theirs, new { priority = "Low" });

        Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Empty((await GetTicketAsync(c.Agent.Session, id)).GetProperty("comments").EnumerateArray());
    }

    [Fact]
    public async Task AgentAndAdmin_SeeEveryTicketOfTheCompany()
    {
        var c = await BuildCompanyAsync("alpha");
        await CreateTicketAsync(c.Requester.Session);
        await CreateTicketAsync(c.Requester2.Session);

        foreach (var session in new[] { c.Agent.Session, c.Admin.Session })
        {
            var list = await JsonAsync(await session.GetAsync("/api/v1/tickets"));
            Assert.Equal(2, list.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task InternalComments_AreHiddenFromRequester_EverywhereTheyCouldLeak()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "note secrète", isInternal = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "réponse publique" })).StatusCode);

        var asRequester = await GetTicketAsync(c.Requester.Session, id);
        var asAgent = await GetTicketAsync(c.Agent.Session, id);
        Assert.Equal(["réponse publique"], asRequester.GetProperty("comments").EnumerateArray().Select(x => x.GetProperty("body").GetString()));
        Assert.Equal(["note secrète", "réponse publique"], asAgent.GetProperty("comments").EnumerateArray().Select(x => x.GetProperty("body").GetString()));

        // Nothing else the requester can call mentions the note.
        var everything = new List<string>
        {
            asRequester.GetRawText(),
            await (await c.Requester.Session.GetAsync("/api/v1/tickets")).Content.ReadAsStringAsync(),
            await (await c.Requester.Session.GetAsync($"/api/v1/tickets/{id}/events")).Content.ReadAsStringAsync(),
            await (await c.Requester.Session.GetAsync("/api/v1/tickets?q=secrète")).Content.ReadAsStringAsync(),
        };
        Assert.All(everything, text => Assert.DoesNotContain("note secrète", text));
    }

    [Fact]
    public async Task Requester_CannotPostInternalComment_AndNothingIsStored()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = (await CreateTicketAsync(c.Requester.Session)).GetProperty("id").GetGuid();

        var response = await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "x", isInternal = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await QueryDbAsync(db => db.TicketComments.CountAsync()));
    }

    [Fact]
    public async Task Comment_WithEmptyOrTooLongBody_Is422()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = (await CreateTicketAsync(c.Requester.Session)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = new string('x', 5001) })).StatusCode);
    }

    [Fact]
    public async Task FirstResponseAt_IsSetByTheFirstPublicAgentComment_Only()
    {
        var c = await BuildCompanyAsync("alpha");
        var id = (await CreateTicketAsync(c.Requester.Session)).GetProperty("id").GetGuid();

        await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "relance" });
        await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "note", isInternal = true });
        Assert.Equal(JsonValueKind.Null, (await GetTicketAsync(c.Agent.Session, id)).GetProperty("firstResponseAt").ValueKind);

        var first = await JsonAsync(await c.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "bonjour" }));
        await c.Admin.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "second" });

        var detail = await GetTicketAsync(c.Agent.Session, id);
        Assert.Equal(first.GetProperty("createdAt").GetDateTimeOffset(), detail.GetProperty("firstResponseAt").GetDateTimeOffset());
        Assert.Equal(c.Agent.Id, first.GetProperty("author").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RequesterComment_OnPendingTicket_ReopensItWithAnEvent_AndShiftsSla()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Pending" });
        var dueBefore = ticket.GetProperty("resolutionDueAt").GetDateTimeOffset();
        await Task.Delay(1100);

        var response = await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "voici les infos" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await GetTicketAsync(c.Requester.Session, id);
        Assert.Equal("Open", detail.GetProperty("status").GetString());
        Assert.True(detail.GetProperty("resolutionDueAt").GetDateTimeOffset() >= dueBefore.AddSeconds(1));
        var events = await EventsAsync(c.Requester.Session, id);
        var last = events[^1];
        Assert.Equal("StatusChanged", last.GetProperty("type").GetString());
        Assert.Equal("Pending", last.GetProperty("oldValue").GetString());
        Assert.Equal("Open", last.GetProperty("newValue").GetString());
        Assert.Equal(c.Requester.Id, last.GetProperty("actor").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AgentComment_OnPendingTicket_KeepsItPending()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Pending" });

        await c.Agent.Session.PostAsync($"/api/v1/tickets/{ticket.GetProperty("id").GetGuid()}/comments", new { body = "toujours là ?" });

        Assert.Equal("Pending", (await GetTicketAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Comment_OnClosedTicket_Is409()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        foreach (var status in new[] { "Open", "Resolved", "Closed" })
        {
            ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status });
        }

        var response = await c.Requester.Session.PostAsync($"/api/v1/tickets/{ticket.GetProperty("id").GetGuid()}/comments", new { body = "encore ?" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/ticket-closed", await ProblemTypeAsync(response));
    }

    [Fact]
    public async Task ValidTransitions_FollowTheStateMachine_AndWriteEvents()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();

        foreach (var status in new[] { "Open", "Pending", "Open", "Resolved", "Open", "Resolved", "Closed" })
        {
            ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status });
            Assert.Equal(status, ticket.GetProperty("status").GetString());
        }

        var statusEvents = (await EventsAsync(c.Agent.Session, id)).Where(e => e.GetProperty("type").GetString() == "StatusChanged").ToList();
        Assert.Equal(
            ["New>Open", "Open>Pending", "Pending>Open", "Open>Resolved", "Resolved>Open", "Open>Resolved", "Resolved>Closed"],
            statusEvents.Select(e => $"{e.GetProperty("oldValue").GetString()}>{e.GetProperty("newValue").GetString()}"));
        Assert.All(statusEvents, e => Assert.Equal(c.Agent.Id, e.GetProperty("actor").GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("New", "Pending")]
    [InlineData("New", "Resolved")]
    [InlineData("New", "Closed")]
    [InlineData("Open", "New")]
    [InlineData("Open", "Closed")]
    [InlineData("Pending", "Resolved")]
    [InlineData("Pending", "Closed")]
    [InlineData("Resolved", "Pending")]
    [InlineData("Closed", "Open")]
    [InlineData("Closed", "Resolved")]
    public async Task InvalidTransition_Is409_WithTypeAndLeavesTheTicketUntouched(string from, string to)
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var path = new Dictionary<string, string[]>
        {
            ["New"] = [],
            ["Open"] = ["Open"],
            ["Pending"] = ["Open", "Pending"],
            ["Resolved"] = ["Open", "Resolved"],
            ["Closed"] = ["Open", "Resolved", "Closed"],
        };
        foreach (var status in path[from])
        {
            ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status });
        }

        var response = await PatchTicketAsync(c.Agent.Session, ticket, new { status = to });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith("/invalid-transition", await ProblemTypeAsync(response));
        var after = await GetTicketAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid());
        Assert.Equal(from, after.GetProperty("status").GetString());
        Assert.Equal(ticket.GetProperty("rowVersion").GetString(), after.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task ResolvedAt_IsSetOnResolve_AndClearedOnReopen()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Resolved" });
        Assert.NotEqual(JsonValueKind.Null, ticket.GetProperty("resolvedAt").ValueKind);

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    public async Task LeavingPending_ShiftsTheDeadlinesByTheTimeSpentPending()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });
        var due = ticket.GetProperty("resolutionDueAt").GetDateTimeOffset();
        var responseDue = ticket.GetProperty("responseDueAt").GetDateTimeOffset();
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Pending" });
        Assert.Equal(due, ticket.GetProperty("resolutionDueAt").GetDateTimeOffset());
        await Task.Delay(1500);

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open" });

        Assert.True(ticket.GetProperty("resolutionDueAt").GetDateTimeOffset() >= due.AddSeconds(1.4));
        Assert.True(ticket.GetProperty("responseDueAt").GetDateTimeOffset() >= responseDue.AddSeconds(1.4));
    }

    [Fact]
    public async Task PriorityChange_RecomputesDeadlinesFromCreation_AndWritesAnEvent()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var created = ticket.GetProperty("createdAt").GetDateTimeOffset();

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { priority = "Urgent" });

        Assert.Equal("Urgent", ticket.GetProperty("priority").GetString());
        Assert.Equal(created.AddMinutes(30), ticket.GetProperty("responseDueAt").GetDateTimeOffset());
        Assert.Equal(created.AddMinutes(240), ticket.GetProperty("resolutionDueAt").GetDateTimeOffset());
        var changed = (await EventsAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).Single(e => e.GetProperty("type").GetString() == "PriorityChanged");
        Assert.Equal("Normal", changed.GetProperty("oldValue").GetString());
        Assert.Equal("Urgent", changed.GetProperty("newValue").GetString());
    }

    [Fact]
    public async Task Assignment_SetChangeAndClear_WritesEventsWithNames()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { assigneeId = c.Agent.Id });
        Assert.Equal(c.Agent.Id, ticket.GetProperty("assignee").GetProperty("id").GetGuid());
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { assigneeId = c.Admin.Id });
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { assigneeId = (Guid?)null });
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("assignee").ValueKind);

        var events = (await EventsAsync(c.Agent.Session, id)).Where(e => e.GetProperty("type").GetString() == "AssigneeChanged").ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("oldValue").ValueKind);
        Assert.Equal(events[0].GetProperty("newValue").GetString(), events[1].GetProperty("oldValue").GetString());
        Assert.Equal(events[1].GetProperty("newValue").GetString(), events[2].GetProperty("oldValue").GetString());
        Assert.Equal(JsonValueKind.Null, events[2].GetProperty("newValue").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(events[0].GetProperty("newValue").GetString()));
        Assert.False(Guid.TryParse(events[0].GetProperty("newValue").GetString(), out _));
    }

    [Fact]
    public async Task Assignee_MustBeAnActiveAgentOrAdminOfTheCompany_Else422()
    {
        var c = await BuildCompanyAsync("alpha");
        var other = await BuildCompanyAsync("beta");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        await c.Admin.Session.PatchAsync($"/api/v1/users/{c.Agent.Id}", new { isActive = false });

        var requester = await PatchTicketAsync(c.Admin.Session, ticket, new { assigneeId = c.Requester2.Id });
        var foreign = await PatchTicketAsync(c.Admin.Session, ticket, new { assigneeId = other.Agent.Id });
        var unknown = await PatchTicketAsync(c.Admin.Session, ticket, new { assigneeId = Guid.NewGuid() });
        var inactive = await PatchTicketAsync(c.Admin.Session, ticket, new { assigneeId = c.Agent.Id });

        foreach (var response in new[] { requester, foreign, unknown, inactive })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("assigneeId", out _));
        }

        Assert.Equal(JsonValueKind.Null, (await GetTicketAsync(c.Admin.Session, ticket.GetProperty("id").GetGuid())).GetProperty("assignee").ValueKind);
    }

    [Fact]
    public async Task Category_CanBeChangedAndRemoved_WithEventsUsingNames()
    {
        var c = await BuildCompanyAsync("alpha");
        var network = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var mail = await CreateCategoryAsync(c.Admin.Session, "Messagerie");
        var ticket = await CreateTicketAsync(c.Requester.Session, categoryId: network);

        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { categoryId = mail });
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { categoryId = (Guid?)null });

        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("category").ValueKind);
        var events = (await EventsAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).Where(e => e.GetProperty("type").GetString() == "CategoryChanged").ToList();
        Assert.Equal(["Réseau>Messagerie", "Messagerie>"], events.Select(e => $"{e.GetProperty("oldValue").GetString()}>{e.GetProperty("newValue").GetString()}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PatchTicketAsync(c.Agent.Session, ticket, new { categoryId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Patch_WithoutChange_KeepsVersionAndWritesNoEvent_AndAbsentFieldsAreUntouched()
    {
        var c = await BuildCompanyAsync("alpha");
        var category = await CreateCategoryAsync(c.Admin.Session, "Réseau");
        var ticket = await CreateTicketAsync(c.Requester.Session, categoryId: category);
        ticket = await PatchOkAsync(c.Agent.Session, ticket, new { assigneeId = c.Agent.Id });

        var same = await PatchOkAsync(c.Agent.Session, ticket, new { priority = "Normal", status = "New" });

        Assert.Equal(ticket.GetProperty("rowVersion").GetString(), same.GetProperty("rowVersion").GetString());
        Assert.Equal(c.Agent.Id, same.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal("Réseau", same.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal(2, (await EventsAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).Count);
    }

    [Fact]
    public async Task StaleRowVersion_Is409_WithConcurrencyType_AndTheFirstWriteWins()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);

        await PatchOkAsync(c.Agent.Session, ticket, new { priority = "High" });
        var stale = await PatchTicketAsync(c.Admin.Session, ticket, new { priority = "Low" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.EndsWith("/concurrency-conflict", await ProblemTypeAsync(stale));
        Assert.Equal("High", (await GetTicketAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).GetProperty("priority").GetString());
    }

    [Fact]
    public async Task RowVersion_ChangesOnEveryWrite_IncludingComments()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();

        await c.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "hello" });

        var after = await GetTicketAsync(c.Agent.Session, id);
        Assert.NotEqual(ticket.GetProperty("rowVersion").GetString(), after.GetProperty("rowVersion").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PatchTicketAsync(c.Agent.Session, ticket, new { priority = "Low" })).StatusCode);
    }

    [Fact]
    public async Task ParallelPatches_WithTheSameVersion_OnlyOneWins()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            PatchTicketAsync(c.Agent.Session, ticket, new { priority = i % 2 == 0 ? "High" : "Low" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rowVersion":"abc"}""")]
    [InlineData("""{"rowVersion":"1","status":"Nope"}""")]
    [InlineData("""{"rowVersion":"1","priority":"Nope"}""")]
    public async Task Patch_WithInvalidBody_Is422(string json)
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var request = c.Agent.Session.Request(HttpMethod.Patch, $"/api/v1/tickets/{ticket.GetProperty("id").GetGuid()}");
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Requester_CannotPatch_AndSuperAdminHasNoAccessToTickets()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var id = ticket.GetProperty("id").GetGuid();
        var root = await SignInSuperAdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await PatchTicketAsync(c.Requester.Session, ticket, new { priority = "Low" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync($"/api/v1/tickets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.PostAsync("/api/v1/tickets", new { title = "t", description = "d" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchTicketAsync(root, ticket, new { priority = "Low" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync($"/api/v1/tickets/{id}/events")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/assignees")).StatusCode);
    }

    [Fact]
    public async Task Assignees_ListsActiveAgentsAndAdmins_ForStaffOnly()
    {
        var c = await BuildCompanyAsync("alpha");
        await BuildCompanyAsync("beta");

        var list = (await JsonAsync(await c.Agent.Session.GetAsync("/api/v1/assignees"))).EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { c.Admin.Id, c.Agent.Id }.Order(), list.Order());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Requester.Session.GetAsync("/api/v1/assignees")).StatusCode);

        await c.Admin.Session.PatchAsync($"/api/v1/users/{c.Agent.Id}", new { isActive = false });
        var afterDeactivation = (await JsonAsync(await c.Admin.Session.GetAsync("/api/v1/assignees"))).EnumerateArray().Select(u => u.GetProperty("id").GetGuid());
        Assert.Equal([c.Admin.Id], afterDeactivation);
    }

    [Fact]
    public async Task Events_AreOrderedOldestFirst_EvenWhenWrittenTogether()
    {
        var c = await BuildCompanyAsync("alpha");
        var ticket = await CreateTicketAsync(c.Requester.Session);
        var category = await CreateCategoryAsync(c.Admin.Session, "Réseau");

        await PatchOkAsync(c.Agent.Session, ticket, new { status = "Open", priority = "High", assigneeId = c.Agent.Id, categoryId = category });

        var types = (await EventsAsync(c.Agent.Session, ticket.GetProperty("id").GetGuid())).Select(e => e.GetProperty("type").GetString());
        Assert.Equal(["Created", "StatusChanged", "PriorityChanged", "AssigneeChanged", "CategoryChanged"], types);
    }
}
