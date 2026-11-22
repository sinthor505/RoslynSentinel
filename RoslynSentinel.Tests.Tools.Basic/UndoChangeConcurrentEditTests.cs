using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class UndoChangeConcurrentEditTests
{
    private const string WidgetsPath = "ContosoOrders.Core/Cf/Widgets.cs";
    private const string ConsumersPath = "ContosoOrders.Core/Cf/Consumers.cs";

    private const string WidgetType = "T:Cf.Widget";
    private const string CreateFactory = "M:Cf.WidgetFactory.Create(System.Int32,System.String)";

    private const string WidgetsSource = """
        namespace Cf;

        public class Widget
        {
            public Widget(int a, string b) { A = a; B = b; }
            public int A { get; }
            public string B { get; }
        }

        public static class WidgetFactory
        {
            public static Widget Create(int a, string b) => new Widget(a, b);
        }
        """;

    private const string ConsumersSource = """
        namespace Cf;

        public class Consumers
        {
            public Widget Plain() => new Widget(1, "a");
        }
        """;

    private IWorkspaceManager _workspaceManager;
    private ValidationEngine _validationEngine;
    private SemanticFindReplaceTools _tool;
    private TestSolutionFixture? _fixture;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _validationEngine = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager);
        _tool = new SemanticFindReplaceTools(
            new SemanticReplaceEngine(_workspaceManager),
            _validationEngine,
            _workspaceManager,
            NullLogger<SemanticFindReplaceTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private async Task SeedAsync()
    {
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, WidgetsPath, WidgetsSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, ConsumersPath, ConsumersSource, reloadSolution: true);
    }

    private string ReadSeeded(string relativePath) =>
        File.ReadAllText(Path.Combine(_fixture!.SolutionDirectory, relativePath));

    private Task<SentinelCallToolResult<object>> RunAsync(SemanticReplaceMode mode, string symbol, string factory) =>
        _tool.SemanticFindReplaceTool(
            new ToolCallReason("testing undo concurrent edit"),
            SemanticReplaceOperation.replaceConstruction,
            symbol,
            mode,
            factory: factory);

    private static JsonElement Data(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return JsonSerializer.SerializeToElement(result.SuccessData);
    }

    [Test]
    [Description("Undo refuses when another agent edited a file after this agent's apply, and writes nothing")]
    public async Task Undo_AfterOtherAgentEdit_IsRefused_AndWritesNothing()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;

        var agentBText = ConsumersSource.Replace("new Widget(1, \"a\")", "new Widget(7, \"b\")");
        var agentBStartSeq = ((IEditAttributionLedgerProvider)_workspaceManager).EditAttributions.CurrentSeq;
        ToolCallContext.Set(new ToolCallContext("agent-b", "implementer", "ReplaceSnippet", "other agent edit", agentBStartSeq));
        var agentBWrite = await ((PersistentWorkspaceManager)_workspaceManager).ApplyProposedChangesAsync(
            new()
            {
                [consumersFullPath] = agentBText
            }, retryCount: 0, validateChanges: false);
        Assert.That(agentBWrite.IsError, Is.False, agentBWrite.Summary);
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));

        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo refusal under concurrent edit"), changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(undo.ErrorData.Message, Does.Contain("Undo refused"));
        Assert.That(undo.ErrorData.Message, Does.Contain("agent-b"));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(agentBText));
    }

    [Test]
    [Description("Undo still reverts when the file is unchanged since this agent's apply")]
    public async Task Undo_FileUnchangedSinceApply_StillReverts()
    {
        await SeedAsync();

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;

        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo with no intervening write"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource));
    }

    [Test]
    [Description("Undo success text names each reverted file with the lines the revert added and removed")]
    public async Task Undo_SuccessText_ReportsLinesChangedByRevert()
    {
        await SeedAsync();

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;

        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo line-delta summary"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        var text = undo.SuccessData?.ToString();
        Assert.That(text, Does.Contain("Lines changed by the revert:"));
        Assert.That(text, Does.Contain("Consumers.cs"));
    }

    [Test]
    [Description("ReplaceSnippet on disk returns a ChangeId that names UndoChange in its success text and reverts the file")]
    public async Task ReplaceSnippet_DiskBackedApply_ReturnsChangeIdThatUndoChangeReverts()
    {
        await SeedAsync();

        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var apply = await editImpl.ReplaceSnippet(
            new ToolCallReason("testing replace snippet undo"),
            ProposedChangeAction.apply,
            ConsumersPath,
            "Plain() => new Widget(1, \"a\");",
            "Plain() => new Widget(2, \"b\");");

        Assert.That(apply.IsError, Is.False, apply.ErrorData?.Message);
        Assert.That(apply.SuccessData!.ChangeId, Is.Not.Null);
        Assert.That(apply.SuccessData.ReplacementResult!.Summary, Does.Contain("UndoChange"));
        Assert.That(ReadSeeded(ConsumersPath), Does.Contain("new Widget(2, \"b\")"));

        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo of replace snippet"), apply.SuccessData.ChangeId!);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource));
    }

    [Test]
    [Description("Undo is refused with ScopedLedgerBlocked when an open scoped ledger blocks a file it would revert, and records no undo")]
    public async Task Undo_RefusedByOpenScopedLedger_ReturnsErrorAndSkipsRecordUndo()
    {
        await SeedAsync();

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;

        // The ledger tracks Widgets.cs, so Consumers.cs (which the undo would revert) is blocked.
        var ledger = (IScopedOperationLedger)_workspaceManager;
        var widgetsFilePath = _workspaceManager.ResolveFromWire(Path.Combine(_fixture!.SolutionDirectory, WidgetsPath)).Absolute;
        var opened = ledger.TryOpen(
            "Undo_Test",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = widgetsFilePath,
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                },
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-2",
                    FilePath = widgetsFilePath,
                    Line = 2,
                    BrokenExpression = "classCLocalVarB.Foo()",
                    OldStaticType = "ClassB",
                    Status = CallSiteStatus.Ambiguous,
                },
            ],
            out var rejection,
            openingChangeId: changeId);
        Assert.That(opened, Is.True, rejection);
        ledger.RecordFix(["entry-1"], "fix-id");

        ToolCallContext.Set(null);
        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo refused by open ledger"), changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
        Assert.That(ReadSeeded(ConsumersPath), Is.Not.EqualTo(ConsumersSource));
        Assert.That(ledger.GetOpenEntries().Single(e => e.EntryId == "entry-1").IsFixed, Is.True);
    }

    [Test]
    [Description("Undo without an agent id is not refused by the agent-ledger guard; the disk-text guard still refuses because another write changed the file after the apply")]
    public async Task Undo_WithoutAgentId_IsNotRefusedByTheLedgerGuard_ButTheDiskTextGuardRefuses()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;

        var agentBText = ConsumersSource.Replace("new Widget(1, \"a\")", "new Widget(7, \"b\")");
        var agentBStartSeq = ((IEditAttributionLedgerProvider)_workspaceManager).EditAttributions.CurrentSeq;
        ToolCallContext.Set(new ToolCallContext("agent-b", "implementer", "ReplaceSnippet", "other agent edit", agentBStartSeq));
        var agentBWrite = await ((PersistentWorkspaceManager)_workspaceManager).ApplyProposedChangesAsync(
            new()
            {
                [consumersFullPath] = agentBText
            }, retryCount: 0, validateChanges: false);
        Assert.That(agentBWrite.IsError, Is.False, agentBWrite.Summary);

        ToolCallContext.Set(null);
        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo without agent id"), changeId);

        // The ledger guard ("was changed after agent") needs an agent id and stays silent; the identity-free disk-text guard refuses.
        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(undo.ErrorData.Message, Does.Not.Contain("was changed after agent"));
        Assert.That(undo.ErrorData.Message, Does.Contain("differs from what change"));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(agentBText));
    }

    private WorkspaceFileEditImpl NewEditImpl() => new(
        _workspaceManager,
        Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
        NullLogger.Instance,
        _validationEngine,
        Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
        WriteToolAdviceHelper.WithAllToolsExposed());

    private async Task<string> ApplyAsAgentAAsync()
    {
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "SemanticFindReplace", "apply replaceConstruction", 0));
        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        return data.GetProperty("changeId").GetString()!;
    }

    [Test]
    [Description("Undo refuses when the same agent changed the file through the chokepoint after the apply, names the last recorded write, and writes nothing")]
    public async Task Undo_AfterSameAgentLaterChokepointEdit_IsRefused_AndWritesNothing()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);
        var changeId = await ApplyAsAgentAAsync();

        var laterText = ReadSeeded(ConsumersPath) + "// later edit by the same agent\r\n";
        var startSeq = ((IEditAttributionLedgerProvider)_workspaceManager).EditAttributions.CurrentSeq;
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "ReplaceSnippet", "later edit same agent", startSeq));
        var later = await ((PersistentWorkspaceManager)_workspaceManager).ApplyProposedChangesAsync(
            new()
            {
                [consumersFullPath] = laterText
            }, retryCount: 0, validateChanges: false);
        Assert.That(later.IsError, Is.False, later.Summary);
        var diskAfterLaterEdit = ReadSeeded(ConsumersPath);

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("testing undo refusal after a later edit by the same agent"), changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(undo.ErrorData.Message, Does.Contain("differs from what change").And.Contain("Consumers.cs"));
        Assert.That(undo.ErrorData.Message, Does.Contain("Last write the server recorded").And.Contain("agent-a"));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(diskAfterLaterEdit));
    }

    [Test]
    [Description("Undo refuses when the file was edited on disk outside the server after the apply, and leaves the disk text untouched")]
    public async Task Undo_AfterRawDiskEdit_IsRefused_AndDiskIsUntouched()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);
        var changeId = await ApplyAsAgentAAsync();

        var rawText = ReadSeeded(ConsumersPath) + "// raw disk edit\r\n";
        File.WriteAllText(consumersFullPath, rawText);

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("testing undo refusal after a raw disk edit"), changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(undo.ErrorData.Message, Does.Contain("differs from what change").And.Contain("GetOperationDetail"));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(rawText));
    }

    [Test]
    [Description("A blob item with no AfterSource (an older blob) is not checked against the disk text and undoes as before")]
    public async Task Undo_ItemWithoutAfterSource_IsNotCheckedAgainstDisk_AndReverts()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);
        var changeId = await ApplyAsAgentAAsync();

        // Make the blob look like one written before AfterSource existed: drop the property from every item.
        var blobPath = OperationBlobWriter.FindBlobPath(changeId, _workspaceManager.GetSolutionRoot())!;
        var blob = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(blobPath))!;
        var strippedAny = false;
        foreach (var item in blob["items"]!.AsArray())
        {
            var obj = item!.AsObject();
            foreach (var name in obj.Select(p => p.Key).Where(k => string.Equals(k, "AfterSource", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                obj.Remove(name);
                strippedAny = true;
            }
        }

        Assert.That(strippedAny, Is.True, "the apply is expected to have recorded AfterSource (slices 1-2); nothing to strip means the premise changed");
        File.WriteAllText(blobPath, blob.ToJsonString());

        // A later chokepoint write by no identified agent, so only the disk-text guard could object.
        ToolCallContext.Set(null);
        var laterText = ReadSeeded(ConsumersPath) + "// later edit\r\n";
        var later = await ((PersistentWorkspaceManager)_workspaceManager).ApplyProposedChangesAsync(
            new()
            {
                [consumersFullPath] = laterText
            }, retryCount: 0, validateChanges: false);
        Assert.That(later.IsError, Is.False, later.Summary);
        ((PersistentWorkspaceManager)_workspaceManager).ClearExternalFileChanges();

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("testing undo of a blob item without AfterSource"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource));
    }

    [Test]
    [Description("A line-ending-only difference between the disk text and the operation's recorded AfterSource does not refuse the undo")]
    public async Task Undo_DiskTextDiffersOnlyInLineEndings_IsNotRefused()
    {
        await SeedAsync();
        var consumersFullPath = Path.Combine(_fixture!.SolutionDirectory, ConsumersPath);
        var changeId = await ApplyAsAgentAAsync();

        var written = ReadSeeded(ConsumersPath);
        var flipped = written.Contains("\r\n")
            ? written.Replace("\r\n", "\n")
            : written.Replace("\n", "\r\n");
        Assert.That(flipped, Is.Not.EqualTo(written), "the rewrite must change the line endings, otherwise the test proves nothing");
        File.WriteAllText(consumersFullPath, flipped);
        ((PersistentWorkspaceManager)_workspaceManager).ClearExternalFileChanges();

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("testing undo after an EOL-only rewrite"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource));
    }
}
