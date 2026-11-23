using System.Security.Claims;
using System.Text.Json;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Security;
using Desk.Infrastructure.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Desk.UnitTests;

public class InvitationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static Invitation Fresh(string email = "pat@acme.test") =>
        new() { Email = email, Role = Role.Agent, CreatedAt = Now, ExpiresAt = Now + InvitationRules.Lifetime };

    [Fact]
    public void Lifetime_IsSevenDays() => Assert.Equal(TimeSpan.FromDays(7), InvitationRules.Lifetime);

    [Fact]
    public void FreshInvitation_WithSameEmail_IsOk() =>
        Assert.Equal(InvitationCheck.Ok, InvitationRules.Check(Fresh(), "pat@acme.test", Now.AddDays(1)));

    [Fact]
    public void EmailComparison_IgnoresCaseAndSpaces() =>
        Assert.Equal(InvitationCheck.Ok, InvitationRules.Check(Fresh("Pat@Acme.test"), " pat@ACME.test ", Now));

    [Fact]
    public void DifferentEmail_IsMismatch() =>
        Assert.Equal(InvitationCheck.EmailMismatch, InvitationRules.Check(Fresh(), "other@acme.test", Now));

    [Fact]
    public void Invitation_IsValidUntilTheExactExpiry()
    {
        var invitation = Fresh();

        Assert.Equal(InvitationCheck.Ok, InvitationRules.Check(invitation, invitation.Email, invitation.ExpiresAt.AddTicks(-1)));
        Assert.Equal(InvitationCheck.Expired, InvitationRules.Check(invitation, invitation.Email, invitation.ExpiresAt));
        Assert.Equal(InvitationCheck.Expired, InvitationRules.Check(invitation, invitation.Email, Now.AddDays(8)));
    }

    [Fact]
    public void AcceptedInvitation_CannotBeUsedAgain()
    {
        var invitation = Fresh();
        invitation.AcceptedAt = Now;

        Assert.Equal(InvitationCheck.AlreadyUsed, InvitationRules.Check(invitation, invitation.Email, Now.AddMinutes(1)));
    }

    [Fact]
    public void AlreadyUsed_TakesPrecedenceOverExpiry()
    {
        var invitation = Fresh();
        invitation.AcceptedAt = Now;

        Assert.Equal(InvitationCheck.AlreadyUsed, InvitationRules.Check(invitation, invitation.Email, Now.AddDays(30)));
    }

    [Fact]
    public void Status_ReflectsAcceptanceAndExpiry()
    {
        var pending = Fresh();
        var expired = Fresh();
        var accepted = Fresh();
        accepted.AcceptedAt = Now;

        Assert.Equal(InvitationStatus.Pending, InvitationRules.StatusOf(pending, Now));
        Assert.Equal(InvitationStatus.Expired, InvitationRules.StatusOf(expired, Now.AddDays(7)));
        Assert.Equal(InvitationStatus.Accepted, InvitationRules.StatusOf(accepted, Now.AddDays(30)));
    }
}

public class RolePolicyTests
{
    [Theory]
    [InlineData(Role.SuperAdmin, Role.TenantAdmin, true)]
    [InlineData(Role.SuperAdmin, Role.Agent, false)]
    [InlineData(Role.SuperAdmin, Role.Requester, false)]
    [InlineData(Role.TenantAdmin, Role.TenantAdmin, true)]
    [InlineData(Role.TenantAdmin, Role.Agent, true)]
    [InlineData(Role.TenantAdmin, Role.Requester, true)]
    [InlineData(Role.TenantAdmin, Role.SuperAdmin, false)]
    [InlineData(Role.Agent, Role.Requester, false)]
    [InlineData(Role.Requester, Role.Requester, false)]
    public void CanInvite(Role actor, Role target, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanInvite(actor, target));

    [Theory]
    [InlineData(Role.TenantAdmin, Role.Agent, true)]
    [InlineData(Role.TenantAdmin, Role.SuperAdmin, false)]
    [InlineData(Role.Agent, Role.Requester, false)]
    [InlineData(Role.SuperAdmin, Role.Agent, false)]
    public void CanAssignRole(Role actor, Role role, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanAssignRole(actor, role));

    [Fact]
    public void TenantAdmin_ManagesOthers_ButNeverHimself_NorASuperAdmin()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.True(RolePolicy.CanManageUser(Role.TenantAdmin, me, other, Role.Agent));
        Assert.False(RolePolicy.CanManageUser(Role.TenantAdmin, me, me, Role.TenantAdmin));
        Assert.False(RolePolicy.CanManageUser(Role.TenantAdmin, me, other, Role.SuperAdmin));
        Assert.False(RolePolicy.CanManageUser(Role.Agent, me, other, Role.Requester));
        Assert.False(RolePolicy.CanManageUser(Role.Requester, me, other, Role.Requester));
    }
}

public class SlaDefaultsTests
{
    [Fact]
    public void DefaultPolicies_FollowTheSpecification()
    {
        var tenantId = Guid.NewGuid();

        var policies = SlaPolicy.CreateDefaults(tenantId).ToDictionary(p => p.Priority);

        Assert.Equal(4, policies.Count);
        Assert.All(policies.Values, p => Assert.Equal(tenantId, p.TenantId));
        Assert.Equal((30, 240), (policies[Priority.Urgent].FirstResponseMinutes, policies[Priority.Urgent].ResolutionMinutes));
        Assert.Equal((120, 480), (policies[Priority.High].FirstResponseMinutes, policies[Priority.High].ResolutionMinutes));
        Assert.Equal((480, 1440), (policies[Priority.Normal].FirstResponseMinutes, policies[Priority.Normal].ResolutionMinutes));
        Assert.Equal((1440, 4320), (policies[Priority.Low].FirstResponseMinutes, policies[Priority.Low].ResolutionMinutes));
    }
}

public class TokenHasherTests
{
    [Fact]
    public void Tokens_AreRandom_UrlSafe_AndLongEnough()
    {
        var a = TokenHasher.NewToken();
        var b = TokenHasher.NewToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.DoesNotContain(a, c => c is '+' or '/' or '=');
    }

    [Fact]
    public void Hash_IsDeterministic_Hex_AndNotTheToken()
    {
        var token = TokenHasher.NewToken();

        Assert.Equal(TokenHasher.Hash(token), TokenHasher.Hash(token));
        Assert.Equal(64, TokenHasher.Hash(token).Length);
        Assert.NotEqual(token, TokenHasher.Hash(token));
        Assert.NotEqual(TokenHasher.Hash(token), TokenHasher.Hash(token + "x"));
    }
}

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new() { Key = "unit-test-signing-key-unit-test-signing-key" };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Token_CarriesTheExpectedClaims_AndExpiresInFifteenMinutes()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var user = new User { TenantId = Guid.NewGuid(), Email = "pat@acme.test", DisplayName = "Pat", Role = Role.Agent };

        var token = new JwtTokenService(Options, new FixedClock(now)).Create(user);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = JwtTokenService.SigningKey(Options),
            ValidateLifetime = false,
        });

        Assert.True(result.IsValid);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal(900, token.ExpiresIn);
        Assert.Equal(now.AddMinutes(15).UtcDateTime, jwt.ValidTo);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(user.TenantId.ToString(), jwt.GetClaim("tid").Value);
        Assert.Equal("Agent", jwt.GetClaim("role").Value);
        Assert.Equal("pat@acme.test", jwt.GetClaim("email").Value);
        Assert.Equal("Pat", jwt.GetClaim("name").Value);
    }

    [Fact]
    public void SuperAdminToken_HasNoTenantClaim()
    {
        var user = new User { Email = "root@platform.test", DisplayName = "Root", Role = Role.SuperAdmin };

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(new JwtTokenService(Options, TimeProvider.System).Create(user).Token);

        Assert.False(jwt.TryGetClaim("tid", out _));
        Assert.Equal("SuperAdmin", jwt.GetClaim("role").Value);
    }

    [Fact]
    public async Task Token_SignedWithAnotherKey_IsRejected()
    {
        var other = new JwtOptions { Key = "another-signing-key-another-signing-key-1" };
        var token = new JwtTokenService(other, TimeProvider.System).Create(new User { Email = "a@b.test", DisplayName = "A", Role = Role.Agent });

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, JwtTokenService.ValidationParameters(Options));

        Assert.False(result.IsValid);
    }
}

public class ExternalIdentityTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void VerifiedEmailClaim_IsRequiredToTrustTheAddress()
    {
        var verified = ExternalIdentityMapper.FromPrincipal("google", Principal(
            new(ClaimTypes.NameIdentifier, "42"), new(ClaimTypes.Email, "a@b.test"), new("email_verified", "true"), new(ClaimTypes.Name, "A")));
        var unverified = ExternalIdentityMapper.FromPrincipal("google", Principal(
            new(ClaimTypes.NameIdentifier, "42"), new(ClaimTypes.Email, "a@b.test"), new("email_verified", "false")));
        var absent = ExternalIdentityMapper.FromPrincipal("github", Principal(
            new(ClaimTypes.NameIdentifier, "42"), new(ClaimTypes.Email, "a@b.test")));

        Assert.True(verified!.EmailVerified);
        Assert.Equal("a@b.test", verified.Email);
        Assert.Equal("42", verified.ProviderKey);
        Assert.False(unverified!.EmailVerified);
        Assert.False(absent!.EmailVerified);
    }

    [Fact]
    public void PrincipalWithoutSubject_IsRejected() =>
        Assert.Null(ExternalIdentityMapper.FromPrincipal("google", Principal(new Claim(ClaimTypes.Email, "a@b.test"))));

    [Fact]
    public void GitHub_PicksTheVerifiedPrimaryAddress()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"email":"old@x.test","primary":false,"verified":true},
              {"email":"main@x.test","primary":true,"verified":true},
              {"email":"noreply@users.noreply.github.com","primary":false,"verified":true}
            ]
            """);

        Assert.Equal("main@x.test", GitHubEmails.PickPrimaryVerified(doc.RootElement));
    }

    [Fact]
    public void GitHub_IgnoresAnUnverifiedPrimary_OrAnUnexpectedPayload()
    {
        using var unverified = JsonDocument.Parse("""[{"email":"main@x.test","primary":true,"verified":false},{"email":"b@x.test","primary":false,"verified":true}]""");
        using var error = JsonDocument.Parse("""{"message":"Bad credentials"}""");

        Assert.Null(GitHubEmails.PickPrimaryVerified(unverified.RootElement));
        Assert.Null(GitHubEmails.PickPrimaryVerified(error.RootElement));
    }

    [Theory]
    [InlineData("google", "Google")]
    [InlineData("github", "GitHub")]
    public void ProviderSlugs_MapToSchemes(string provider, string scheme)
    {
        Assert.Equal(scheme, ExternalProviders.SchemeFor(provider));
        Assert.Equal(provider, ExternalProviders.NameFor(scheme));
    }

    [Fact]
    public void UnknownProvider_HasNoScheme() => Assert.Null(ExternalProviders.SchemeFor("facebook"));
}

public class JwtKeyPolicyTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890")]
    public void TooShortKey_IsRefused_Everywhere(string key)
    {
        Assert.Throws<InvalidOperationException>(() => JwtKeyPolicy.Validate(key, isProduction: true));
        Assert.Throws<InvalidOperationException>(() => JwtKeyPolicy.Validate(key, isProduction: false));
    }

    [Theory]
    [InlineData("change-me-32-chars-minimum-xxxxxxxxx")]
    [InlineData("CHANGE-ME-with-a-long-enough-suffix-1234")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("abababababababababababababababababababab")]
    public void PlaceholderOrFakeKey_IsRefused_InProduction(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyPolicy.Validate(key, isProduction: true));
        Assert.Contains("Jwt:Key", ex.Message);
    }

    [Fact]
    public void RandomKey_IsAccepted_InProduction() =>
        JwtKeyPolicy.Validate(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)), isProduction: true);

    [Fact]
    public void PlaceholderKey_RemainsUsable_OutsideProduction() =>
        JwtKeyPolicy.Validate("change-me-32-chars-minimum-xxxxxxxxx", isProduction: false);
}
