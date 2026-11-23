using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Email;

/// <summary>Builds invitations (token stored hashed) and mails the sign-in link.</summary>
public sealed class InvitationService(
    IEmailSender email, IConfiguration configuration, TimeProvider clock, ILogger<InvitationService> logger)
{
    public (Invitation Invitation, string RawToken) Build(Guid tenantId, string address, Role role)
    {
        var raw = TokenHasher.NewToken();
        var now = clock.GetUtcNow();
        var invitation = new Invitation
        {
            TenantId = tenantId,
            Email = InvitationRules.NormalizeEmail(address),
            Role = role,
            TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + InvitationRules.Lifetime,
        };
        return (invitation, raw);
    }

    /// <summary>Best effort: the invitation already exists, a failing SMTP server must not lose it.</summary>
    public async Task SendAsync(Invitation invitation, string rawToken, string tenantName, CancellationToken ct = default)
    {
        var baseUrl = (configuration["App:PublicBaseUrl"] ?? "http://localhost:8080").TrimEnd('/');
        var link = $"{baseUrl}/login?invitation={Uri.EscapeDataString(rawToken)}";
        var body =
            $"Bonjour,\n\nVous avez été invité(e) à rejoindre « {tenantName} » sur DeskFlow.\n\n" +
            $"Connectez-vous avec Google ou GitHub (avec l'adresse {invitation.Email}) via ce lien :\n{link}\n\n" +
            $"Ce lien est valable jusqu'au {invitation.ExpiresAt:dd/MM/yyyy} et ne peut servir qu'une fois.\n";
        try
        {
            await email.SendAsync(invitation.Email, $"Invitation à rejoindre {tenantName} sur DeskFlow", body, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Invitation e-mail could not be sent for invitation {InvitationId}", invitation.Id);
        }
    }
}
