using Microsoft.Extensions.Configuration;

namespace Desk.Infrastructure.Messaging.Consumers;

internal static class TicketLinks
{
    /// <summary>Link to a ticket in the web application (<c>App:PublicBaseUrl</c>).</summary>
    public static string Url(IConfiguration configuration, Guid ticketId) =>
        $"{(configuration["App:PublicBaseUrl"] ?? "http://localhost:8080").TrimEnd('/')}/tickets/{ticketId}";
}
