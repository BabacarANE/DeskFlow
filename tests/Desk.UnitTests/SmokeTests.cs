using Desk.Infrastructure;

namespace Desk.UnitTests;

public class HealthCheckTagsTests
{
    [Fact]
    public void ReadyTag_IsStable() => Assert.Equal("ready", HealthChecksExtensions.ReadyTag);
}
