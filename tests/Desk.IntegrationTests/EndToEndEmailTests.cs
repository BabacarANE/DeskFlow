using System.Diagnostics;
using System.Net;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

/// <summary>
/// The whole chain with nothing replaced: API (in process) -> PostgreSQL outbox -> the real Worker process (dispatcher,
/// RabbitMQ, EmailAckConsumer, MailKit) -> Mailpit over SMTP. Mailpit is the development mail server of docker-compose:
/// it speaks real SMTP and exposes what it received, which a hand-made fake server would only imitate.
/// </summary>
public class EndToEndEmailTests(PostgresFixture postgres, RabbitMqFixture rabbit, MailpitFixture mailpit) : TicketTestBase(postgres)
{
    private TestBroker _broker = null!;
    private WorkerProcess _worker = null!;

    protected override Dictionary<string, string?>? Settings => new() { ["ConnectionStrings:RabbitMq"] = _broker.Uri };

    public override async Task InitializeAsync()
    {
        _broker = await rabbit.NewBrokerAsync();
        await mailpit.EnsureStartedAsync();
        await base.InitializeAsync(); // API: migrates the database, declares the topology in the background

        var connection = Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Postgres"]!;
        _worker = WorkerProcess.Start(new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = connection,
            ["ConnectionStrings__RabbitMq"] = _broker.Uri,
            ["Smtp__Host"] = mailpit.SmtpHost,
            ["Smtp__Port"] = mailpit.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Smtp__UseTls"] = "false",
            ["App__PublicBaseUrl"] = "http://deskflow.test",
        });

        // The Worker is ready once its consumer is subscribed.
        await Poll.UntilAsync(async () => await _broker.ConsumersAsync("desk.email-ack") >= 1, "EmailAckConsumer subscribed\n" + _worker.Output, 60);
    }

    public override async Task DisposeAsync()
    {
        await _worker.DisposeAsync();
        await _broker.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Creating_a_ticket_sends_the_acknowledgement_visible_in_mailpit_in_under_5_seconds()
    {
        var company = await BuildCompanyAsync("e2e-mail");

        var clock = Stopwatch.StartNew();
        var ticket = await CreateTicketAsync(company.Requester.Session, "VPN en panne <urgent>");
        var requesterEmail = "req1@e2e-mail.test";
        List<MailpitMessage> mails = [];
        await Poll.UntilAsync(async () => (mails = await mailpit.MessagesToAsync(requesterEmail)).Count > 0, "e-mail in Mailpit\n" + _worker.Output, 5);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"e-mail visible after {clock.Elapsed}");
        var mail = Assert.Single(mails);
        Assert.Equal($"[#{ticket.GetProperty("number").GetInt32()}] Votre demande a bien été reçue", mail.Subject);
        Assert.Contains("VPN en panne <urgent>", mail.Text);
        Assert.Contains("VPN en panne &lt;urgent&gt;", mail.Html); // HTML part is encoded
        Assert.Contains($"http://deskflow.test/tickets/{ticket.GetProperty("id").GetGuid()}", mail.Text);
        Assert.Equal(0u, await _broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task Two_tickets_send_two_distinct_acknowledgements_and_no_one_else_is_mailed()
    {
        var company = await BuildCompanyAsync("e2e-two");

        await CreateTicketAsync(company.Requester.Session, "Premier");
        await CreateTicketAsync(company.Requester2.Session, "Second");

        await Poll.UntilAsync(async () => (await mailpit.MessagesToAsync("req1@e2e-two.test")).Count == 1
            && (await mailpit.MessagesToAsync("req2@e2e-two.test")).Count == 1, "one e-mail for each requester", 10);
        Assert.Empty(await mailpit.MessagesToAsync("agent@e2e-two.test"));
        Assert.Empty(await mailpit.MessagesToAsync("admin@e2e-two.test"));
    }

    [Fact]
    public async Task The_worker_health_endpoint_reports_postgres_and_rabbitmq()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_worker.Port}") };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        var ready = await http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }
}

/// <summary>The API declares the topology by itself at startup, before any Worker runs.</summary>
public class ApiTopologyTests(PostgresFixture postgres, RabbitMqFixture rabbit) : ApiTestBase(postgres)
{
    private TestBroker _broker = null!;

    protected override Dictionary<string, string?>? Settings => new() { ["ConnectionStrings:RabbitMq"] = _broker.Uri };

    public override async Task InitializeAsync()
    {
        _broker = await rabbit.NewBrokerAsync();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await _broker.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task The_topology_exists_once_the_api_has_started_and_the_readiness_probe_still_answers()
    {
        var ready = await Http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // Declaration is sequential: wait for the LAST queue declared (not the first, the DLQ), or the asserts below race it.
        await Poll.UntilAsync(async () => await _broker.ExistsAsync("desk.agent-realtime.retry.2m"), "topology declared by the API", 20);
        Assert.True(await _broker.ExistsAsync(MessagingNames.EventsExchange, exchange: true));
        Assert.True(await _broker.ExistsAsync(MessagingNames.RetryExchange, exchange: true));
        Assert.True(await _broker.ExistsAsync(MessagingNames.DlxExchange, exchange: true));
        Assert.True(await _broker.ExistsAsync("desk.email-ack"));
        Assert.True(await _broker.ExistsAsync("desk.email-ack.retry.5s"));
        Assert.True(await _broker.ExistsAsync("desk.email-ack.retry.30s"));
        Assert.True(await _broker.ExistsAsync("desk.email-ack.retry.2m"));
    }
}
