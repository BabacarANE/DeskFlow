using System.Security.Claims;
using System.Text.Json;

namespace Desk.Infrastructure.Auth;

public sealed record ExternalIdentity(
    string Provider,
    string ProviderKey,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    string? AvatarUrl);

public static class ExternalProviders
{
    public const string Google = "google";
    public const string GitHub = "github";

    public const string GoogleScheme = "Google";
    public const string GitHubScheme = "GitHub";
    public const string EmailVerifiedClaim = "email_verified";
    public const string AvatarClaim = "avatar_url";

    public static string? SchemeFor(string provider) => provider switch
    {
        Google => GoogleScheme,
        GitHub => GitHubScheme,
        _ => null,
    };

    public static string? NameFor(string scheme) => scheme switch
    {
        GoogleScheme => Google,
        GitHubScheme => GitHub,
        _ => null,
    };
}

public static class ExternalIdentityMapper
{
    /// <summary>
    /// Reads the claims produced by the OAuth handlers. The e-mail counts as verified only
    /// when the handler (Google <c>email_verified</c>, GitHub primary verified address) said so.
    /// </summary>
    public static ExternalIdentity? FromPrincipal(string provider, ClaimsPrincipal principal)
    {
        var key = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var verified = string.Equals(principal.FindFirstValue(ExternalProviders.EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
        return new ExternalIdentity(
            provider,
            key,
            principal.FindFirstValue(ClaimTypes.Email),
            verified,
            principal.FindFirstValue(ClaimTypes.Name),
            principal.FindFirstValue(ExternalProviders.AvatarClaim));
    }
}

public static class GitHubEmails
{
    /// <summary>Returns the primary address of the <c>/user/emails</c> payload, only if GitHub verified it.</summary>
    public static string? PickPrimaryVerified(JsonElement emails)
    {
        if (emails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in emails.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var primary = item.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
            var verified = item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            if (primary && verified && item.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
        }

        return null;
    }
}
