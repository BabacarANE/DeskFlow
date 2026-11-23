using System.Net;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>The API writes its events to the outbox in the business transaction (no broker needed).</summary>
public class OutboxWriteTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private Task<List<(OutboxMessage Row, JsonElement Envelope)>> OutboxAsync() =>
        QueryDbAsync(async db =>
        {
            var rows = await db.OutboxMessages.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Type).ToListAsync();
            return rows.Select(r => (r, JsonDocument.Parse(r.Payload).RootElement.Clone())).ToList();
        });

    /// <summary>jsonb does not keep key order: the contract is the set of fields.</summary>
    private static void AssertKeys(JsonElement element, string[] expected) =>
        Assert.Equal(expected.Order(), element.EnumerateObject().Select(p => p.Name).Order());

    private static Task<HttpResponseMessage> CommentAsync(Session session, Guid ticketId, string body, bool isInternal) =>
        session.PostAsync($"/api/v1/tickets/{ticketId}/comments", new { body, isInternal });

    [Fact]
    public async Task Creating_a_ticket_writes_ticket_created_with_the_contract_payload_and_the_request_correlation_id()
    {
        var company = await BuildCompanyAsync("outbox-created");
        var request = company.Requester.Session.Request(HttpMethod.Post, "/api/v1/tickets",
            new { title = "Imprimante HS", description = "Bourrage papier.", priority = "High" });
        request.Headers.Add("X-Correlation-Id", "corr-from-client-1");

        var response = await company.Requester.Session.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("corr-from-client-1", response.Headers.GetValues("X-Correlation-Id").Single());
        var ticket = await JsonAsync(response);
        var (row, envelope) = Assert.Single(await OutboxAsync());
        Assert.Null(row.DispatchedAt);
        Assert.Equal("ticket.created", row.Type);
        Assert.Equal(row.Id, envelope.GetProperty("messageId").GetGuid());
        Assert.Equal("ticket.created", envelope.GetProperty("type").GetString());
        Assert.Equal(company.TenantId, envelope.GetProperty("tenantId").GetGuid());
        Assert.Equal("corr-from-client-1", envelope.GetProperty("correlationId").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", envelope.GetProperty("occurredAt").GetString()!);

        var payload = envelope.GetProperty("payload");
        AssertKeys(payload, ["ticketId", "number", "title", "priority", "requesterId"]);
        Assert.Equal(ticket.GetProperty("id").GetGuid(), payload.GetProperty("ticketId").GetGuid());
        Assert.Equal(ticket.GetProperty("number").GetInt32(), payload.GetProperty("number").GetInt32());
        Assert.Equal("Imprimante HS", payload.GetProperty("title").GetString());
        Assert.Equal("High", payload.GetProperty("priority").GetString());
        Assert.Equal(company.Requester.Id, payload.GetProperty("requesterId").GetGuid());
    }

    [Fact]
    public async Task A_request_without_correlation_id_gets_one_and_the_event_carries_it()
    {
        var company = await BuildCompanyAsync("outbox-corr");

        var response = await company.Requester.Session.PostAsync("/api/v1/tickets", new { title = "Souris", description = "Ne répond plus." });

        var correlation = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEmpty(correlation);
        var (_, envelope) = Assert.Single(await OutboxAsync());
        Assert.Equal(correlation, envelope.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task An_unsafe_correlation_id_from_the_client_is_replaced()
    {
        var company = await BuildCompanyAsync("outbox-unsafe");
        var request = company.Requester.Session.Request(HttpMethod.Post, "/api/v1/tickets", new { title = "Souris", description = "Ne répond plus." });
        request.Headers.Add("X-Correlation-Id", "bad id with spaces and \"quotes\"");

        var response = await company.Requester.Session.Http.SendAsync(request);

        Assert.DoesNotContain("quotes", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Assigning_and_reassigning_write_ticket_assigned_with_the_previous_assignee()
    {
        var company = await BuildCompanyAsync("outbox-assigned");
        var (otherAgentId, _) = await InviteAndSignInAsync(company.Admin.Session, "agent2@outbox-assigned.test", "Agent");
        var ticket = await CreateTicketAsync(company.Requester.Session, "Écran noir");
        var ticketId = ticket.GetProperty("id").GetGuid();

        var assigned = await PatchOkAsync(company.Agent.Session, ticket, new { assigneeId = company.Agent.Id });
        await PatchOkAsync(company.Admin.Session, assigned, new { assigneeId = otherAgentId });

        var events = (await OutboxAsync()).Where(e => e.Row.Type == "ticket.assigned").ToList();
        Assert.Equal(2, events.Count);
        var first = events[0].Envelope.GetProperty("payload");
        AssertKeys(first, ["ticketId", "number", "title", "assigneeId", "previousAssigneeId", "actorId"]);
        Assert.Equal(ticketId, first.GetProperty("ticketId").GetGuid());
        Assert.Equal("Écran noir", first.GetProperty("title").GetString());
        Assert.Equal(company.Agent.Id, first.GetProperty("assigneeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previousAssigneeId").ValueKind);
        Assert.Equal(company.Agent.Id, first.GetProperty("actorId").GetGuid());

        var second = events[1].Envelope.GetProperty("payload");
        Assert.Equal(otherAgentId, second.GetProperty("assigneeId").GetGuid());
        Assert.Equal(company.Agent.Id, second.GetProperty("previousAssigneeId").GetGuid());
        Assert.Equal(company.Admin.Id, second.GetProperty("actorId").GetGuid());
    }

    [Fact]
    public async Task Unassigning_or_resending_the_same_assignee_writes_no_assignment_event()
    {
        var company = await BuildCompanyAsync("outbox-unassign");
        var ticket = await CreateTicketAsync(company.Requester.Session);
        var assigned = await PatchOkAsync(company.Agent.Session, ticket, new { assigneeId = company.Agent.Id });
        var same = await PatchOkAsync(company.Agent.Session, assigned, new { assigneeId = company.Agent.Id });
        await PatchOkAsync(company.Agent.Session, same, new { assigneeId = (Guid?)null });

        Assert.Single(await OutboxAsync(), e => e.Row.Type == "ticket.assigned");
    }

    [Fact]
    public async Task Only_public_comments_write_ticket_commented_and_never_the_body()
    {
        var company = await BuildCompanyAsync("outbox-commented");
        var ticket = await CreateTicketAsync(company.Requester.Session);
        var ticketId = ticket.GetProperty("id").GetGuid();

        var internalNote = await CommentAsync(company.Agent.Session, ticketId, "note interne secrète", isInternal: true);
        var publicReply = await CommentAsync(company.Agent.Session, ticketId, "réponse publique visible", isInternal: false);

        Assert.Equal(HttpStatusCode.Created, internalNote.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publicReply.StatusCode);
        var commented = (await OutboxAsync()).Where(e => e.Row.Type == "ticket.commented").ToList();
        var (row, envelope) = Assert.Single(commented);
        var payload = envelope.GetProperty("payload");
        AssertKeys(payload, ["ticketId", "number", "commentId", "authorId"]);
        Assert.Equal(company.Agent.Id, payload.GetProperty("authorId").GetGuid());
        var commentId = (await JsonAsync(publicReply)).GetProperty("id").GetGuid();
        Assert.Equal(commentId, payload.GetProperty("commentId").GetGuid());
        Assert.DoesNotContain("réponse publique", row.Payload);
        Assert.DoesNotContain("secrète", string.Concat((await OutboxAsync()).Select(e => e.Row.Payload)));
    }

    [Fact]
    public async Task Resolving_a_ticket_writes_ticket_resolved_once()
    {
        var company = await BuildCompanyAsync("outbox-resolved");
        var ticket = await CreateTicketAsync(company.Requester.Session);
        var open = await PatchOkAsync(company.Agent.Session, ticket, new { status = "Open" });
        var resolved = await PatchOkAsync(company.Agent.Session, open, new { status = "Resolved" });
        await PatchOkAsync(company.Agent.Session, resolved, new { priority = "High" });

        var (_, envelope) = Assert.Single(await OutboxAsync(), e => e.Row.Type == "ticket.resolved");
        var payload = envelope.GetProperty("payload");
        AssertKeys(payload, ["ticketId", "number", "resolvedAt"]);
        Assert.Equal(ticket.GetProperty("id").GetGuid(), payload.GetProperty("ticketId").GetGuid());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", payload.GetProperty("resolvedAt").GetString()!);
    }

    [Fact]
    public async Task A_failure_after_the_ticket_rows_are_written_leaves_neither_ticket_nor_message()
    {
        var company = await BuildCompanyAsync("outbox-atomic-create");
        Factory.Faults.FailAfterSave = true;

        var failed = await company.Requester.Session.PostAsync("/api/v1/tickets", new { title = "Perdu", description = "Ne doit pas exister." });
        Factory.Faults.FailAfterSave = false;

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Empty(await OutboxAsync());
        Assert.Equal(0, await QueryDbAsync(db => db.Tickets.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0, await QueryDbAsync(db => db.TicketEvents.IgnoreQueryFilters().CountAsync()));

        // The transaction rolled the sequence back too: the next ticket is number 1 and has its message.
        var ok = await CreateTicketAsync(company.Requester.Session, "Bien créé");
        Assert.Equal(1, ok.GetProperty("number").GetInt32());
        Assert.Single(await OutboxAsync());
    }

    [Fact]
    public async Task When_the_outbox_insert_fails_the_assignment_is_not_applied_either()
    {
        var company = await BuildCompanyAsync("outbox-atomic-assign");
        var ticket = await CreateTicketAsync(company.Requester.Session);
        var before = (await OutboxAsync()).Count;
        Factory.Faults.FailOutboxInsert = true;

        var failed = await PatchTicketAsync(company.Agent.Session, ticket, new { assigneeId = company.Agent.Id });
        Factory.Faults.FailOutboxInsert = false;

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, (await OutboxAsync()).Count);
        var current = await GetTicketAsync(company.Agent.Session, ticket.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("assignee").ValueKind);
        Assert.Single(await EventsAsync(company.Agent.Session, ticket.GetProperty("id").GetGuid())); // only "Created"
    }

    [Fact]
    public async Task When_the_outbox_insert_fails_the_comment_is_not_stored_either()
    {
        var company = await BuildCompanyAsync("outbox-atomic-comment");
        var ticket = await CreateTicketAsync(company.Requester.Session);
        var ticketId = ticket.GetProperty("id").GetGuid();
        Factory.Faults.FailOutboxInsert = true;

        var failed = await CommentAsync(company.Agent.Session, ticketId, "réponse perdue", isInternal: false);
        Factory.Faults.FailOutboxInsert = false;

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Empty((await GetTicketAsync(company.Agent.Session, ticketId)).GetProperty("comments").EnumerateArray());
        Assert.DoesNotContain(await OutboxAsync(), e => e.Row.Type == "ticket.commented");
    }
}
