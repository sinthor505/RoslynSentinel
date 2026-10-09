// AdminTools -> tests for the "Admin"-mode-gated reconciliation tools.
// See docs/current/ideas/external-drift-hard-blocker.md.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("AdminTools")] // sentinel:auto-category
[Category("McpServerControlOperation")] // sentinel:auto-category
[Category("McpServerStopConfirmation")] // sentinel:auto-category
public class AdminToolsTests
{
    private IWorkspaceManager _workspaceManager;
    private AdminTools _tools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new AdminTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void ListExternalDiskChanges_Always_ReturnsList()
    {
        var result = _tools.ListExternalDiskChanges(reason: "test message");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void AcknowledgeExternalFileChanges_Always_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _tools.AcknowledgeExternalFileChanges(reason: "test message"));
    }

    [Test]
    public void AcknowledgeExternalFileChanges_UnknownFile_ReturnsNothingClearedNamingFile()
    {
        var result = _tools.AcknowledgeExternalFileChanges(reason: "test message", files: "nope.cs");
        Assert.That(result, Does.Contain("Nothing cleared"));
        Assert.That(result, Does.Contain("nope.cs"));
    }

    [Test]
    public void AcknowledgeExternalFileChanges_NoFilesArgument_ReportsZeroCleared()
    {
        var result = _tools.AcknowledgeExternalFileChanges(reason: "test message");
        Assert.That(result, Does.StartWith("Cleared 0"));
    }

    [Test]
    public void AcknowledgeExternalFileChanges_MalformedJsonArray_ReturnsParserError()
    {
        var result = _tools.AcknowledgeExternalFileChanges(reason: "test message", files: "[\"a.cs\"");
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Not.StartWith("Cleared"));
    }

    // McpServerControl: the stop path is exercised through AdminTools.ControlServer with an injected
    // exit action, so these tests never call Environment.Exit on the test host.

    [Test]
    public void McpServerControl_GetServerStatus_ReportsRunning()
    {
        var result = _tools.McpServerControl(reason: "test message", operation: AdminTools.McpServerControlOperation.GetServerStatus);
        Assert.That(result, Does.StartWith("Running."));
    }

    [Test]
    public void ControlServer_StopWithoutConfirmation_RefusesAndNamesParameterAndValue()
    {
        var exitScheduled = false;

        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer, confirmServerStop: null, () => exitScheduled = true);

        Assert.That(exitScheduled, Is.False, "an unconfirmed stop must not schedule a process exit");
        Assert.That(result, Does.StartWith("Refused"));
        Assert.That(result, Does.Contain("confirmServerStop"));
        Assert.That(result, Does.Contain("ConfirmServerStop"));
    }

    [Test]
    public void ControlServer_StopWithConfirmation_SchedulesExit()
    {
        var exitScheduled = false;

        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => exitScheduled = true);

        Assert.That(exitScheduled, Is.True);
        Assert.That(result, Does.StartWith("Stopping."));
    }

    [Test]
    public void ControlServer_StopWithLoadedSolution_TellsCallerToRespawnAndReloadThatPath()
    {
        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => { },
            loadedSolutionPath: @"C:\repo\My.slnx");

        Assert.That(result, Does.Contain("next tool call"));
        Assert.That(result, Does.Contain(@"LoadSolution(solutionPath: ""C:\repo\My.slnx"")"));
    }

    [Test]
    public void ControlServer_StopWithNoSolutionLoaded_StillTellsCallerToLoadSolution()
    {
        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => { });

        Assert.That(result, Does.Contain("call LoadSolution with your solution path"));
    }

    [Test]
    public void ControlServer_StatusWithConfirmation_DoesNotScheduleExit()
    {
        var exitScheduled = false;

        AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.GetServerStatus,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => exitScheduled = true);

        Assert.That(exitScheduled, Is.False);
    }

    [Test]
    public void McpServerControl_ConfirmParameter_IsOptionalSingleValueEnum()
    {
        // The guard must be visible in the emitted schema as a one-value enum, not a magic string.
        var parameter = typeof(AdminTools).GetMethod(nameof(AdminTools.McpServerControl))!
            .GetParameters()
            .Single(p => p.Name == "confirmServerStop");

        Assert.That(Nullable.GetUnderlyingType(parameter.ParameterType), Is.EqualTo(typeof(AdminTools.McpServerStopConfirmation)));
        Assert.That(parameter.HasDefaultValue, Is.True);
        Assert.That(Enum.GetNames<AdminTools.McpServerStopConfirmation>(), Is.EqualTo(new[] { "ConfirmServerStop" }));
    }
}
