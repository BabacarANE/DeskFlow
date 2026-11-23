using System.Security.Cryptography;
using System.Text;

namespace Desk.Core.Security;

/// <summary>Opaque random tokens (refresh, invitation) are only ever stored as SHA-256 hashes.</summary>
public static class TokenHasher
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
