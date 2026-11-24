using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>A connected hub client and what it received.</summary>
public sealed class HubProbe(HubConnection connection) : IAsyncDisposable
{
    public HubConnection Connection { get; } = connection;

    public ConcurrentQueue<(string Method, JsonElement Payload)> Received { get; } = new();

    public IEnumerable<JsonElement> Of(string method) => Received.Where(r => r.Method == method).Select(r => r.Payload);

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>Builds hub clients that talk to the in-process API (WebSocket transport, token in the query string).</summary>
public static class HubClients
{
    public static async Task<HubProbe> ConnectAsync(DeskFactory factory, string? token, bool skipNegotiation = false)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost/hubs/notifications"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = skipNegotiation;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                // A browser cannot set a header on a WebSocket: the JavaScript client puts the token in the query string. The .NET
                // client would use an Authorization header instead, so this factory does what the real (Angular) client does.
                o.WebSocketFactory = async (context, ct) =>
                {
                    var uri = token is null
                        ? context.Uri
                        : new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}");
                    return await factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
                };
                if (token is not null)
                {
                    o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
        var probe = new HubProbe(connection);
        foreach (var method in new[] { "ticketCreated", "ticketAssigned", "ticketReplied" })
        {
            connection.On<JsonElement>(method, payload => probe.Received.Enqueue((method, payload)));
        }

        try
        {
            await connection.StartAsync();
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }

        await Task.Delay(300); // the server joins the groups right after the handshake
        return probe;
    }
}

/// <summary>
/// The SignalR hub and the real-time consumer, whole chain: HTTP action in the API -> outbox -> dispatcher -> RabbitMQ ->
/// AgentRealtimeConsumer (in the API) -> hub -> WebSocket client. The API and the Worker run in process on the test's
/// database and virtual host; nothing between them is replaced.
/// </summary>
public class RealtimeHubTests(PostgresFixture postgres, RabbitMqFixture rabbit) : TicketTestBase(postgres)
{
    private TestBroker _broker = null!;
    private TestWorker _worker = null!;
    private readonly List<HubProbe> _probes = [];

    protected override Dictionary<string, string?>? Settings => new()
    {
        ["ConnectionStrings:RabbitMq"] = _broker.Uri,
        ["Messaging:RetryDelays:0"] = "00:00:00.4",
        ["Messaging:RetryDelays:1"] = "00:00:00.8",
        ["Messaging:RetryDelays:2"] = "00:00:01.2",
    };

    public override async Task InitializeAsync()
    {
        _broker = await rabbit.NewBrokerAsync();
        await base.InitializeAsync();
        var connection = Factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Postgres"]!;
        _worker = await TestWorker.StartAsync(connection, _broker, new() { ["Sla:Enabled"] = "false" });
        // The API's own consumer subscribes in the background.
        await Poll.UntilAsync(async () => await _broker.ConsumersAsync("desk.agent-realtime") >= 1, "AgentRealtimeConsumer subscribed", 30);
    }

    public override async Task DisposeAsync()
    {
        foreach (var probe in _probes)
        {
            await probe.DisposeAsync();
        }

        await _worker.DisposeAsync();
        await base.DisposeAsync();
        await _broker.DisposeAsync();
    }

    private async Task<HubProbe> ConnectAsync(Session session, bool skipNegotiation = false)
    {
        var probe = await HubClients.ConnectAsync(Factory, session.AccessToken, skipNegotiation);
        _probes.Add(probe);
        return probe;
    }

    private async Task<Member> AddAgentAsync(Company company, string slug, string name)
    {
        var (id, session) = await InviteAndSignInAsync(company.Admin.Session, $"{name}@{slug}.test", "Agent");
        return new Member(id, session);
    }

    private static async Task SettleAsync() => await Task.Delay(1500); // negative assertions: let a wrong delivery show up

    // ---- handshake and authorisation ----

    [Fact]
    public async Task The_connection_is_refused_without_token_for_a_requester_and_for_the_super_admin()
    {
        var company = await BuildCompanyAsync("hub-auth");
        var root = await SignInSuperAdminAsync();

        var anonymous = await Assert.ThrowsAnyAsync<HttpRequestException>(() => HubClients.ConnectAsync(Factory, null));
        var requester = await Assert.ThrowsAnyAsync<HttpRequestException>(() => HubClients.ConnectAsync(Factory, company.Requester.Session.AccessToken));
        var superAdmin = await Assert.ThrowsAnyAsync<HttpRequestException>(() => HubClients.ConnectAsync(Factory, root.AccessToken));
        var forged = await Assert.ThrowsAnyAsync<HttpRequestException>(() => HubClients.ConnectAsync(Factory, company.Agent.Session.AccessToken + "x"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, requester.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, superAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task An_agent_and_a_tenant_admin_connect_with_the_token_in_the_query_string_alone()
    {
        var company = await BuildCompanyAsync("hub-ok");

        // SkipNegotiation: the only request is the WebSocket upgrade, which carries the token as ?access_token= (no header possible).
        var agent = await ConnectAsync(company.Agent.Session, skipNegotiation: true);
        var admin = await ConnectAsync(company.Admin.Session);

        Assert.Equal(HubConnectionState.Connected, agent.Connection.State);
        Assert.Equal(HubConnectionState.Connected, admin.Connection.State);
    }

    [Fact]
    public async Task A_token_in_the_query_string_is_accepted_for_the_hub_only()
    {
        var company = await BuildCompanyAsync("hub-query");
        var token = Uri.EscapeDataString(company.Agent.Session.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync($"/api/v1/me?access_token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync($"/api/v1/tickets?access_token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await company.Agent.Session.GetAsync("/api/v1/me")).StatusCode); // the header still works
    }

    // ---- events ----

    [Fact]
    public async Task A_new_ticket_reaches_the_agents_of_its_company_and_never_another_company()
    {
        var a = await BuildCompanyAsync("hub-a");
        var b = await BuildCompanyAsync("hub-b");
        var agentA = await ConnectAsync(a.Agent.Session);
        var adminA = await ConnectAsync(a.Admin.Session);
        var agentB = await ConnectAsync(b.Agent.Session);
        var adminB = await ConnectAsync(b.Admin.Session);

        var ticket = await CreateTicketAsync(a.Requester.Session, "Écran noir <b>urgent</b>", priority: "High");

        await Poll.UntilAsync(() => agentA.Of("ticketCreated").Any() && adminA.Of("ticketCreated").Any(), "ticketCreated for both agents of company A", 20);
        await SettleAsync();
        foreach (var received in new[] { agentA, adminA })
        {
            var payload = Assert.Single(received.Of("ticketCreated"));
            Assert.Equal(ticket.GetProperty("id").GetGuid(), payload.GetProperty("ticketId").GetGuid());
            Assert.Equal(ticket.GetProperty("number").GetInt32(), payload.GetProperty("number").GetInt32());
            Assert.Equal("Écran noir <b>urgent</b>", payload.GetProperty("title").GetString());
            Assert.Equal("High", payload.GetProperty("priority").GetString());
            Assert.True(DateTimeOffset.TryParse(payload.GetProperty("at").GetString(), out _));
            Assert.Equal(["at", "number", "priority", "ticketId", "title"], payload.EnumerateObject().Select(p => p.Name).Order());
            Assert.DoesNotContain("Impossible de me connecter", payload.GetRawText()); // the description never travels
        }

        Assert.Empty(agentB.Received);
        Assert.Empty(adminB.Received);
        Assert.Equal(0u, await _broker.CountAsync(MessagingNames.DlqQueue));
        // The same event also sent the acknowledgement e-mail (Worker): one chain, two effects.
        await Poll.UntilAsync(() => _worker.Email.Sent.Any(m => m.To == "req1@hub-a.test"), "acknowledgement e-mail");
    }

    [Fact]
    public async Task An_assignment_reaches_the_assignee_only_and_emails_him()
    {
        var a = await BuildCompanyAsync("hub-assign");
        var b = await BuildCompanyAsync("hub-assign-b");
        var second = await AddAgentAsync(a, "hub-assign", "agent2");
        var assignee = await ConnectAsync(second.Session);
        var otherAgent = await ConnectAsync(a.Agent.Session);
        var admin = await ConnectAsync(a.Admin.Session);
        var foreignAgent = await ConnectAsync(b.Agent.Session);
        var ticket = await CreateTicketAsync(a.Requester.Session, "Accès VPN");
        await Poll.UntilAsync(() => otherAgent.Of("ticketCreated").Any(), "creation delivered first", 20);

        await PatchOkAsync(a.Agent.Session, ticket, new { assigneeId = second.Id });

        await Poll.UntilAsync(() => assignee.Of("ticketAssigned").Any(), "ticketAssigned", 20);
        await SettleAsync();
        var payload = Assert.Single(assignee.Of("ticketAssigned"));
        Assert.Equal(ticket.GetProperty("id").GetGuid(), payload.GetProperty("ticketId").GetGuid());
        Assert.Equal("Accès VPN", payload.GetProperty("title").GetString());
        Assert.Equal(["at", "number", "ticketId", "title"], payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Empty(otherAgent.Of("ticketAssigned"));
        Assert.Empty(admin.Of("ticketAssigned"));
        Assert.Empty(foreignAgent.Received);

        await Poll.UntilAsync(() => _worker.Email.Sent.Any(m => m.To == "agent2@hub-assign.test"), "assignment e-mail");
        Assert.Equal("[#" + ticket.GetProperty("number").GetInt32() + "] Un ticket vous a été assigné",
            _worker.Email.Sent.Single(m => m.To == "agent2@hub-assign.test").Subject);
    }

    [Fact]
    public async Task A_public_reply_of_the_requester_reaches_the_assignee_only_never_an_agent_answer_or_an_internal_note()
    {
        var a = await BuildCompanyAsync("hub-reply");
        var b = await BuildCompanyAsync("hub-reply-b");
        var second = await AddAgentAsync(a, "hub-reply", "agent2");
        var assignee = await ConnectAsync(second.Session);
        var otherAgent = await ConnectAsync(a.Agent.Session);
        var foreignAgent = await ConnectAsync(b.Agent.Session);
        var ticket = await CreateTicketAsync(a.Requester.Session, "Imprimante");
        var id = ticket.GetProperty("id").GetGuid();
        await PatchOkAsync(a.Agent.Session, ticket, new { assigneeId = second.Id });
        await Poll.UntilAsync(() => assignee.Of("ticketAssigned").Any(), "assignment delivered first", 20);

        // Agent answer then internal note: nothing for the assignee.
        Assert.Equal(HttpStatusCode.Created, (await a.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "Réponse agent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await a.Agent.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "Note", isInternal = true })).StatusCode);
        await Poll.UntilAsync(() => _worker.Email.Sent.Any(m => m.To == "req1@hub-reply.test" && m.Subject.Contains("Nouvelle réponse")), "requester e-mail for the agent answer");
        await SettleAsync();
        Assert.Empty(assignee.Of("ticketReplied"));

        // The requester answers: the assignee is told, in real time and by e-mail.
        Assert.Equal(HttpStatusCode.Created, (await a.Requester.Session.PostAsync($"/api/v1/tickets/{id}/comments", new { body = "Texte confidentiel du demandeur" })).StatusCode);
        await Poll.UntilAsync(() => assignee.Of("ticketReplied").Any(), "ticketReplied", 20);
        await SettleAsync();

        var payload = Assert.Single(assignee.Of("ticketReplied"));
        Assert.Equal(id, payload.GetProperty("ticketId").GetGuid());
        Assert.Equal("Imprimante", payload.GetProperty("title").GetString());
        Assert.DoesNotContain("confidentiel", payload.GetRawText());
        Assert.Empty(otherAgent.Of("ticketReplied"));
        Assert.Empty(foreignAgent.Of("ticketReplied"));
        Assert.Empty(foreignAgent.Of("ticketAssigned"));
        await Poll.UntilAsync(() => _worker.Email.Sent.Any(m => m.To == "agent2@hub-reply.test" && m.Subject.Contains("demandeur a répondu")), "e-mail to the assignee");
        Assert.DoesNotContain(_worker.Email.Sent, m => m.Body.Contains("confidentiel"));
    }

    [Fact]
    public async Task A_redelivered_event_notifies_once_and_a_message_of_a_foreign_tenant_is_dead_lettered_unseen()
    {
        var a = await BuildCompanyAsync("hub-dup");
        var b = await BuildCompanyAsync("hub-dup-b");
        var agentA = await ConnectAsync(a.Agent.Session);
        var agentB = await ConnectAsync(b.Agent.Session);
        var ticket = await CreateTicketAsync(a.Requester.Session, "Doublon");
        await Poll.UntilAsync(() => agentA.Of("ticketCreated").Any(), "first notification", 20);
        var id = ticket.GetProperty("id").GetGuid();
        var payload = new TicketCreatedPayload(id, ticket.GetProperty("number").GetInt32(), "Doublon", Priority.Normal, a.Requester.Id);

        var duplicate = MessageEnvelope.Create(Guid.NewGuid(), EventTypes.TicketCreated, a.TenantId, DateTimeOffset.UtcNow, "corr-dup", payload);
        await _broker.PublishAsync(MessagingNames.EventsExchange, duplicate.Type, duplicate.ToUtf8Json(), 0, a.TenantId.ToString(), duplicate.MessageId.ToString());
        await _broker.PublishAsync(MessagingNames.EventsExchange, duplicate.Type, duplicate.ToUtf8Json(), 0, a.TenantId.ToString(), duplicate.MessageId.ToString());
        var foreign = MessageEnvelope.Create(Guid.NewGuid(), EventTypes.TicketCreated, b.TenantId, DateTimeOffset.UtcNow, "corr-foreign", payload);
        await _broker.PublishAsync(MessagingNames.EventsExchange, foreign.Type, foreign.ToUtf8Json(), 0, b.TenantId.ToString(), foreign.MessageId.ToString());

        await Poll.UntilAsync(async () => await _broker.CountAsync(MessagingNames.DlqQueue) >= 2, "foreign message dead-lettered (real-time and e-mail consumers)", 30);
        await SettleAsync();
        Assert.Equal(2, agentA.Of("ticketCreated").Count()); // the ticket's own event + the duplicated one, delivered twice but notified once
        Assert.Empty(agentB.Received);
    }
}

/// <summary>The API must start, and the hub must accept an agent, while the broker is unreachable.</summary>
public class HubWithoutBrokerTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    [Fact]
    public async Task The_api_starts_and_the_hub_works_while_the_broker_is_down()
    {
        // The factory points RabbitMQ at 127.0.0.1:1: nothing listens there.
        var company = await BuildCompanyAsync("hub-nobroker");

        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/health")).StatusCode);
        await using var agent = await HubClients.ConnectAsync(Factory, company.Agent.Session.AccessToken);

        Assert.Equal(HubConnectionState.Connected, agent.Connection.State);
        Assert.Equal(HttpStatusCode.Created, (await company.Requester.Session.PostAsync("/api/v1/tickets",
            new { title = "Sans broker", description = "Le ticket est créé quand même." })).StatusCode);
    }
}
