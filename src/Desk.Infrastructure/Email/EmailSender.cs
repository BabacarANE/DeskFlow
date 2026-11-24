using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Desk.Infrastructure.Email;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public bool UseTls { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "DeskFlow <no-reply@deskflow.local>";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);

    /// <summary>Multipart message: plain text plus HTML. Implementations that cannot send HTML fall back to the text.</summary>
    Task SendAsync(string to, string subject, string textBody, string? htmlBody, CancellationToken ct = default) =>
        SendAsync(to, subject, textBody, ct);
}

/// <summary>SMTP through MailKit: Mailpit in development, any SMTP server in production.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default) =>
        SendAsync(to, subject, textBody, null, ct);

    public async Task SendAsync(string to, string subject, string textBody, string? htmlBody, CancellationToken ct = default)
    {
        var smtp = options.Value;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(smtp.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(smtp.User))
        {
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? "", ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("E-mail sent ({Subject})", subject);
    }
}
