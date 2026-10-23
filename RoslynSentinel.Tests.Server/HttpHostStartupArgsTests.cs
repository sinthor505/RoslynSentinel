// Coverage for plan_build_isolation_prod_instances_and_test_servers.md Step 4.1: --port=0, --bind, --respawn
// parsing and the SentinelHostOptions defaults the hosts rely on. No host is started here; the bound-address
// readback is verified by running the built server (see the step's manual check).
using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ServerStartupHelpers")] // sentinel:auto-category
public class HttpHostStartupArgsTests
{
    [TestCase("--port=0", 0)]
    [TestCase("--port=5151", 5151)]
    [TestCase("--port=abc", 5100)]
    public void ParsePort_ParsesValueIncludingZero_AndFallsBackToDefault(string arg, int expected)
    {
        Assert.That(ServerStartupHelpers.ParsePort([arg], defaultPort: 5100), Is.EqualTo(expected));
    }

    [Test]
    public void ParsePort_Absent_ReturnsDefault()
    {
        Assert.That(ServerStartupHelpers.ParsePort([], defaultPort: 5100), Is.EqualTo(5100));
    }

    [TestCase("--bind=any", HttpBindScope.Any)]
    [TestCase("--bind=ANY", HttpBindScope.Any)]
    [TestCase("--bind=loopback", HttpBindScope.Loopback)]
    [TestCase("--bind=Loopback", HttpBindScope.Loopback)]
    [TestCase("--bind=everything", HttpBindScope.Loopback)] // unknown -> the safe default, no throw
    public void ParseBind_ExplicitValue(string arg, HttpBindScope expected)
    {
        Assert.That(ServerStartupHelpers.ParseBind([arg]), Is.EqualTo(expected));
    }

    [Test]
    public void ParseBind_Absent_DefaultsToLoopback()
    {
        Assert.That(ServerStartupHelpers.ParseBind([]), Is.EqualTo(HttpBindScope.Loopback));
    }

    [Test]
    public void ParseBind_AcceptsSpaceSeparatedForm()
    {
        Assert.That(ServerStartupHelpers.ParseBind(["--bind", "any"]), Is.EqualTo(HttpBindScope.Any));
    }

    [TestCase("stdio", RespawnPolicy.Auto)]
    [TestCase("http", RespawnPolicy.None)]
    [TestCase("HTTP", RespawnPolicy.None)]
    public void ParseRespawn_Absent_DefaultFollowsTransport(string transport, RespawnPolicy expected)
    {
        Assert.That(ServerStartupHelpers.ParseRespawn([], transport), Is.EqualTo(expected));
    }

    [TestCase("--respawn=auto", "http", RespawnPolicy.Auto)]
    [TestCase("--respawn=none", "stdio", RespawnPolicy.None)]
    [TestCase("--respawn=NONE", "stdio", RespawnPolicy.None)]
    public void ParseRespawn_ExplicitValueOverridesTransportDefault(string arg, string transport, RespawnPolicy expected)
    {
        Assert.That(ServerStartupHelpers.ParseRespawn([arg], transport), Is.EqualTo(expected));
    }

    [TestCase("stdio", RespawnPolicy.Auto)]
    [TestCase("http", RespawnPolicy.None)]
    public void ParseRespawn_UnknownValue_FallsBackToTransportDefault(string transport, RespawnPolicy expected)
    {
        Assert.That(ServerStartupHelpers.ParseRespawn(["--respawn=sometimes"], transport), Is.EqualTo(expected));
    }

    [Test]
    public void SentinelHostOptions_Defaults_MatchPreStep41Behaviour()
    {
        var options = new SentinelHostOptions();

        Assert.That(options.OperatingMode, Is.EqualTo(OperatingMode.Production));
        Assert.That(options.Transport, Is.EqualTo(HostTransport.Stdio));
        Assert.That(options.Respawn, Is.EqualTo(RespawnPolicy.Auto));
    }

    [Test]
    public void AddRoslynSentinelHostOptions_WithoutOptionalArguments_RegistersStdioAuto()
    {
        var services = new ServiceCollection().AddRoslynSentinelHostOptions(OperatingMode.Production);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<SentinelHostOptions>();

        Assert.That(options.Transport, Is.EqualTo(HostTransport.Stdio));
        Assert.That(options.Respawn, Is.EqualTo(RespawnPolicy.Auto));
    }

    [Test]
    public void AddRoslynSentinelHostOptions_PassesTransportAndRespawnThrough()
    {
        var services = new ServiceCollection().AddRoslynSentinelHostOptions(
            OperatingMode.Testing, HostTransport.Http, RespawnPolicy.None);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<SentinelHostOptions>();

        Assert.That(options.OperatingMode, Is.EqualTo(OperatingMode.Testing));
        Assert.That(options.Transport, Is.EqualTo(HostTransport.Http));
        Assert.That(options.Respawn, Is.EqualTo(RespawnPolicy.None));
    }
}
