using System.Text;

namespace Desk.Infrastructure.Auth;

/// <summary>Refuses weak or placeholder signing keys, strictly in Production.</summary>
public static class JwtKeyPolicy
{
    public const int MinBytes = 32;
    public const int MinDistinctChars = 8;

    public static void Validate(string key, bool isProduction)
    {
        if (Encoding.UTF8.GetByteCount(key) < MinBytes)
        {
            throw new InvalidOperationException($"Jwt:Key must be at least {MinBytes} bytes long.");
        }

        if (!isProduction)
        {
            return;
        }

        if (key.TrimStart().StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Jwt:Key is a public placeholder (starts with 'change-me'): generate a random secret.");
        }

        if (key.Distinct().Count() < MinDistinctChars)
        {
            throw new InvalidOperationException($"Jwt:Key looks fake (fewer than {MinDistinctChars} distinct characters): generate a random secret.");
        }
    }
}
