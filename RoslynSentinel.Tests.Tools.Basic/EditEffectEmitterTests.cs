using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Slice 2 of proposal_not_found_cause_from_prior_tool_call: RenameSymbol and Member add/remove record an EditEffect on the ambient
// ToolCallContext just before the write chokepoint, and UndoLastApply marks the undone change's effects reverted.
// The emitter tests run on an InMemoryWorkspace, whose FakeWorkspaceManager has no real chokepoint: the effects stay pending on
// the context, so TakePendingEffects shows exactly what the emitter recorded. The commit-at-chokepoint behaviour is covered by
// EditEffectChokepointTests; the undo test below uses a real on-disk workspace so the blob stamps the change id.
[TestFixture]
public class EditEffectEmitterTests
{
    private const string FixturePath = "ContosoOrders.Core/EditEffectEmitterFixture.cs";

    private const string FixtureSource =
        "namespace Emit;\r\n\r\npublic class Widget\r\n{\r\n    public int Size() => 1;\r\n\r\n    public int Unused() => 2;\r\n}\r\n\r\npublic class User\r\n{\r\n    public int Use(Widget w) => w.Size();\r\n}\r\n";

    [TearDown]
    public void ResetContext()
    {
        ToolCallContext.Set(null);
    }

    private static ToolCallContext StartCall(string toolName)
    {
        var context = new ToolCallContext("agent-1", "coder", toolName, "emit an effect", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        return context;
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Apply_RecordsOneRenamedEffectWithTheNamesAndTheDeclaringFile()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(workspace.Manager);
        var context = StartCall("RenameSymbol");

        var result = await impl.RenameSymbol("rename Size", "InMemoryProject", "M:Emit.Widget.Size", "Dimension");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(1));
        var effect = effects[0];
        Assert.Multiple(() =>
        {
            Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.Renamed));
            Assert.That(effect.SimpleName, Is.EqualTo("Size"));
            Assert.That(effect.NewName, Is.EqualTo("Dimension"));
            Assert.That(effect.SymbolDocId, Is.EqualTo("M:Emit.Widget.Size"));
            Assert.That(effect.SymbolDisplay, Does.EndWith("Widget.Size").And.Not.Contain("("), "display is container-qualified, with no parameter list");
            Assert.That(effect.NewContainer, Is.Null, "a rename does not move the symbol");
            Assert.That(effect.NewFilePath, Does.EndWith("EditEffectEmitterFixture.cs"));
            Assert.That(effect.ToolName, Is.EqualTo("RenameSymbol"));
            Assert.That(effect.ToolCallId, Is.EqualTo("a76e9f1c"));
            Assert.That(effect.ChangeId, Is.Null, "the change id is stamped later, at blob write");
        });
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_DryRun_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(workspace.Manager);
        var context = StartCall("RenameSymbol");

        var result = await impl.RenameSymbol("dry run rename", "InMemoryProject", "M:Emit.Widget.Size", "Dimension", dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(context.TakePendingEffects(), Is.Empty, "a dry run writes nothing, so it records nothing");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_UnresolvedSymbol_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(workspace.Manager);
        var context = StartCall("RenameSymbol");

        var result = await impl.RenameSymbol("rename a missing symbol", "InMemoryProject", "M:Emit.Widget.NoSuchMethod", "Dimension");

        Assert.That(result.IsError, Is.True);
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_WithNoAmbientContext_StillRenames()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(workspace.Manager);
        ToolCallContext.Set(null);

        var result = await impl.RenameSymbol("rename with no context", "InMemoryProject", "M:Emit.Widget.Size", "Dimension");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FixturePath), Does.Contain("Dimension()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberRemove_Apply_RecordsARemovedEffectWithTheDocIdAndTheFile()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "remove an unused method",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.remove,
            containerName: "Widget",
            memberName: "Unused");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(1));
        var effect = effects[0];
        Assert.Multiple(() =>
        {
            Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.Removed));
            Assert.That(effect.SimpleName, Is.EqualTo("Unused"));
            Assert.That(effect.SymbolDocId, Is.EqualTo("M:Emit.Widget.Unused"));
            Assert.That(effect.SymbolDisplay, Does.EndWith("Widget.Unused"));
            Assert.That(effect.NewName, Is.Null);
            Assert.That(effect.NewFilePath, Does.EndWith("EditEffectEmitterFixture.cs"), "the file the symbol was removed from");
            Assert.That(effect.ToolName, Is.EqualTo("Member"));
        });
        Assert.That(workspace.ReadText(FixturePath), Does.Not.Contain("Unused"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberRemove_DryRun_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "dry run remove",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.remove,
            containerName: "Widget",
            memberName: "Unused",
            dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberRemove_RefusedBecauseTheMemberHasCallers_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "remove a method that is called",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.remove,
            containerName: "Widget",
            memberName: "Size");

        Assert.That(result.IsError, Is.True, "User.Use calls Size, so the removal is refused");
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberAdd_AddMember_RecordsAnAddedEffectWithTheContainerAndTheFile()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "add a method",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.addMember,
            containerName: "Widget",
            newMemberSource: "public int Extra() => 3;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(1));
        var effect = effects[0];
        Assert.Multiple(() =>
        {
            Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.Added));
            Assert.That(effect.SimpleName, Is.EqualTo("Extra"));
            Assert.That(effect.SymbolDisplay, Is.EqualTo("Widget.Extra"));
            Assert.That(effect.NewContainer, Is.EqualTo("Widget"));
            Assert.That(effect.SymbolDocId, Is.Null, "the doc id is not known before the write");
            Assert.That(effect.NewFilePath, Does.EndWith("EditEffectEmitterFixture.cs"));
        });
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberAdd_AddTopLevelType_RecordsAnAddedEffectForTheType()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "add a type",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.addTopLevelType,
            newMemberSource: "public class Gadget\r\n{\r\n}");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(1));
        Assert.That(effects[0].Kind, Is.EqualTo(EditEffectKind.Added));
        Assert.That(effects[0].SimpleName, Is.EqualTo("Gadget"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberAdd_DryRunAndUnparsableSource_RecordNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FixturePath, FixtureSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var dryRun = await impl.Member(
            reason: "dry run add",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.addMember,
            containerName: "Widget",
            newMemberSource: "public int Extra() => 3;",
            dryRun: true);
        var broken = await impl.Member(
            reason: "add unparsable source",
            filePath: workspace.PathOf(FixturePath),
            operation: MemberAction.addMember,
            containerName: "Widget",
            newMemberSource: "public int Broken( =>");

        Assert.That(dryRun.IsError, Is.False, dryRun.ErrorData?.Message);
        Assert.That(broken.IsError, Is.True);
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public void DescribeAdded_ParsesEveryDeclaredName_AndEnumMembers()
    {
        var field = MemberEditEffectFacts.DescribeAdded("private int _a, _b;", "Widget", isEnumMember: false);
        Assert.That(field.Select(f => f.SimpleName), Is.EqualTo(new[] { "_a", "_b" }));

        var enumMembers = MemberEditEffectFacts.DescribeAdded("Pending = 1, Shipped", "OrderState", isEnumMember: true);
        Assert.That(enumMembers.Select(f => f.Display), Is.EqualTo(new[] { "OrderState.Pending", "OrderState.Shipped" }));

        Assert.That(MemberEditEffectFacts.DescribeAdded("   ", "Widget", isEnumMember: false), Is.Empty);
    }

    [TestCase("N.Foo<T>.Bar(int)", "Bar")]
    [TestCase("Foo`1", "Foo")]
    [TestCase("Foo<TKey, TValue>", "Foo")]
    [TestCase("Bar", "Bar")]
    [TestCase("  ", "")]
    public void SimpleName_StripsContainerArityAndParameterList(string input, string expected)
    {
        Assert.That(EditEffectNames.SimpleName(input), Is.EqualTo(expected));
    }
}

// UndoLastApply marks the effects of the undone change reverted. A real on-disk workspace and the real chokepoint: the apply writes a
// blob, the blob write stamps the change id on the effects, and the undo finds them by that id.
[TestFixture]
public class EditEffectUndoTests
{
    private const string WidgetsPath = "ContosoOrders.Core/Cf/Widgets.cs";

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

    private PersistentWorkspaceManager _workspaceManager;
    private TestSolutionFixture? _fixture;

    [SetUp]
    public async Task Setup()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, WidgetsPath, WidgetsSource, reloadSolution: true);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private WorkspaceFileEditImpl NewEditImpl() => new(
        _workspaceManager,
        Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
        NullLogger.Instance,
        Battery.Basic.TestEngines.ValidationEngine(_workspaceManager),
        Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
        WriteToolAdviceHelper.WithAllToolsExposed());

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameThenUndo_TheEffectIsStampedWithTheChangeId_AndUndoMarksItReverted()
    {
        var context = new ToolCallContext("agent-a", "implementer", "RenameSymbol", "rename Create", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(_workspaceManager);

        var rename = await impl.RenameSymbol("rename the factory method", "ContosoOrders.Core", CreateFactory, "Build");

        Assert.That(rename.IsError, Is.False, rename.ErrorData?.Message);
        var changeId = JsonSerializer.SerializeToElement(rename.SuccessData).GetProperty("changeId").GetString()!;
        var index = ((IEditEffectIndexProvider)_workspaceManager).EditEffects;
        var committed = index.Find("Create");
        Assert.That(committed, Has.Count.EqualTo(1), "the chokepoint committed the effect with the write");
        Assert.That(committed[0].Kind, Is.EqualTo(EditEffectKind.Renamed));
        Assert.That(committed[0].NewName, Is.EqualTo("Build"));
        Assert.That(committed[0].ChangeId, Is.EqualTo(changeId), "the blob write stamped the effect with the change id");

        var undo = await NewEditImpl().UndoLastApply(new ToolCallReason("undo the rename"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(index.Find("Create"), Is.Empty, "a reverted effect is excluded from lookups");
        Assert.That(index.Count, Is.EqualTo(1), "the entry is kept, marked reverted");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task UndoOfAnotherChange_LeavesTheEffectActive()
    {
        var context = new ToolCallContext("agent-a", "implementer", "RenameSymbol", "rename Create", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        var impl = Battery.Basic.TestTools.RefactoringSignatureImpl(_workspaceManager);
        var rename = await impl.RenameSymbol("rename the factory method", "ContosoOrders.Core", CreateFactory, "Build");
        Assert.That(rename.IsError, Is.False, rename.ErrorData?.Message);

        var undo = await NewEditImpl().UndoLastApply(new ToolCallReason("undo an unknown change"), "deadbeef");

        Assert.That(undo.IsError, Is.True, "no blob exists for that id");
        Assert.That(((IEditEffectIndexProvider)_workspaceManager).EditEffects.Find("Create"), Has.Count.EqualTo(1));
    }

    [Test]
    public void EffectsCommittedWithNoBlob_HaveNoChangeId_SoAnUndoCannotRevertThem()
    {
        // Documents the no-blob path: a write that produces no blob never stamps a change id, and UndoLastApply needs a blob to
        // run at all, so such an effect stays active. Slice 3's lookup treats it like any other active effect.
        var context = new ToolCallContext("agent-a", "implementer", "RenameSymbol", "rename", 0) { SessionId = "session-1" };
        ToolCallContext.Set(context);
        context.RecordEffect(EditEffectKind.Renamed, "Cf.WidgetFactory.Create", CreateFactory, "Create", "Build", null, "Widgets.cs");
        context.CommitPendingEffects(_workspaceManager.EditEffects);

        var effect = _workspaceManager.EditEffects.Find("Create").Single();
        Assert.That(effect.ChangeId, Is.Null);
        Assert.That(_workspaceManager.EditEffects.MarkReverted("abc12345"), Is.EqualTo(0));
        Assert.That(_workspaceManager.EditEffects.Find("Create"), Has.Count.EqualTo(1));
    }
}
