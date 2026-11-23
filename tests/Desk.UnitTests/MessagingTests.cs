using System.Text;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Messaging;
using Desk.Infrastructure.Messaging.Consumers;

namespace Desk.UnitTests;

public class EnvelopeTests
{
    private static readonly Guid MessageId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset At = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static MessageEnvelope Sample() => MessageEnvelope.Create(
        MessageId, EventTypes.TicketCreated, TenantId, At, "corr-1",
        new TicketCreatedPayload(Guid.Parse("33333333-3333-3333-3333-333333333333"), 7, "VPN", Priority.Urgent, Guid.Parse("44444444-4444-4444-4444-444444444444")));

    [Fact]
    public void Envelope_follows_the_frozen_contract()
    {
        using var json = JsonDocument.Parse(Sample().ToJson());
        var root = json.RootElement;

        Assert.Equal(["messageId", "type", "tenantId", "occurredAt", "correlationId", "payload"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(MessageId.ToString(), root.GetProperty("messageId").GetString());
        Assert.Equal("ticket.created", root.GetProperty("type").GetString());
        Assert.Equal(TenantId.ToString(), root.GetProperty("tenantId").GetString());
        Assert.Equal("2026-11-23T10:00:00.000000Z", root.GetProperty("occurredAt").GetString());
        Assert.Equal("corr-1", root.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void Occurred_at_is_always_written_in_utc_with_six_decimals()
    {
        var local = new DateTimeOffset(2026, 11, 23, 11, 0, 0, 123, TimeSpan.FromHours(1)).AddTicks(4560);
        var envelope = MessageEnvelope.Create(MessageId, EventTypes.TicketResolved, TenantId, local, "c",
            new TicketResolvedPayload(Guid.Empty, 1, local));

        using var json = JsonDocument.Parse(envelope.ToJson());
        Assert.Equal("2026-11-23T10:00:00.123456Z", json.RootElement.GetProperty("occurredAt").GetString());
        Assert.Equal("2026-11-23T10:00:00.123456Z", json.RootElement.GetProperty("payload").GetProperty("resolvedAt").GetString());
    }

    [Fact]
    public void Created_payload_uses_camel_case_and_the_priority_string()
    {
        using var json = JsonDocument.Parse(Sample().ToJson());
        var payload = json.RootElement.GetProperty("payload");

        Assert.Equal(["ticketId", "number", "title", "priority", "requesterId"], payload.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Urgent", payload.GetProperty("priority").GetString());
        Assert.Equal(7, payload.GetProperty("number").GetInt32());
    }

    [Fact]
    public void Assigned_payload_keeps_a_null_previous_assignee()
    {
        var envelope = MessageEnvelope.Create(MessageId, EventTypes.TicketAssigned, TenantId, At, "c",
            new TicketAssignedPayload(Guid.NewGuid(), 3, "t", Guid.NewGuid(), null, Guid.NewGuid()));

        using var json = JsonDocument.Parse(envelope.ToJson());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(["ticketId", "number", "title", "assigneeId", "previousAssigneeId", "actorId"],
            payload.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("previousAssigneeId").ValueKind);
    }

    [Fact]
    public void Payloads_never_carry_an_address_or_a_comment_body()
    {
        var commented = JsonSerializer.Serialize(new TicketCommentedPayload(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid()), EventJson.Options);
        Assert.DoesNotContain("email", commented, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", commented, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_valid_envelope_survives_a_round_trip()
    {
        var original = Sample();

        Assert.True(MessageEnvelope.TryParse(original.ToUtf8Json(), out var parsed, out var reason), reason);
        Assert.Equal(original.MessageId, parsed.MessageId);
        Assert.Equal(original.TenantId, parsed.TenantId);
        Assert.Equal(original.OccurredAt, parsed.OccurredAt);
        Assert.Equal(original.GetPayload<TicketCreatedPayload>(), parsed.GetPayload<TicketCreatedPayload>());
    }

    public static TheoryData<string, string> PoisonedMessages() => new()
    {
        { "not json at all", "Invalid JSON" },
        { "", "Invalid JSON" },
        { "[1,2]", "Invalid JSON" },
        { "null", "Empty message" },
        { """{"type":"ticket.created","tenantId":"22222222-2222-2222-2222-222222222222","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "messageId" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "tenantId" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":null,"occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "tenantId" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"00000000-0000-0000-0000-000000000000","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "tenantId" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"not-a-guid","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "Invalid JSON" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"","tenantId":"22222222-2222-2222-2222-222222222222","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":{}}""", "type" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"22222222-2222-2222-2222-222222222222","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c"}""", "Payload" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"22222222-2222-2222-2222-222222222222","occurredAt":"2026-11-23T10:00:00.000000Z","correlationId":"c","payload":"text"}""", "Payload" },
        { """{"messageId":"11111111-1111-1111-1111-111111111111","type":"ticket.created","tenantId":"22222222-2222-2222-2222-222222222222","correlationId":"c","payload":{}}""", "occurredAt" },
    };

    [Theory]
    [MemberData(nameof(PoisonedMessages))]
    public void A_poisoned_message_is_rejected_with_a_reason(string body, string expectedReason)
    {
        Assert.False(MessageEnvelope.TryParse(Encoding.UTF8.GetBytes(body), out var envelope, out var reason));
        Assert.Null(envelope);
        Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Default_delays_are_5_seconds_30_seconds_and_2_minutes()
    {
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)], new MessagingOptions().BuildRetryPolicy().Delays);
    }

    [Theory]
    [InlineData(0, true, 1, 5)]
    [InlineData(1, true, 2, 30)]
    [InlineData(2, true, 3, 120)]
    [InlineData(3, false, 0, 0)]
    [InlineData(10, false, 0, 0)]
    public void The_delay_grows_then_the_message_goes_to_the_dead_letter_queue(int failedAttempt, bool retry, int nextAttempt, int seconds)
    {
        var decision = new MessagingOptions().BuildRetryPolicy().Decide(failedAttempt);

        Assert.Equal(retry, decision.Retry);
        Assert.Equal(nextAttempt, decision.NextAttempt);
        Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Delay);
    }

    [Fact]
    public void A_negative_attempt_counts_as_the_first_delivery()
    {
        Assert.Equal(1, new MessagingOptions().BuildRetryPolicy().Decide(-4).NextAttempt);
    }

    [Fact]
    public void Delays_are_configurable_and_replace_the_defaults()
    {
        var policy = new MessagingOptions { RetryDelays = ["00:00:00.5", "00:00:01"] }.BuildRetryPolicy();

        Assert.Equal(2, policy.MaxRetries);
        Assert.False(policy.Decide(2).Retry);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Decide(1).Delay);
    }

    [Theory]
    [InlineData(500, "500ms")]
    [InlineData(5_000, "5s")]
    [InlineData(30_000, "30s")]
    [InlineData(90_000, "90s")]
    [InlineData(120_000, "2m")]
    public void Delay_queue_labels_are_compact_and_distinct(int milliseconds, string label)
    {
        Assert.Equal(label, RetryPolicy.Label(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Queue_and_routing_key_names_follow_the_topology()
    {
        Assert.Equal("desk.email-ack", MessagingNames.QueueFor("email-ack"));
        Assert.Equal("desk.email-ack.retry.30s", MessagingNames.DelayQueueFor("email-ack", TimeSpan.FromSeconds(30)));
        Assert.Equal("email-ack.retry.2m", MessagingNames.DelayRoutingKeyFor("email-ack", TimeSpan.FromMinutes(2)));
    }
}

public class FailureClassificationTests
{
    [Fact]
    public void An_unprocessable_message_or_a_bad_payload_is_permanent()
    {
        Assert.True(FailureClassifier.IsPermanent(new UnprocessableMessageException("ticket not found")));
        Assert.True(FailureClassifier.IsPermanent(new JsonException("bad payload")));
    }

    [Fact]
    public void Everything_else_is_retried()
    {
        Assert.False(FailureClassifier.IsPermanent(new InvalidOperationException("SMTP down")));
        Assert.False(FailureClassifier.IsPermanent(new SimulatedFailureException("EmailAckConsumer")));
        Assert.False(FailureClassifier.IsPermanent(new TimeoutException()));
    }

    [Theory]
    [InlineData("EmailAckConsumer", true)]
    [InlineData("emailackconsumer", true)]
    [InlineData("email-ack", true)]
    [InlineData("KbDraftConsumer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Fail_consumer_matches_the_class_name_or_the_queue_name(string? configured, bool fails)
    {
        var definition = ConsumerDefinition.For(typeof(EmailAckConsumer));

        Assert.Equal(fails, new MessagingOptions { FailConsumer = configured }.ShouldFail(definition));
    }
}

public class ConsumerDeclarationTests
{
    [Fact]
    public void A_consumer_is_declared_by_its_attribute()
    {
        var definition = ConsumerDefinition.For(typeof(EmailAckConsumer));

        Assert.Equal("EmailAckConsumer", definition.Name);
        Assert.Equal("desk.email-ack", definition.Queue);
        Assert.Equal([EventTypes.TicketCreated], definition.RoutingKeys);
        Assert.Equal(ConsumerHost.Worker, definition.Host);
    }

    [Fact]
    public void Scanning_finds_the_consumers_of_the_assembly()
    {
        var found = ConsumerDefinition.Scan(typeof(EmailAckConsumer).Assembly).Select(d => d.Name);

        Assert.Contains("EmailAckConsumer", found);
    }
}

public class EmailTemplateTests
{
    [Fact]
    public void The_acknowledgement_is_in_french_with_text_and_html()
    {
        var mail = EmailTemplates.TicketAcknowledgement("Camille", 42, "VPN en panne", "http://localhost:8080/tickets/abc");

        Assert.Equal("[#42] Votre demande a bien été reçue", mail.Subject);
        Assert.Contains("Bonjour Camille", mail.Text);
        Assert.Contains("« VPN en panne »", mail.Text);
        Assert.Contains("http://localhost:8080/tickets/abc", mail.Text);
        Assert.Contains("<a href=\"http://localhost:8080/tickets/abc\">", mail.Html);
    }

    [Fact]
    public void User_values_are_html_encoded_and_kept_out_of_the_subject()
    {
        var mail = EmailTemplates.TicketAcknowledgement("<b>Eve</b>", 1, "<script>alert(1)</script>\r\nBcc: x@y.z", "http://x/\"onclick=\"1");

        Assert.DoesNotContain("<script>", mail.Html);
        Assert.DoesNotContain("<b>Eve</b>", mail.Html);
        Assert.Contains("&lt;script&gt;", mail.Html);
        Assert.DoesNotContain("\"onclick=\"", mail.Html);
        Assert.DoesNotContain("script", mail.Subject);
        Assert.DoesNotContain('\n', mail.Subject);
    }
}
