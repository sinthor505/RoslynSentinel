// AdminTools -> tests for the "Admin"-mode-gated reconciliation tools.
// See docs/current/ideas/closed/idea_external_drift_hard_blocker.md.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

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
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new AdminTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void ExternalFileDrift_Status_ReportsNotHaltedOnFreshManager()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Status);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Does.StartWith("SessionHalted=False"));
        Assert.That(result.SuccessData, Does.Contain("tracked external changes: 0"));
    }

    [Test]
    public void ExternalFileDrift_List_OnFreshManager_SaysNoChanges()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.List);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Is.EqualTo("No tracked external file changes."));
    }

    [Test]
    public void ExternalFileDrift_FilesWithStatus_IsRefused()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Status, files: "a.cs");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("only used with operation: Acknowledge"));
        Assert.That(result.ErrorData!.Message, Does.Contain("nothing changed"));
    }

    [Test]
    public void Acknowledge_UnknownFile_ReturnsNothingClearedNamingFile()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "nope.cs");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Nothing cleared"));
        Assert.That(result.ErrorData!.Message, Does.Contain("nope.cs"));
    }

    [Test]
    public void Acknowledge_MalformedJsonArray_ReportsParseError()
    {
        var result = _tools.ExternalFileDriftTool(reason: "test message", operation: AdminTools.ExternalFileDriftOperation.Acknowledge, files: "[\"a.cs\"");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("looks like a JSON array"));
        Assert.That(result.ErrorData!.Message, Does.Not.StartWith("Cleared"));
    }

    [Test]
    public void Acknowledge_Partial_ClearsNamedFilesAndNamesRemainingFiles()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, "A.cs", AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(1));
        Assert.That(health.Tracked, Is.EqualTo(new[] { "B.cs", "C.cs" }));
        Assert.That(result, Does.Contain("B.cs"));
        Assert.That(result, Does.Contain("C.cs"));
        Assert.That(result, Does.StartWith("Cleared 1: A.cs from the drift list."));
        Assert.That(result, Does.Contain("2 file(s) remain listed"));
        Assert.That(result, Does.Not.Contain("halt the session"));
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
    public void Acknowledge_ConfirmAll_ClearsEverythingFromVisibilityList()
    {
        var health = new StubHealth("A.cs", "B.cs", "C.cs");

        var result = AdminTools.AcknowledgeDrift(health, files: null, AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmAll);

        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(1));
        Assert.That(health.Tracked, Is.Empty);
        Assert.That(result, Is.EqualTo("Cleared 3 tracked external file change(s): A.cs, B.cs, C.cs from the drift list."));
    }

    [Test]
    public void ExternalFileDriftTool_Description_SaysDriftDoesNotHaltAndAcknowledgeOnlyEditsVisibilityList()
    {
        var method = typeof(AdminTools).GetMethod(nameof(AdminTools.ExternalFileDriftTool))!;
        var description = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(method)!.Description;

        Assert.That(description, Does.Contain("StaleRead"));
        Assert.That(description, Does.Contain("Acknowledge = remove files from the visibility list"));
        Assert.That(description, Does.Contain("does not change whether a write is allowed"));
        Assert.That(description, Does.Not.Contain("halts the session"));
        Assert.That(description, Does.Not.Contain("clear the halt"));
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

    [Test]
    public void Acknowledge_PartialWithUnknownName_ClearsTrackedNameAndNamesUnknownOne()
    {
        var health = new StubHealth("A.cs", "B.cs");

        var result = AdminTools.AcknowledgeDriftResult(health, "A.cs,nope.cs", AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles);

        Assert.That(result.IsError, Is.False);
        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(1));
        Assert.That(health.Tracked, Is.EqualTo(new[] { "B.cs" }));
        Assert.That(result.SuccessData, Does.StartWith("Cleared 1: A.cs from the drift list."));
        Assert.That(result.SuccessData, Does.Contain("nope.cs"));
        Assert.That(result.SuccessData, Does.Contain("match no tracked change"));
        Assert.That(result.SuccessData, Does.Not.Contain("Nothing cleared"));
    }

    [Test]
    public void Acknowledge_AllUnknownNames_IsRefusedNotFoundAndClearsNothing()
    {
        var health = new StubHealth("A.cs", "B.cs");

        var result = AdminTools.AcknowledgeDriftResult(health, "nope.cs", AdminTools.ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("Nothing cleared"));
        Assert.That(health.ClearSessionHaltCalls, Is.EqualTo(0));
        Assert.That(health.Tracked, Has.Count.EqualTo(2));
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

        public void RegisterOwnWrites(IReadOnlyCollection<string> paths) => throw new NotImplementedException();

        public void ClearSessionHalt() => ClearSessionHaltCalls++;

        public bool IsSessionHalted() => ClearSessionHaltCalls == 0;

        public Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();

        public HealthComponents GetHealthComponents() => throw new NotImplementedException();

        public List<string> GetWorkspaceLoadErrors() => throw new NotImplementedException();

        public WorkspaceStatus GetWorkspaceStatus() => throw new NotImplementedException();
    }

    // McpServerControlTool: the stop path is exercised through AdminTools.ControlServer with an injected
    // exit action, so these tests never call Environment.Exit on the test host.

    [Test]
    public async Task McpServerControl_GetServerStatus_ReportsRunning()
    {
        var result = await _tools.McpServerControlTool(reason: "test message", operation: AdminTools.McpServerControlOperation.GetServerStatus);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData as string, Does.StartWith("Running."));
    }

    [Test]
    public async Task McpServerControl_ListTestInstances_ReturnsPayloadNestedUnderSuccessData()
    {
        var result = await _tools.McpServerControlTool(reason: "test message", operation: AdminTools.McpServerControlOperation.ListTestInstances);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Is.TypeOf<System.Text.Json.JsonElement>());
        var payload = (System.Text.Json.JsonElement)result.SuccessData!;
        Assert.That(payload.GetProperty("instances").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task McpServerControl_StopTestInstanceUnknownPid_MapsIsErrorJsonToErrorEnvelope()
    {
        var result = await _tools.McpServerControlTool(
            reason: "test message", operation: AdminTools.McpServerControlOperation.StopTestInstance, pid: 99999991);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData!.Message, Does.Contain("ListTestInstances"));
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
        Assert.That(result, Does.Contain("LoadSolution(reason:"));
        Assert.That(result, Does.Contain("solutionPath: "));
        Assert.That(result, Does.Contain("My.slnx"));
    }

    [Test]
    public void ControlServer_StopWithNoSolutionLoaded_StillTellsCallerToLoadSolution()
    {
        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => { });

        Assert.That(result, Does.Contain("no solution loaded: call LoadSolution(reason:"));
        Assert.That(result, Does.Contain("before any other tool."));
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
    public async Task McpServerControlTool_WithoutConfirmation_RefusesRegardlessOfRespawn()
    {
        // The instance path reads the policy from SentinelHostOptions; an unconfirmed stop must still refuse
        // (and so never schedule the real process exit) under respawn none.
        var tools = new AdminTools(_workspaceManager, new SentinelHostOptions { Respawn = RespawnPolicy.None });

        var result = await tools.McpServerControlTool(
            reason: "test message", operation: AdminTools.McpServerControlOperation.StopServer);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConfirmationRequired));
        Assert.That(result.ErrorData!.Message, Does.StartWith("Refused"));
    }

    [Test]
    public void ControlServer_StopWithRespawnAuto_KeepsTheVsCodeRespawnText()
    {
        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => { },
            loadedSolutionPath: @"C:\repo\My.slnx",
            respawn: RespawnPolicy.Auto);

        Assert.That(result, Does.StartWith("Stopping. VS Code will rebuild the server binary and spawn a fresh instance on your next tool call. "));
        Assert.That(result, Does.Contain("LoadSolution(reason:"));
        Assert.That(result, Does.Contain("solutionPath: "));
        Assert.That(result, Does.Contain("My.slnx"));
        Assert.That(result, Does.EndWith("before any other tool."));
        Assert.That(result, Does.Not.Contain("LaunchTestInstance"));
    }

    [Test]
    public void ControlServer_StopWithRespawnNone_SaysInstanceStaysDownAndNamesLaunchTestInstance()
    {
        var exitScheduled = false;

        var result = AdminTools.ControlServer(
            AdminTools.McpServerControlOperation.StopServer,
            AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            () => exitScheduled = true,
            loadedSolutionPath: @"C:\repo\My.slnx",
            respawn: RespawnPolicy.None);

        Assert.That(exitScheduled, Is.True);
        Assert.That(result, Does.StartWith("Stopping."));
        Assert.That(result, Does.Contain("stays down"));
        Assert.That(result, Does.Contain("LaunchTestInstance"));
        Assert.That(result, Does.Not.Contain("VS Code"), "none must not promise a VS Code respawn");
        Assert.That(result, Does.Not.Contain("next tool call"));
    }

    [Test]
    public void McpServerControl_ConfirmParameter_IsOptionalSingleValueEnum()
    {
        // The guard must be visible in the emitted schema as a one-value enum, not a magic string.
        var parameter = typeof(AdminTools).GetMethod(nameof(AdminTools.McpServerControlTool))!
            .GetParameters()
            .Single(p => p.Name == "confirmServerStop");

        Assert.That(Nullable.GetUnderlyingType(parameter.ParameterType), Is.EqualTo(typeof(AdminTools.McpServerStopConfirmation)));
        Assert.That(parameter.HasDefaultValue, Is.True);
        Assert.That(Enum.GetNames<AdminTools.McpServerStopConfirmation>(), Is.EqualTo(new[] { "ConfirmServerStop" }));
    }

    private static string NewStateDir() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private static void DeleteStateDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ToolsetService CreateToolsetService(params ToolSetName[] enable)
    {
        var options = Options.Create(new McpServerOptions { ToolCollection = new McpServerPrimitiveCollection<McpServerTool>() });
        var service = new ToolsetService(
            options,
            new ServiceCollection().BuildServiceProvider(),
            new HashSet<string> { "GitRemoteTools", "DeclarationTools", "ParameterEditTools" },
            () => new[] { typeof(AdminTools).Assembly });
        foreach (var set in enable)
        {
            service.SetEnabled(set, true);
        }

        return service;
    }

    [Test]
    public void StopServer_WithToolsets_AddsToCurrentSets()
    {
        var stateDir = NewStateDir();
        try
        {
            var store = new TabToolsetStateStore(stateDir, sessionId: null);
            var service = CreateToolsetService(ToolSetName.git);

            var line = AdminTools.ApplyStopToolsets(store, service, keepToolsets: true, added: new[] { ToolSetName.declarations });

            Assert.That(store.Load()!.EnabledToolSets, Is.EqualTo(new[] { "git", "declarations" }));
            Assert.That(line, Does.Contain("Toolsets after restart:"));
            Assert.That(line, Does.Contain("git"));
            Assert.That(line, Does.Contain("declarations"));
        }
        finally
        {
            DeleteStateDir(stateDir);
        }
    }

    [Test]
    public void StopServer_Default_SavesCurrentSets()
    {
        var stateDir = NewStateDir();
        try
        {
            var store = new TabToolsetStateStore(stateDir, sessionId: null);
            var service = CreateToolsetService(ToolSetName.git);

            AdminTools.ApplyStopToolsets(store, service, keepToolsets: true, added: Array.Empty<ToolSetName>());

            Assert.That(store.Load()!.EnabledToolSets, Is.EqualTo(new[] { "git" }));
        }
        finally
        {
            DeleteStateDir(stateDir);
        }
    }

    [Test]
    public void StopServer_KeepToolsetsFalse_ClearsState()
    {
        var stateDir = NewStateDir();
        try
        {
            var store = new TabToolsetStateStore(stateDir, sessionId: null);
            store.Save(new[] { ToolSetName.git });
            var service = CreateToolsetService(ToolSetName.declarations);

            var line = AdminTools.ApplyStopToolsets(store, service, keepToolsets: false, added: Array.Empty<ToolSetName>());

            Assert.That(File.Exists(Path.Combine(stateDir, TabToolsetStateStore.FileName)), Is.False);
            Assert.That(store.Load(), Is.Null);
            Assert.That(line, Does.Contain("configured --toolsets"));
        }
        finally
        {
            DeleteStateDir(stateDir);
        }
    }

    [Test]
    public void StopServer_KeepFalseWithToolsets_IsConfiguredPlusList()
    {
        var stateDir = NewStateDir();
        try
        {
            var store = new TabToolsetStateStore(stateDir, sessionId: null)
            {
                ConfiguredToolSets = new[] { ToolSetName.git },
            };
            var service = CreateToolsetService(ToolSetName.projectAdmin);

            AdminTools.ApplyStopToolsets(store, service, keepToolsets: false, added: new[] { ToolSetName.declarations });

            Assert.That(store.Load()!.EnabledToolSets, Is.EqualTo(new[] { "git", "declarations" }));
        }
        finally
        {
            DeleteStateDir(stateDir);
        }
    }

    [Test]
    public async Task StopServer_UnknownToolset_InvalidArgumentListsValidNames()
    {
        var result = await _tools.McpServerControlTool(
            reason: "test message",
            operation: AdminTools.McpServerControlOperation.StopServer,
            confirmServerStop: AdminTools.McpServerStopConfirmation.ConfirmServerStop,
            toolsets: "git,notASet");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("notASet"));
        Assert.That(result.ErrorData!.Message, Does.Contain("git"));
    }

    [Test]
    public async Task StopServer_WithoutConfirmation_DoesNotTouchState()
    {
        var stateDir = NewStateDir();
        try
        {
            var store = new TabToolsetStateStore(stateDir, sessionId: null);
            var service = CreateToolsetService(ToolSetName.git);
            var tools = new AdminTools(_workspaceManager, tabToolsetState: store, toolsetService: service);

            var result = await tools.McpServerControlTool(
                reason: "test message",
                operation: AdminTools.McpServerControlOperation.StopServer,
                confirmServerStop: null,
                toolsets: "git");

            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConfirmationRequired));
            Assert.That(File.Exists(Path.Combine(stateDir, TabToolsetStateStore.FileName)), Is.False);
        }
        finally
        {
            DeleteStateDir(stateDir);
        }
    }

    [Test]
    public void StopServer_NoTabStateDir_SaysOptionsHadNoEffect()
    {
        var line = AdminTools.ApplyStopToolsets(null, null, keepToolsets: false, added: Array.Empty<ToolSetName>());
        Assert.That(line, Does.Contain("had no effect"));

        var quiet = AdminTools.ApplyStopToolsets(null, null, keepToolsets: true, added: Array.Empty<ToolSetName>());
        Assert.That(quiet, Is.Empty);
    }
}
