using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(addMember) with two declarations in newMemberSource, against the edit-effect and undo contract: one write and one
/// changeId for the whole block; UndoChange restores the file byte for byte; each declaration records its own Added effect
/// on the ambient ToolCallContext; a refused or dry-run add records nothing; the undo marks both committed effects reverted.
/// The on-disk cases use a real PersistentWorkspaceManager so the blob stamps the change id (an InMemoryWorkspace cannot undo).
/// </summary>
[TestFixture]
[Category("RefactoringStructuralTools")]
public class MemberAddManyUndoTests
{
    private const string HolderPath = "ContosoOrders.Core/Mu/Holder.cs";

    private const string HolderSource = """
        namespace Mu;

        public class Holder
        {
            public int Seed() => 0;
        }
        """;

    private const string TwoMembers = "public int A() => 1;\n\npublic int B() => 2;";

    private PersistentWorkspaceManager _workspaceManager;
    private TestSolutionFixture? _fixture;

    // Deviation from EditEffectUndoTests: the on-disk workspace is opened per test by OpenOnDiskAsync instead of in SetUp,
    // because the effect-recording tests run on an InMemoryWorkspace and should not pay for a solution load.
    [SetUp]
    public void ResetContext()
    {
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private async Task OpenOnDiskAsync()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, HolderPath, HolderSource, reloadSolution: true);
    }

    private string HolderFullPath => Path.Combine(_fixture!.SolutionDirectory, HolderPath);

    private WorkspaceFileEditImpl NewEditImpl() => new(
        _workspaceManager,
        Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
        NullLogger.Instance,
        Battery.Basic.TestEngines.ValidationEngine(_workspaceManager),
        Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
        WriteToolAdviceHelper.WithAllToolsExposed());

    private static ToolCallContext StartCall(string toolName)
    {
        var context = new ToolCallContext("agent-1", "coder", toolName, "emit an effect", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        return context;
    }

    [Test]
    public async Task AddTwoMembers_UndoChange_RestoresTheFileByteForByte()
    {
        await OpenOnDiskAsync();
        var before = await File.ReadAllBytesAsync(HolderFullPath);
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(_workspaceManager);

        var add = await impl.Member(
            reason: "add two methods in one call",
            filePath: HolderFullPath,
            operation: MemberAction.addMember,
            containerName: "Holder",
            newMemberSource: TwoMembers);

        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);
        var summary = add.SuccessData as AppliedChangeSummary;
        Assert.That(summary?.ChangeId, Is.Not.Null.And.Not.Empty, "no changeId in the add result; serialized: " + JsonSerializer.Serialize(add.SuccessData));
        var changeId = summary!.ChangeId!;
        Assert.That(changeId, Is.Not.Empty);
        var added = await File.ReadAllTextAsync(HolderFullPath);
        Assert.That(added, Does.Contain("public int A() => 1;").And.Contain("public int B() => 2;"), "both members must be written before the undo");

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("undo the two-member add"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(await File.ReadAllBytesAsync(HolderFullPath), Is.EqualTo(before), "the undo restores the exact bytes, so both members are gone");
    }

    [Test]
    public async Task AddTwoMembers_RecordsTwoAddedEffectsNamedAfterTheContainer()
    {
        using var workspace = InMemoryWorkspace.Create((HolderPath, HolderSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "add two methods in one call",
            filePath: workspace.PathOf(HolderPath),
            operation: MemberAction.addMember,
            containerName: "Holder",
            newMemberSource: TwoMembers);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(2), "one Added effect per declaration in the block");
        Assert.Multiple(() =>
        {
            Assert.That(effects[0].Kind, Is.EqualTo(EditEffectKind.Added));
            Assert.That(effects[0].SimpleName, Is.EqualTo("A"));
            Assert.That(effects[0].SymbolDisplay, Is.EqualTo("Holder.A"));
            Assert.That(effects[1].Kind, Is.EqualTo(EditEffectKind.Added));
            Assert.That(effects[1].SimpleName, Is.EqualTo("B"));
            Assert.That(effects[1].SymbolDisplay, Is.EqualTo("Holder.B"));
        });
    }

    [Test]
    public async Task AddTwoMembers_SecondDeclarationFailsCompileGate_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((HolderPath, HolderSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "add with an undefined type in the second declaration",
            filePath: workspace.PathOf(HolderPath),
            operation: MemberAction.addMember,
            containerName: "Holder",
            newMemberSource: "public int A() => 1;\n\npublic Missing M2() => null;");

        Assert.That(result.IsError, Is.True, "the second declaration must fail the compile gate");
        Assert.That(context.TakePendingEffects(), Is.Empty, "a refused add records no effects");
    }

    [Test]
    public async Task AddTwoMembers_DryRun_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((HolderPath, HolderSource));
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(workspace.Manager);
        var context = StartCall("Member");

        var result = await impl.Member(
            reason: "dry run add of two methods",
            filePath: workspace.PathOf(HolderPath),
            operation: MemberAction.addMember,
            containerName: "Holder",
            newMemberSource: TwoMembers,
            dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(context.TakePendingEffects(), Is.Empty, "a dry run writes nothing, so it records nothing");
    }

    [Test]
    public async Task AddTwoMembers_ThenUndo_MarksBothCommittedEffectsReverted()
    {
        await OpenOnDiskAsync();
        var context = new ToolCallContext("agent-a", "implementer", "Member", "add two methods", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(_workspaceManager);

        var add = await impl.Member(
            reason: "add two methods in one call",
            filePath: HolderFullPath,
            operation: MemberAction.addMember,
            containerName: "Holder",
            newMemberSource: TwoMembers);

        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);
        var summary = add.SuccessData as AppliedChangeSummary;
        Assert.That(summary?.ChangeId, Is.Not.Null.And.Not.Empty, "no changeId in the add result; serialized: " + JsonSerializer.Serialize(add.SuccessData));
        var changeId = summary!.ChangeId!;
        var index = ((IEditEffectIndexProvider)_workspaceManager).EditEffects;
        Assert.Multiple(() =>
        {
            Assert.That(index.Find("A"), Has.Count.EqualTo(1), "the chokepoint committed the A effect with the write");
            Assert.That(index.Find("B"), Has.Count.EqualTo(1), "the chokepoint committed the B effect with the write");
            Assert.That(index.Find("A")[0].ChangeId, Is.EqualTo(changeId), "the blob write stamped A with the change id");
            Assert.That(index.Find("B")[0].ChangeId, Is.EqualTo(changeId), "the blob write stamped B with the change id");
        });

        var undo = await NewEditImpl().UndoChange(new ToolCallReason("undo the two-member add"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(index.Find("A"), Is.Empty, "a reverted effect is excluded from lookups");
            Assert.That(index.Find("B"), Is.Empty, "a reverted effect is excluded from lookups");
            Assert.That(index.Count, Is.EqualTo(2), "both entries are kept, marked reverted");
        });
    }
}
