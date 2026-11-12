using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The unknown-pid miss of TestInstanceLauncher.Stop, reached through McpServerControl(StopTestInstance) (plan_not_found_error_orientation,
/// Step 5e slice 4): the text states the pid asked for, which pids the server does track (here none: a fresh launcher) and the prefilled
/// ListTestInstances option. A pid is an exact number, so no similarity ranking applies. The case with a tracked instance needs a real server
/// process; it is asserted inside the explicit TestInstanceLauncher_EndToEnd_LaunchListToolsAndStop test.
/// </summary>
[TestFixture]
[Category("TestInstanceLauncher")] // sentinel:auto-category
public class TestInstanceStopMissTests
{
    [Test]
    public async Task StopTestInstance_UnknownPidAndNothingTracked_StatesThePidAndTheEmptyFactAsync()
    {
        using var workspace = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tools = new AdminTools(workspace);

        var json = await tools.ControlTestInstancesAsync(
            AdminTools.McpServerControlOperation.StopTestInstance, build: null, mode: null, includeTools: null, pid: 99999991, CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("isError").GetBoolean(), Is.True);
        var error = document.RootElement.GetProperty("errorData");
        Assert.That(error.GetProperty("errorCode").GetString(), Is.EqualTo(ToolErrorCode.NotFound));
        var message = error.GetProperty("message").GetString()!;
        Assert.That(message, Does.Contain("No test instance with pid 99999991 is tracked by this server."));
        Assert.That(message, Does.Contain("No test instance is tracked at all."));
        Assert.That(message, Does.Contain("ListTestInstances"), "the orientation option is kept");
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("tell the user"));
    }
}
