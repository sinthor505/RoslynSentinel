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
    public void ExternalFileDrift_Status_ReportsNotHaltedOnFreshManager()
    {
        var result = _tools.ExternalFileDrift(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Status);
        Assert.That(result, Does.StartWith("SessionHalted=False"));
        Assert.That(result, Does.Contain("tracked external changes: 0"));
    }

    [Test]
    public void ExternalFileDrift_List_OnFreshManager_SaysNoChanges()
    {
        var result = _tools.ExternalFileDrift(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.List);
        Assert.That(result, Is.EqualTo("No tracked external file changes."));
    }

    [Test]
    public void ExternalFileDrift_FilesWithStatus_IsRefused()
    {
        var result = _tools.ExternalFileDrift(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Status, files: "a.cs");
        Assert.That(result, Does.Contain("only used with operation: Acknowledge"));
        Assert.That(result, Does.Contain("nothing changed"));
    }

    [Test]
    public void Acknowledge_UnknownFile_ReturnsNothingClearedNamingFile()
    {
        var result = _tools.ExternalFileDrift(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "nope.cs");
        Assert.That(result, Does.Contain("Nothing cleared"));
        Assert.That(result, Does.Contain("nope.cs"));
    }

    [Test]
    public void Acknowledge_MalformedJsonArray_ReturnsParserError()
    {
        var result = _tools.ExternalFileDrift(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "[\"a.cs\"");
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Not.Empty);
        Assert.That(result, Does.Not.StartWith("Cleared"));
    }

    [Test]
    public void Acknowledge_Partial_ClearsLatchAndNamesRemainingFiles()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, "A.cs", AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(1));
        Assert.That(health.Tracked, Is.EqualTo(new[] { "B.cs", "C.cs" }));
        Assert.That(result, Does.Contain("B.cs"));
        Assert.That(result, Does.Contain("C.cs"));
        Assert.That(result, Does.Contain("halt the session again"));
    }

    [Test]
    public void Acknowledge_NoFilesDefaultScope_RefusesNamingBothOptions()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, files: null, AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(0));
        Assert.That(health.Tracked, Has.Count.EqualTo(3));
        Assert.That(result, Does.StartWith("Nothing cleared"));
        Assert.That(result, Does.Contain("files"));
        Assert.That(result, Does.Contain("ConfirmWithListedFiles"));
        Assert.That(result, Does.Contain("ConfirmAll"));
    }

    [Test]
    public void Acknowledge_ConfirmAll_ClearsEverythingAndLatch()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, files: null, AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmAll);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(1));
        Assert.That(health.Tracked, Is.Empty);
        Assert.That(result, Does.StartWith("Cleared 3"));
        Assert.That(result, Does.Contain("Session-halt latch cleared"));
    }

    [Test]
    public void Acknowledge_ConfirmAllWithFiles_IsRefusedAndClearsNothing()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, "A.cs", AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmAll);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(0));
        Assert.That(health.Tracked, Has.Count.EqualTo(3));
        Assert.That(result, Does.StartWith("Nothing cleared"));
        Assert.That(result, Does.Contain("cannot be combined with files"));
    }

    // Stub drift source: the real manager cannot be seeded with drift without a file-watcher round trip.
    private sealed class StubHealth : IWorkspaceHealthReporter
    {
        public StubHealth(params string[] driftPaths)
        {
            Tracked = new List<string>(driftPaths);
        }

        public List<string> Tracked { get; }

        public int ClearSessionHaltCalls { get; private set; }

        public List<string> GetExternalFileChanges() => new(Tracked);

        public void ClearExternalFileChanges() => Tracked.Clear();

        public void ClearExternalFileChanges(IReadOnlyCollection<string> paths) => Tracked.RemoveAll(t => paths.Contains(t));

        public void ClearSessionHalt() => ClearSessionHaltCalls++;

        public bool IsSessionHalted() => ClearSessionHaltCalls == 0;

        public Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();

        public HealthComponents GetHealthComponents() => throw new NotImplementedException();

        public List<string> GetWorkspaceLoadErrors() => throw new NotImplementedException();

        public WorkspaceStatus GetWorkspaceStatus() => throw new NotImplementedException();
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
