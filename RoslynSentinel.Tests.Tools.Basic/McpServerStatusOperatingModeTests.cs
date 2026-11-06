using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
// AgentOperatingModeOptions and ToolCallContext are process-wide statics, so these tests must not run in parallel.
[NonParallelizable]
public class McpServerStatusOperatingModeTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>.Instance);
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        _manager.Dispose();
    }

    private McpServerStatusResult Call()
    {
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface("test", none, none, new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool(
            "test reason", McpServerStatusToolListing.none);
        return result.SuccessData!;
    }

    [TestCase("multi-agent", "MultiAgent")]
    [TestCase("single-agent", "SingleAgent")]
    public void ConfiguredMode_IsReportedWithStartupSource(string modeArg, string expectedMode)
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=" + modeArg });

        var status = Call();

        Assert.That(status.OperatingMode, Is.EqualTo(expectedMode));
        Assert.That(status.OperatingModeSource, Is.EqualTo("startup arg or env var"));
    }

    [Test]
    public void AutoBeforeFirstCall_ReportsAutoAndNoToolCallYet()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());

        var status = Call();

        Assert.That(status.OperatingMode, Is.EqualTo("Auto"));
        Assert.That(status.OperatingModeSource, Is.EqualTo("auto: no tool call yet"));
    }

    [Test]
    public void AutoLatchedWithAgentId_ReportsMultiAgent()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        AgentOperatingModeOptions.ResolveOnce(hasAgentId: true);

        var status = Call();

        Assert.That(status.OperatingMode, Is.EqualTo("MultiAgent"));
        Assert.That(status.OperatingModeSource, Is.EqualTo("auto: first call carried an agent id"));
    }

    [Test]
    public void AutoLatchedWithoutAgentId_ReportsSingleAgent()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        AgentOperatingModeOptions.ResolveOnce(hasAgentId: false);

        var status = Call();

        Assert.That(status.OperatingMode, Is.EqualTo("SingleAgent"));
        Assert.That(status.OperatingModeSource, Is.EqualTo("auto: first call had no agent id"));
    }

    [Test]
    public void CallerAgentId_IsTheContextAgentId_OrNull()
    {
        ToolCallContext.Set(new ToolCallContext("agent-7", "implementer", "McpServerStatus", "test", 0));

        var withContext = Call();

        Assert.That(withContext.CallerAgentId, Is.EqualTo("agent-7"));

        ToolCallContext.Set(null);

        var withoutContext = Call();

        Assert.That(withoutContext.CallerAgentId, Is.Null);
    }
}
