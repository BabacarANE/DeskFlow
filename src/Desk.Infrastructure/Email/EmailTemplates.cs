using System.Net;

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
