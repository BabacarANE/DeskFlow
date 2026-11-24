using System.Collections.Concurrent;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging.Consumers;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests.Support;

/// <summary>One company written directly in the database: two admins (one inactive), two agents, two requesters.</summary>
public sealed record SeededCompany(
    Guid TenantId, string Slug, User Admin, User Admin2, User InactiveAdmin, User Agent, User Agent2, User Requester, User Requester2);

/// <summary>Records what a consumer asked the real-time channel to send (the hub itself is tested separately).</summary>
public sealed class RecordingNotifier : IRealtimeNotifier
{
    public ConcurrentQueue<(string Group, string Method, JsonElement Payload)> Sent { get; } = new();

    public Task SendToGroupAsync<T>(string group, string method, T payload, CancellationToken ct)
    {
        Sent.Enqueue((group, method, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        return Task.CompletedTask;
    }
}

/// <summary>Seeding and envelope helpers of the consumer tests (messages are published straight to the broker).</summary>
public abstract class ConsumerTestBase(PostgresFixture postgres, RabbitMqFixture rabbit) : MessagingTestBase(postgres, rabbit)
{
    protected const string Body = "Texte confidentiel de la réponse";

    protected async Task<SeededCompany> SeedCompanyAsync(string slug)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        var tenant = new Tenant { Name = "Entreprise " + slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
        User NewUser(string name, Role role, bool active = true) => new()
        {
            TenantId = tenant.Id,
            Email = $"{name}@{slug}.test",
            DisplayName = "Nom " + name,
            Role = role,
            IsActive = active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var company = new SeededCompany(tenant.Id, slug, NewUser("admin", Role.TenantAdmin), NewUser("admin2", Role.TenantAdmin),
            NewUser("oldadmin", Role.TenantAdmin, active: false), NewUser("agent", Role.Agent), NewUser("agent2", Role.Agent),
            NewUser("req", Role.Requester), NewUser("req2", Role.Requester));
        db.Tenants.Add(tenant);
        db.Users.AddRange(company.Admin, company.Admin2, company.InactiveAdmin, company.Agent, company.Agent2, company.Requester, company.Requester2);
        db.SlaPolicies.AddRange(SlaPolicy.CreateDefaults(tenant.Id));
        await db.SaveChangesAsync();
        return company;
    }

    private int _numbers;

    protected async Task<Ticket> AddTicketAsync(SeededCompany company, Action<Ticket>? configure = null)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            TenantId = company.TenantId,
            Number = Interlocked.Increment(ref _numbers),
            Title = "VPN en panne",
            Description = "Impossible de me connecter depuis hier.",
            RequesterId = company.Requester.Id,
            Status = TicketStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };
        configure?.Invoke(ticket);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket;
    }

    protected async Task<TicketComment> AddCommentAsync(
        SeededCompany company, Ticket ticket, User author, string body, bool isInternal = false, DateTimeOffset? at = null)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        var comment = new TicketComment
        {
            TenantId = company.TenantId,
            TicketId = ticket.Id,
            AuthorId = author.Id,
            Body = body,
            IsInternal = isInternal,
            CreatedAt = at ?? DateTimeOffset.UtcNow,
        };
        db.TicketComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    protected Task<T> ReadAsync<T>(Func<Desk.Infrastructure.Persistence.DeskDbContext, Task<T>> query) => ReadCoreAsync(query);

    private async Task<T> ReadCoreAsync<T>(Func<Desk.Infrastructure.Persistence.DeskDbContext, Task<T>> query)
    {
        await using var db = OpenDb(out var scope);
        using var _ = scope;
        return await query(db);
    }

    protected static MessageEnvelope Envelope<TPayload>(string type, Guid tenantId, TPayload payload, Guid? messageId = null) =>
        MessageEnvelope.Create(messageId ?? Guid.NewGuid(), type, tenantId, DateTimeOffset.UtcNow, "corr-test", payload);

    protected static MessageEnvelope Assigned(Guid tenantId, Ticket ticket, User assignee, User actor, Guid? messageId = null) =>
        Envelope(EventTypes.TicketAssigned, tenantId,
            new TicketAssignedPayload(ticket.Id, ticket.Number, ticket.Title, assignee.Id, null, actor.Id), messageId);

    protected static MessageEnvelope Commented(Guid tenantId, Ticket ticket, TicketComment comment, Guid? messageId = null) =>
        Envelope(EventTypes.TicketCommented, tenantId,
            new TicketCommentedPayload(ticket.Id, ticket.Number, comment.Id, comment.AuthorId), messageId);

    protected static MessageEnvelope Resolved(Guid tenantId, Ticket ticket, Guid? messageId = null) =>
        Envelope(EventTypes.TicketResolved, tenantId, new TicketResolvedPayload(ticket.Id, ticket.Number, DateTimeOffset.UtcNow), messageId);

    protected static MessageEnvelope Breached(Guid tenantId, Ticket ticket, string kind = "resolution", Guid? messageId = null) =>
        Envelope(EventTypes.TicketSlaBreached, tenantId,
            new TicketSlaBreachedPayload(ticket.Id, ticket.Number, kind, DateTimeOffset.UtcNow.AddHours(-1)), messageId);

    protected static MessageEnvelope Created(Guid tenantId, Ticket ticket, Guid? messageId = null) =>
        Envelope(EventTypes.TicketCreated, tenantId,
            new TicketCreatedPayload(ticket.Id, ticket.Number, ticket.Title, ticket.Priority, ticket.RequesterId), messageId);

    /// <summary>Waits until the consumer recorded the message: it was processed (or recognised as a duplicate before).</summary>
    protected async Task WaitProcessedAsync(string consumer, Guid messageId, int seconds = 20) =>
        await Poll.UntilAsync(async () => await CountAsync<ProcessedMessage>(
            db => db.ProcessedMessages.Where(p => p.MessageId == messageId && p.Consumer == consumer)) == 1,
            $"{consumer} processed {messageId}", seconds);

    protected async Task WaitDuplicatesAsync(TestWorker worker, int count) =>
        await Poll.UntilAsync(() => worker.Logs.Lines.Count(l => l.Contains("Duplicate message", StringComparison.Ordinal)) >= count,
            $"{count} duplicate(s) ignored");

    protected static Dictionary<string, string?> Failing(string consumer) => new() { ["Messaging:FailConsumer"] = consumer, ["Sla:Enabled"] = "false" };

    protected static Dictionary<string, string?> NoWatcher => new() { ["Sla:Enabled"] = "false" };
}
