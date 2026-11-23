using System.Text;
using Desk.Core.Domain;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Desk.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const int AccessTokenMinutes = 15;

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "deskflow";
    public string Audience { get; set; } = "deskflow-web";
}

public sealed record AccessToken(string Token, int ExpiresIn);

/// <summary>Issues the 15 minute access token. Claims: sub, tid (absent for SuperAdmin), role, email, name.</summary>
public sealed class JwtTokenService(JwtOptions options, TimeProvider clock)
{
    public AccessToken Create(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["role"] = user.Role.ToString(),
            ["email"] = user.Email,
            ["name"] = user.DisplayName,
        };
        if (user.TenantId is { } tid)
        {
            claims["tid"] = tid.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(JwtOptions.AccessTokenMinutes),
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256),
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), JwtOptions.AccessTokenMinutes * 60);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.Key));

    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = SigningKey(options),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = "name",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
