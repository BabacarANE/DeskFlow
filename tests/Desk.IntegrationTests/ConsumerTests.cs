using Desk.Core.Domain;
using Desk.Core.Events;
using Desk.Infrastructure.Messaging;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>
/// The e-mail and knowledge-base consumers of the Worker, on real PostgreSQL and RabbitMQ: effect, tenant isolation
/// (a message whose tenant does not own the ticket goes to the DLQ without effect), idempotence, retry then DLQ.
/// </summary>
public class ConsumerTests(PostgresFixture postgres, RabbitMqFixture rabbit) : ConsumerTestBase(postgres, rabbit)
{
    // ---- AssignmentNotifierConsumer ----

    [Fact]
    public async Task Assignment_sends_one_email_to_the_assignee_with_encoded_values_and_a_subject_without_user_data()
    {
        var c = await SeedCompanyAsync("assign");
        var ticket = await AddTicketAsync(c, t => t.Title = "Panne <script>alert(1)</script>");
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Assigned(c.TenantId, ticket, c.Agent, c.Admin);
        await PublishAsync(message);

        await Poll.UntilAsync(() => worker.Email.Sent.Count >= 1, "assignment e-mail");
        var mail = Assert.Single(worker.Email.Sent);
        Assert.Equal(c.Agent.Email, mail.To);
        Assert.Equal($"[#{ticket.Number}] Un ticket vous a été assigné", mail.Subject);
        Assert.DoesNotContain("script", mail.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Panne <script>alert(1)</script>", mail.Body);
        Assert.Contains("Panne &lt;script&gt;alert(1)&lt;/script&gt;", mail.Html);
        Assert.DoesNotContain("<script>", mail.Html);
        Assert.Contains($"http://deskflow.test/tickets/{ticket.Id}", mail.Body);
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));
    }

    [Fact]
    public async Task Assigning_to_oneself_sends_nothing()
    {
        var c = await SeedCompanyAsync("selfassign");
        var ticket = await AddTicketAsync(c);
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Assigned(c.TenantId, ticket, c.Agent, actor: c.Agent);
        await PublishAsync(message);

        await WaitProcessedAsync("AssignmentNotifierConsumer", message.MessageId);
        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task Assignment_message_of_another_tenant_or_for_an_unknown_ticket_goes_to_the_dlq_without_effect()
    {
        var a = await SeedCompanyAsync("assign-a");
        var b = await SeedCompanyAsync("assign-b");
        var ticketOfA = await AddTicketAsync(a);
        await using var worker = await StartWorkerAsync(NoWatcher);

        await PublishAsync(Assigned(b.TenantId, ticketOfA, a.Agent, a.Admin)); // tenant B does not own the ticket
        await PublishAsync(Assigned(a.TenantId, new Ticket { Number = 9 }, a.Agent, a.Admin)); // no such ticket
        await WaitForDlqAsync(2);

        Assert.Empty(worker.Email.Sent);
        Assert.Equal(0, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
        Assert.All(await Broker.PeekAsync(MessagingNames.DlqQueue), m => Assert.Equal("poison", m.Header("x-failure-kind")));
    }

    [Fact]
    public async Task Assignment_is_idempotent_the_same_message_twice_sends_one_email()
    {
        var c = await SeedCompanyAsync("assign-idem");
        var ticket = await AddTicketAsync(c);
        await using var worker = await StartWorkerAsync(NoWatcher);
        var message = Assigned(c.TenantId, ticket, c.Agent, c.Admin);

        await PublishAsync(message);
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "first e-mail");
        await PublishAsync(message);
        await WaitDuplicatesAsync(worker, 1);

        Assert.Single(worker.Email.Sent);
        Assert.Equal(1, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages.Where(p => p.Consumer == "AssignmentNotifierConsumer")));
    }

    [Fact]
    public async Task A_failing_notifier_is_retried_then_dead_lettered_and_sends_nothing()
    {
        var c = await SeedCompanyAsync("assign-fail");
        var ticket = await AddTicketAsync(c);
        await using var worker = await StartWorkerAsync(Failing("AssignmentNotifierConsumer"));

        var message = Assigned(c.TenantId, ticket, c.Agent, c.Admin);
        await PublishAsync(message);
        await WaitForDlqAsync(1);

        var dead = Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue));
        Assert.Equal("AssignmentNotifierConsumer", dead.Header("x-failed-consumer"));
        Assert.Equal("retries-exhausted", dead.Header("x-failure-kind"));
        Assert.Equal("3", dead.Header("attempt"));
        Assert.Equal("ticket.assigned", dead.Header("x-original-routing-key"));
        Assert.Empty(worker.Email.Sent);
        Assert.Equal(0, await CountAsync<ProcessedMessage>(db => db.ProcessedMessages));
    }

    // ---- CommentNotifierConsumer ----

    [Fact]
    public async Task A_reply_of_the_requester_mails_the_assignee_only_and_never_carries_the_comment_text()
    {
        var c = await SeedCompanyAsync("comment-req");
        var ticket = await AddTicketAsync(c, t => t.AssigneeId = c.Agent.Id);
        var comment = await AddCommentAsync(c, ticket, c.Requester, Body);
        await using var worker = await StartWorkerAsync(NoWatcher);

        await PublishAsync(Commented(c.TenantId, ticket, comment));

        await Poll.UntilAsync(() => worker.Email.Sent.Count >= 1, "reply e-mail");
        await Task.Delay(500);
        var mail = Assert.Single(worker.Email.Sent);
        Assert.Equal(c.Agent.Email, mail.To);
        Assert.Equal($"[#{ticket.Number}] Le demandeur a répondu", mail.Subject);
        Assert.DoesNotContain(Body, mail.Body);
        Assert.DoesNotContain(Body, mail.Html!);
    }

    [Fact]
    public async Task A_public_answer_of_an_agent_mails_the_requester_never_the_author()
    {
        var c = await SeedCompanyAsync("comment-agent");
        var ticket = await AddTicketAsync(c, t => t.AssigneeId = c.Agent.Id);
        var comment = await AddCommentAsync(c, ticket, c.Agent, Body); // the author is the assignee himself
        await using var worker = await StartWorkerAsync(NoWatcher);

        await PublishAsync(Commented(c.TenantId, ticket, comment));

        await Poll.UntilAsync(() => worker.Email.Sent.Count >= 1, "reply e-mail");
        await Task.Delay(500);
        var mail = Assert.Single(worker.Email.Sent);
        Assert.Equal(c.Requester.Email, mail.To);
        Assert.Equal($"[#{ticket.Number}] Nouvelle réponse à votre demande", mail.Subject);
        Assert.DoesNotContain(Body, mail.Body);
    }

    [Fact]
    public async Task An_internal_note_and_a_requester_reply_on_an_unassigned_ticket_send_nothing()
    {
        var c = await SeedCompanyAsync("comment-none");
        var assigned = await AddTicketAsync(c, t => t.AssigneeId = c.Agent.Id);
        var unassigned = await AddTicketAsync(c);
        var note = await AddCommentAsync(c, assigned, c.Agent, Body, isInternal: true);
        var reply = await AddCommentAsync(c, unassigned, c.Requester, Body);
        await using var worker = await StartWorkerAsync(NoWatcher);

        var m1 = Commented(c.TenantId, assigned, note);
        var m2 = Commented(c.TenantId, unassigned, reply);
        await PublishAsync(m1);
        await PublishAsync(m2);
        await WaitProcessedAsync("CommentNotifierConsumer", m1.MessageId);
        await WaitProcessedAsync("CommentNotifierConsumer", m2.MessageId);

        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task Comment_message_of_another_tenant_or_with_a_foreign_comment_goes_to_the_dlq_without_effect()
    {
        var a = await SeedCompanyAsync("comment-a");
        var b = await SeedCompanyAsync("comment-b");
        var ticketOfA = await AddTicketAsync(a, t => t.AssigneeId = a.Agent.Id);
        var otherTicket = await AddTicketAsync(a, t => t.AssigneeId = a.Agent.Id);
        var comment = await AddCommentAsync(a, ticketOfA, a.Requester, Body);
        await using var worker = await StartWorkerAsync(NoWatcher);

        await PublishAsync(Commented(b.TenantId, ticketOfA, comment)); // wrong tenant
        await PublishAsync(Commented(a.TenantId, otherTicket, comment)); // the comment belongs to another ticket
        await WaitForDlqAsync(2);

        Assert.Empty(worker.Email.Sent);
    }

    [Fact]
    public async Task Comment_notification_is_idempotent()
    {
        var c = await SeedCompanyAsync("comment-idem");
        var ticket = await AddTicketAsync(c, t => t.AssigneeId = c.Agent.Id);
        var comment = await AddCommentAsync(c, ticket, c.Requester, Body);
        await using var worker = await StartWorkerAsync(NoWatcher);
        var message = Commented(c.TenantId, ticket, comment);

        await PublishAsync(message);
        await Poll.UntilAsync(() => worker.Email.Sent.Count == 1, "first e-mail");
        await PublishAsync(message);
        await WaitDuplicatesAsync(worker, 1);

        Assert.Single(worker.Email.Sent);
    }

    // ---- KbDraftConsumer ----

    [Fact]
    public async Task Resolving_creates_one_draft_filled_from_the_ticket_and_its_last_public_comment()
    {
        var c = await SeedCompanyAsync("kb");
        var ticket = await AddTicketAsync(c, t =>
        {
            t.Title = "Imprimante hors ligne";
            t.Description = "L'imprimante du 2e étage ne répond plus.";
            t.AssigneeId = c.Agent.Id;
            t.Status = TicketStatus.Resolved;
        });
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        await AddCommentAsync(c, ticket, c.Agent, "Première piste", at: t0);
        await AddCommentAsync(c, ticket, c.Agent, "Solution : redémarrer le spouleur.", at: t0.AddHours(1));
        await AddCommentAsync(c, ticket, c.Agent, "Note interne récente", isInternal: true, at: t0.AddHours(2));
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Resolved(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("KbDraftConsumer", message.MessageId);

        var article = Assert.Single(await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().ToListAsync()));
        Assert.Equal(c.TenantId, article.TenantId);
        Assert.Equal(ArticleStatus.Draft, article.Status);
        Assert.Equal(ticket.Id, article.SourceTicketId);
        Assert.Equal("Imprimante hors ligne", article.Title);
        Assert.Equal("L'imprimante du 2e étage ne répond plus.", article.Symptom);
        Assert.Equal("Solution : redémarrer le spouleur.", article.Solution);
        Assert.Equal("", article.Cause);
        Assert.Equal(c.Agent.Id, article.AuthorId);
        Assert.Null(article.PublishedAt);
    }

    [Fact]
    public async Task A_draft_without_public_comment_has_an_empty_solution_and_the_requester_as_author_when_unassigned()
    {
        var c = await SeedCompanyAsync("kb-empty");
        var ticket = await AddTicketAsync(c, t => t.Status = TicketStatus.Resolved);
        await AddCommentAsync(c, ticket, c.Agent, "Seulement une note", isInternal: true);
        await using var worker = await StartWorkerAsync(NoWatcher);

        var message = Resolved(c.TenantId, ticket);
        await PublishAsync(message);
        await WaitProcessedAsync("KbDraftConsumer", message.MessageId);

        var article = Assert.Single(await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().ToListAsync()));
        Assert.Equal("", article.Solution);
        Assert.Equal(c.Requester.Id, article.AuthorId);
    }

    [Fact]
    public async Task Redelivery_and_a_second_resolution_of_the_same_ticket_never_create_a_second_draft()
    {
        var c = await SeedCompanyAsync("kb-idem");
        var ticket = await AddTicketAsync(c, t => t.Status = TicketStatus.Resolved);
        await using var worker = await StartWorkerAsync(NoWatcher);
        var first = Resolved(c.TenantId, ticket);

        await PublishAsync(first);
        await WaitProcessedAsync("KbDraftConsumer", first.MessageId);
        await PublishAsync(first); // same message: ProcessedMessage
        await WaitDuplicatesAsync(worker, 1);
        var reopened = Resolved(c.TenantId, ticket); // reopened then resolved again: a new message, the article exists
        await PublishAsync(reopened);
        await WaitProcessedAsync("KbDraftConsumer", reopened.MessageId);

        Assert.Equal(1, await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task Two_resolutions_racing_produce_one_draft_and_the_database_refuses_a_duplicate()
    {
        var c = await SeedCompanyAsync("kb-race");
        var ticket = await AddTicketAsync(c, t => t.Status = TicketStatus.Resolved);
        await using var worker = await StartWorkerAsync(NoWatcher);

        var messages = Enumerable.Range(0, 6).Select(_ => Resolved(c.TenantId, ticket)).ToList();
        await Task.WhenAll(messages.Select(m => PublishAsync(m)));
        foreach (var m in messages)
        {
            await WaitProcessedAsync("KbDraftConsumer", m.MessageId, 40); // a loser of the race is retried, then sees the article
        }

        Assert.Equal(1, await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(0u, await Broker.CountAsync(MessagingNames.DlqQueue));

        await using var db = OpenDb(out var scope);
        using var _ = scope;
        db.KnowledgeArticles.Add(new KnowledgeArticle
        {
            TenantId = c.TenantId,
            Title = "Doublon",
            SourceTicketId = ticket.Id,
            AuthorId = c.Agent.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Draft_creation_stays_inside_the_tenant_and_a_foreign_message_goes_to_the_dlq()
    {
        var a = await SeedCompanyAsync("kb-a");
        var b = await SeedCompanyAsync("kb-b");
        var ticketOfA = await AddTicketAsync(a, t => t.Status = TicketStatus.Resolved);
        var ticketOfB = await AddTicketAsync(b, t => t.Status = TicketStatus.Resolved);
        await using var worker = await StartWorkerAsync(NoWatcher);

        var ok = Resolved(b.TenantId, ticketOfB);
        await PublishAsync(Resolved(b.TenantId, ticketOfA)); // B claims A's ticket
        await PublishAsync(ok);
        await WaitForDlqAsync(1);
        await WaitProcessedAsync("KbDraftConsumer", ok.MessageId);

        var article = Assert.Single(await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().ToListAsync()));
        Assert.Equal(b.TenantId, article.TenantId);
        Assert.Equal(ticketOfB.Id, article.SourceTicketId);
    }

    [Fact]
    public async Task A_failing_draft_consumer_is_retried_then_dead_lettered_and_creates_nothing()
    {
        var c = await SeedCompanyAsync("kb-fail");
        var ticket = await AddTicketAsync(c, t => t.Status = TicketStatus.Resolved);
        await using var worker = await StartWorkerAsync(Failing("KbDraftConsumer"));

        await PublishAsync(Resolved(c.TenantId, ticket));
        await WaitForDlqAsync(1);

        Assert.Equal(0, await ReadAsync(db => db.KnowledgeArticles.IgnoreQueryFilters().CountAsync()));
        Assert.Equal("KbDraftConsumer", Assert.Single(await Broker.PeekAsync(MessagingNames.DlqQueue)).Header("x-failed-consumer"));
    }
}
