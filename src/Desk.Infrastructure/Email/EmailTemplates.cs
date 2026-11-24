using System.Net;
using Desk.Core.Domain;

namespace Desk.Infrastructure.Email;

public sealed record EmailContent(string Subject, string Text, string Html);

/// <summary>
/// French transactional e-mails: plain text plus a sober HTML twin (no colours, no images). Every value that comes
/// from a user (title, name) is HTML-encoded in the HTML part.
/// </summary>
public static class EmailTemplates
{
    public static EmailContent TicketAcknowledgement(string recipientName, int number, string title, string ticketUrl) =>
        Build(
            $"[#{number}] Votre demande a bien été reçue",
            recipientName,
            [$"Nous avons bien reçu votre demande « {title} » (ticket n° {number})."],
            "Un agent la prendra en charge dès que possible. Vous serez prévenu(e) par e-mail à chaque réponse.",
            ticketUrl);

    /// <summary>To the new assignee. The subject carries the ticket number only (no user-supplied text).</summary>
    public static EmailContent TicketAssigned(string recipientName, int number, string title, string ticketUrl) =>
        Build(
            $"[#{number}] Un ticket vous a été assigné",
            recipientName,
            [$"Le ticket n° {number} « {title} » vous a été assigné."],
            "Consultez-le pour le prendre en charge.",
            ticketUrl);

    /// <summary>To the requester when an agent answered publicly.</summary>
    public static EmailContent ReplyToRequester(string recipientName, int number, string title, string ticketUrl) =>
        Build(
            $"[#{number}] Nouvelle réponse à votre demande",
            recipientName,
            [$"Un agent a répondu à votre demande « {title} » (ticket n° {number})."],
            "Ouvrez le ticket pour lire la réponse et y répondre.",
            ticketUrl);

    /// <summary>To the assignee when the requester answered.</summary>
    public static EmailContent ReplyToAssignee(string recipientName, int number, string title, string ticketUrl) =>
        Build(
            $"[#{number}] Le demandeur a répondu",
            recipientName,
            [$"Le demandeur a répondu sur le ticket n° {number} « {title} »."],
            "Ouvrez le ticket pour lire sa réponse.",
            ticketUrl);

    /// <summary>To a TenantAdmin when a ticket was escalated after a missed SLA deadline.</summary>
    public static EmailContent TicketEscalated(
        string recipientName, int number, string title, string kind, Priority from, Priority to, string ticketUrl) =>
        Build(
            $"[#{number}] Ticket escaladé : échéance SLA dépassée",
            recipientName,
            [
                $"Le ticket n° {number} « {title} » a dépassé son échéance de {(kind == "response" ? "première réponse" : "résolution")}.",
                from == to
                    ? $"Sa priorité est déjà {Label(to)} : elle reste inchangée."
                    : $"Sa priorité passe de {Label(from)} à {Label(to)}.",
            ],
            "Merci de vérifier sa prise en charge.",
            ticketUrl);

    private static string Label(Priority priority) => priority switch
    {
        Priority.Low => "Basse",
        Priority.Normal => "Normale",
        Priority.High => "Haute",
        _ => "Urgente",
    };

    private static EmailContent Build(string subject, string recipientName, string[] lines, string closing, string url)
    {
        var text = $"Bonjour {recipientName},\n\n{string.Join("\n", lines)}\n\n{closing}\n\nVoir le ticket : {url}\n\nDeskFlow\n";

        string Html(string value) => WebUtility.HtmlEncode(value);
        var paragraphs = string.Concat(lines.Select(l => $"<p>{Html(l)}</p>"));
        var html =
            "<!doctype html><html lang=\"fr\"><body style=\"font-family: sans-serif;\">" +
            $"<p>Bonjour {Html(recipientName)},</p>{paragraphs}<p>{Html(closing)}</p>" +
            $"<p><a href=\"{Html(url)}\">Voir le ticket</a></p><p>DeskFlow</p></body></html>";
        return new EmailContent(subject, text, html);
    }
}
