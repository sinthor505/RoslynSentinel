using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// NonParallelizable: AgentOperatingModeOptions is static and these tests mutate the ROSLYNSENTINEL_OPERATING_MODE process-wide env var, so they must not interleave.
[NonParallelizable]
public class AgentOperatingModeOptionsTests
{
    private const string EnvVar = "ROSLYNSENTINEL_OPERATING_MODE";

    [SetUp]
    public void ResetAgentOperatingMode()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());
    }

    [TearDown]
    public void ResetAgentOperatingModeAfterTest()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());
    }

    [Test]
    public void Configure_ArgBeatsEnvVar()
    {
        Environment.SetEnvironmentVariable(EnvVar, "multi-agent");

        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });

        Assert.That(AgentOperatingModeOptions.Configured, Is.EqualTo(AgentOperatingMode.SingleAgent));
    }

    [Test]
    public void Configure_EnvVarUsedWhenNoArg()
    {
        Environment.SetEnvironmentVariable(EnvVar, "multi-agent");

        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());

        Assert.That(AgentOperatingModeOptions.Configured, Is.EqualTo(AgentOperatingMode.MultiAgent));
    }

    [TestCase(" Single-Agent ", AgentOperatingMode.SingleAgent)]
    [TestCase("MULTI-AGENT", AgentOperatingMode.MultiAgent)]
    [TestCase("auto", AgentOperatingMode.Auto)]
    [TestCase(null, AgentOperatingMode.Auto)]
    [TestCase("", AgentOperatingMode.Auto)]
    [TestCase("nonsense", AgentOperatingMode.Auto)]
    public void ParseMode_MapsRawValues(string? raw, AgentOperatingMode expected)
    {
        Assert.That(AgentOperatingModeOptions.ParseMode(raw), Is.EqualTo(expected));
    }

    [Test]
    public void ExplicitSingleAgent_NeverChanges()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });

        Assert.That(AgentOperatingModeOptions.ResolveOnce(true), Is.EqualTo(AgentOperatingMode.SingleAgent));
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.SingleAgent));
        Assert.That(AgentOperatingModeOptions.Source, Is.EqualTo("startup arg or env var"));
    }

    [Test]
    public void ExplicitMultiAgent_NeverChanges()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=multi-agent" });

        Assert.That(AgentOperatingModeOptions.ResolveOnce(false), Is.EqualTo(AgentOperatingMode.MultiAgent));
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.MultiAgent));
        Assert.That(AgentOperatingModeOptions.Source, Is.EqualTo("startup arg or env var"));
    }

    [Test]
    public void Auto_BeforeAnyCall_IsAutoWithNoToolCallYet()
    {
        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.Auto));
        Assert.That(AgentOperatingModeOptions.Source, Is.EqualTo("auto: no tool call yet"));
    }

    [Test]
    public void Auto_LatchesMultiAgentOnFirstCall()
    {
        Assert.That(AgentOperatingModeOptions.ResolveOnce(true), Is.EqualTo(AgentOperatingMode.MultiAgent));
        Assert.That(AgentOperatingModeOptions.ResolveOnce(false), Is.EqualTo(AgentOperatingMode.MultiAgent));
        Assert.That(AgentOperatingModeOptions.Source, Is.EqualTo("auto: first call carried an agent id"));
    }

    [Test]
    public void Auto_LatchesSingleAgentOnFirstCall()
    {
        Assert.That(AgentOperatingModeOptions.ResolveOnce(false), Is.EqualTo(AgentOperatingMode.SingleAgent));
        Assert.That(AgentOperatingModeOptions.ResolveOnce(true), Is.EqualTo(AgentOperatingMode.SingleAgent));
        Assert.That(AgentOperatingModeOptions.Source, Is.EqualTo("auto: first call had no agent id"));
    }

    [Test]
    public async Task Auto_ConcurrentFirstCalls_AllAgreeWithEffective()
    {
        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => AgentOperatingModeOptions.ResolveOnce(i % 2 == 0)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var expected = AgentOperatingModeOptions.Effective;
        Assert.That(expected, Is.Not.EqualTo(AgentOperatingMode.Auto));
        Assert.That(results, Has.All.EqualTo(expected));
    }

    [Test]
    public void Configure_ResetsTheLatch()
    {
        AgentOperatingModeOptions.ResolveOnce(true);

        AgentOperatingModeOptions.Configure(System.Array.Empty<string>());

        Assert.That(AgentOperatingModeOptions.Effective, Is.EqualTo(AgentOperatingMode.Auto));
    }
}
