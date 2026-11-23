using Desk.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.IntegrationTests;

public class JwtKeyStartupTests
{
    private static IConfiguration Config(string? key) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = "Host=x",
        ["Jwt:Key"] = key,
    }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("change-me-32-chars-minimum-xxxxxxxxx")]
    [InlineData("short")]
    public void Production_RefusesMissingPlaceholderOrShortKey(string? key) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDeskIdentity(Config(key), isProduction: true));

    [Fact]
    public void Production_AcceptsARealKey() =>
        new ServiceCollection().AddDeskIdentity(Config("k7Qp2xVn9LdR4sTw8YbHc3MfJ6ZaUe1G"), isProduction: true);

    [Fact]
    public void Development_GeneratesAPerProcessKey_WhenNoneIsConfigured() =>
        new ServiceCollection().AddDeskIdentity(Config(null), isProduction: false);
}
