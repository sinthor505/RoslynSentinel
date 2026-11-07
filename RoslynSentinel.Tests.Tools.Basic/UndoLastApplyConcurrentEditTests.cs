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
public class UndoLastApplyConcurrentEditTests
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
        var undo = await editImpl.UndoLastApply(new ToolCallReason("testing undo refusal under concurrent edit"), changeId);

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
        var undo = await editImpl.UndoLastApply(new ToolCallReason("testing undo with no intervening write"), changeId);

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
        var undo = await editImpl.UndoLastApply(new ToolCallReason("testing undo refused by open ledger"), changeId);

        Assert.That(undo.IsError, Is.True);
        Assert.That(undo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
        Assert.That(ReadSeeded(ConsumersPath), Is.Not.EqualTo(ConsumersSource));
        Assert.That(ledger.GetOpenEntries().Single(e => e.EntryId == "entry-1").IsFixed, Is.True);
    }

    [Test]
    [Description("Undo without an agent id is not refused by the concurrent-edit guard")]
    public async Task Undo_WithoutAgentId_IsNotRefusedByTheGuard()
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
        var undo = await editImpl.UndoLastApply(new ToolCallReason("testing undo without agent id"), changeId);

        Assert.That(undo.ErrorData?.ErrorCode, Is.Not.EqualTo(ToolErrorCode.ConcurrentEditRefused));
    }
}
