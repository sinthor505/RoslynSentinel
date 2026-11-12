using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Slice 4 of proposal_not_found_cause_from_prior_tool_call: MoveMember (Moved), ChangeSignature (SignatureChanged) and DeleteFile
// (Removed per declared type, then FileDeleted) record an EditEffect just before the write chokepoint, and the index is seeded from
// recent operation blobs at solution load. The MoveMember and ChangeSignature tests run on an InMemoryWorkspace, whose
// FakeWorkspaceManager has no real chokepoint, so the effects stay pending on the context (TakePendingEffects shows exactly what the
// emitter recorded), as in EditEffectEmitterTests. The DeleteFile and seeding tests use a real on-disk workspace.
[TestFixture]
public class EditEffectMoveSignatureEmitterTests
{
    private const string MoveSourcePath = "ContosoOrders.Core/MoveFixtureSource.cs";

    private const string MoveTargetPath = "ContosoOrders.Core/MoveFixtureTarget.cs";

    private const string MoveSource = "public class Source\r\n{\r\n    public int Get() => 1;\r\n\r\n    public int Other() => 2;\r\n}\r\n";

    private const string MoveTarget = "public class Target\r\n{\r\n}\r\n";

    private const string WidgetPath = "ContosoOrders.Core/SignatureFixture.cs";

    private const string WidgetSource = "namespace Emit;\r\n\r\npublic class Widget\r\n{\r\n    public int Size(int a, string b) => a + b.Length;\r\n}\r\n\r\npublic class User\r\n{\r\n    public int Use(Widget w) => w.Size(1, \"x\");\r\n}\r\n";

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
    [Category("CoreRefactoringTools")]
    public async Task MoveMember_Apply_RecordsAMovedEffectPerMemberWithTheOldContainerTheTargetAndItsFile()
    {
        using var workspace = InMemoryWorkspace.Create((MoveSourcePath, MoveSource), (MoveTargetPath, MoveTarget));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("MoveMember");

        var result = await tools.MoveMemberTool(reason: "move two members", workspace.PathOf(MoveSourcePath), "Source", ["Get", "Other"], "Target");

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(2));
        Assert.That(effects.Select(e => e.SimpleName), Is.EqualTo(new[] { "Get", "Other" }));
        Assert.Multiple(() =>
        {
            Assert.That(effects.Select(e => e.Kind), Is.All.EqualTo(EditEffectKind.Moved));
            Assert.That(effects[0].SymbolDisplay, Is.EqualTo("Source.Get"), "display names the old container, with no parameter list");
            Assert.That(effects[0].SymbolDocId, Is.EqualTo("M:Source.Get"));
            Assert.That(effects.Select(e => e.NewContainer), Is.All.EqualTo("Target"));
            Assert.That(effects.Select(e => e.NewFilePath), Is.All.Not.Null.And.Matches<string?>(p => p!.EndsWith("MoveFixtureTarget.cs", StringComparison.Ordinal)), "the file that declares the target type");
            Assert.That(effects.Select(e => e.NewName), Is.All.Null);
            Assert.That(effects.Select(e => e.ToolName), Is.All.EqualTo("MoveMember"));
            Assert.That(effects.Select(e => e.ToolCallId), Is.All.EqualTo("a76e9f1c"));
            Assert.That(effects.Select(e => e.ChangeId), Is.All.Null, "the change id is stamped later, at blob write");
        });
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task MoveMember_DryRun_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((MoveSourcePath, MoveSource), (MoveTargetPath, MoveTarget));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("MoveMember");

        var result = await tools.MoveMemberTool(reason: "dry run move", workspace.PathOf(MoveSourcePath), "Source", ["Get"], "Target", dryRun: true);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(context.TakePendingEffects(), Is.Empty, "a dry run writes nothing, so it records nothing");
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task MoveMember_RefusedForAnUnknownMember_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((MoveSourcePath, MoveSource), (MoveTargetPath, MoveTarget));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("MoveMember");

        var result = await tools.MoveMemberTool(reason: "move a member that does not exist", workspace.PathOf(MoveSourcePath), "Source", ["NoSuchMember"], "Target");

        Assert.That(result.IsError, Is.True);
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task MoveMember_KeepDelegator_RecordsNothing_BecauseTheOldNameStillResolves()
    {
        const string original = "public class Src\r\n{\r\n    public static int Foo(int x) { return x + 1; }\r\n}\r\n\r\npublic class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("MoveMember");

        var result = await tools.MoveMemberTool(reason: "move and keep a forwarder", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(context.TakePendingEffects(), Is.Empty, "a keep mode leaves a forwarder under the old name, so no Moved fact is recorded");
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task MoveMember_WithNoAmbientContext_StillMoves()
    {
        using var workspace = InMemoryWorkspace.Create((MoveSourcePath, MoveSource), (MoveTargetPath, MoveTarget));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        ToolCallContext.Set(null);

        var result = await tools.MoveMemberTool(reason: "move with no context", workspace.PathOf(MoveSourcePath), "Source", ["Get"], "Target");

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText(MoveTargetPath), Does.Contain("Get()"));
    }

    [Test]
    public void FindTargetFile_PrefersTheGivenPath_ThenAnotherChangedFile_ThenTheSourceFile()
    {
        var source = new FilePathWrapper("C:/repo/Source.cs");
        var target = new FilePathWrapper("C:/repo/Target.cs");
        var caller = new FilePathWrapper("C:/repo/Caller.cs");
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [source] = "public class Source { public class Target { } }",
            [caller] = "public class Caller { void M() { var t = new Target(); } }",
            [target] = "public class Target { public int Get() => 1; }",
        };

        Assert.That(MoveEditEffectFacts.FindTargetFile(changes, "Target", targetFilePath: null, source), Is.EqualTo(target.Absolute), "another changed file that declares the type wins over the source file and over a file that only mentions it");
        Assert.That(MoveEditEffectFacts.FindTargetFile(changes, "Target", new FilePathWrapper("C:/repo/Given.cs"), source), Is.EqualTo(new FilePathWrapper("C:/repo/Given.cs").Absolute));
        Assert.That(MoveEditEffectFacts.FindTargetFile(new Dictionary<FilePathWrapper, string> { [source] = "public class Source { } public class Target { }" }, "Target", null, source), Is.EqualTo(source.Absolute), "a same-file move");
        Assert.That(MoveEditEffectFacts.FindTargetFile(new Dictionary<FilePathWrapper, string> { [caller] = "public class Caller { }" }, "Target", null, source), Is.Null);
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task ChangeSignature_Apply_RecordsASignatureChangedEffectWithTheDocIdAndTheFile()
    {
        using var workspace = InMemoryWorkspace.Create((WidgetPath, WidgetSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("ChangeSignature");

        var result = await tools.ChangeSignatureTool(
            reason: "swap the parameters",
            workspace.PathOf(WidgetPath),
            "Size",
            [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)]);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var effects = context.TakePendingEffects();
        Assert.That(effects, Has.Count.EqualTo(1));
        var effect = effects[0];
        Assert.Multiple(() =>
        {
            Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.SignatureChanged));
            Assert.That(effect.SimpleName, Is.EqualTo("Size"));
            Assert.That(effect.SymbolDisplay, Is.EqualTo("Emit.Widget.Size"), "container-qualified, with no parameter list");
            Assert.That(effect.SymbolDocId, Is.EqualTo("M:Emit.Widget.Size(System.Int32,System.String)"), "the doc id of the signature before the change");
            Assert.That(effect.NewName, Is.Null);
            Assert.That(effect.NewContainer, Is.Null);
            Assert.That(effect.NewFilePath, Does.EndWith("SignatureFixture.cs"));
            Assert.That(effect.ToolName, Is.EqualTo("ChangeSignature"));
        });
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task ChangeSignature_DryRun_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((WidgetPath, WidgetSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("ChangeSignature");

        var result = await tools.ChangeSignatureTool(
            reason: "dry run swap",
            workspace.PathOf(WidgetPath),
            "Size",
            [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)],
            dryRun: true);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }

    [Test]
    [Category("CoreRefactoringTools")]
    public async Task ChangeSignature_UnknownMethod_RecordsNothing()
    {
        using var workspace = InMemoryWorkspace.Create((WidgetPath, WidgetSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var context = StartCall("ChangeSignature");

        var result = await tools.ChangeSignatureTool(
            reason: "change a method that does not exist",
            workspace.PathOf(WidgetPath),
            "NoSuchMethod",
            [new ChangeSignatureParameterInput(originalIndex: 0)]);

        Assert.That(result.IsError, Is.True);
        Assert.That(context.TakePendingEffects(), Is.Empty);
    }
}

// DeleteFile calls the chokepoint directly, so a real on-disk workspace: the chokepoint commits the pending effects when the delete
// stands, and the blob write stamps them with the change id.
[TestFixture]
public class EditEffectDeleteFileEmitterTests
{
    private const string GonePath = "ContosoOrders.Core/Del/Gone.cs";

    private const string GoneSource = "namespace Del;\r\n\r\npublic class Gone\r\n{\r\n}\r\n\r\npublic enum GoneKind\r\n{\r\n    A,\r\n}\r\n";

    private PersistentWorkspaceManager _workspaceManager;
    private TestSolutionFixture? _fixture;

    [SetUp]
    public async Task Setup()
    {
        ToolCallContext.Set(null);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, GonePath, GoneSource, reloadSolution: true);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private WholeFileWriteTools NewTools() =>
        Battery.Basic.TestTools.WholeFileWriteTools(_workspaceManager, Battery.Basic.TestTools.WorkspaceTools(_workspaceManager));

    private static ToolCallContext StartCall()
    {
        var context = new ToolCallContext("agent-1", "coder", "DeleteFile", "delete a file", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        return context;
    }

    [Test]
    [Category("WholeFileWriteTools")]
    public async Task DeleteFile_Apply_RecordsARemovedEffectPerDeclaredTypeAndAFileDeletedEffect()
    {
        StartCall();
        var path = Path.Combine(_fixture!.SolutionDirectory, "ContosoOrders.Core", "Del", "Gone.cs");

        var result = await NewTools().DeleteFileTool(reason: "delete the file", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(path), Is.False);
        var index = _workspaceManager.EditEffects;
        var byName = index.Find("Gone");
        Assert.That(byName.Select(e => e.Kind), Is.EquivalentTo(new[] { EditEffectKind.Removed, EditEffectKind.FileDeleted }), "the type and the file share the name Gone");
        var removed = byName.Single(e => e.Kind == EditEffectKind.Removed);
        var deleted = byName.Single(e => e.Kind == EditEffectKind.FileDeleted);
        var enumRemoved = index.Find("GoneKind").Single();
        Assert.Multiple(() =>
        {
            Assert.That(removed.SymbolDisplay, Is.EqualTo("Del.Gone"));
            Assert.That(removed.SymbolDocId, Is.EqualTo("T:Del.Gone"));
            Assert.That(removed.NewFilePath, Does.EndWith("Gone.cs"), "the deleted file");
            Assert.That(enumRemoved.Kind, Is.EqualTo(EditEffectKind.Removed));
            Assert.That(enumRemoved.SymbolDisplay, Is.EqualTo("Del.GoneKind"));
            Assert.That(deleted.SymbolDisplay, Is.EqualTo(path), "the path as the caller passed it");
            Assert.That(deleted.SimpleName, Is.EqualTo("Gone"), "the file name without its extension");
            Assert.That(deleted.NewFilePath, Does.EndWith("Gone.cs"));
            Assert.That(deleted.ToolName, Is.EqualTo("DeleteFile"));
            Assert.That(deleted.ToolCallId, Is.EqualTo("a76e9f1c"));
            Assert.That(new[] { removed, enumRemoved, deleted }.Select(e => e.ChangeId), Is.All.Not.Null, "the blob write stamped the change id");
        });
    }

    [Test]
    [Category("WholeFileWriteTools")]
    public async Task DeleteFile_Apply_ProducesNotesForTheTypeNameAndTheFileName()
    {
        StartCall();
        var path = Path.Combine(_fixture!.SolutionDirectory, "ContosoOrders.Core", "Del", "Gone.cs");
        var result = await NewTools().DeleteFileTool(reason: "delete the file", path);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var index = _workspaceManager.EditEffects;

        var typeNote = EditEffectEvidence.Find(index, "GoneKind", "session-1", ServerInstanceIdentity.Id, "agent-1", solution: null, solutionRoot: null, DateTime.UtcNow);
        var fileNote = EditEffectEvidence.Find(index, "Gone", "session-1", ServerInstanceIdentity.Id, "agent-1", solution: null, solutionRoot: null, DateTime.UtcNow);

        Assert.That(typeNote, Does.Contain("removed `Del.GoneKind` from `").And.Contain("Gone.cs"));
        Assert.That(fileNote, Does.Contain("deleted `" + path + "`"));
    }

    [Test]
    [Category("WholeFileWriteTools")]
    public async Task DeleteFile_MissingFile_RecordsNothing()
    {
        var context = StartCall();
        var path = Path.Combine(_fixture!.SolutionDirectory, "ContosoOrders.Core", "Del", "NoSuchFile.cs");

        var result = await NewTools().DeleteFileTool(reason: "delete a file that is not there", path);

        Assert.That(result.IsError, Is.True);
        Assert.That(context.TakePendingEffects(), Is.Empty);
        Assert.That(_workspaceManager.EditEffects.Count, Is.EqualTo(0));
    }

    [Test]
    [Category("WholeFileWriteTools")]
    public async Task DeleteFile_WithNoAmbientContext_StillDeletes()
    {
        ToolCallContext.Set(null);
        var path = Path.Combine(_fixture!.SolutionDirectory, "ContosoOrders.Core", "Del", "Gone.cs");

        var result = await NewTools().DeleteFileTool(reason: "delete with no context", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(File.Exists(path), Is.False);
        Assert.That(_workspaceManager.EditEffects.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task DescribeDeclaredTypes_SkipsPartialTypes_AndAFileOutsideTheSolution()
    {
        const string partialPath = "ContosoOrders.Core/Del/Parts.cs";
        await _fixture!.AddFileToSolution(_workspaceManager, partialPath, "namespace Del;\r\n\r\npublic partial class Parts\r\n{\r\n}\r\n\r\npublic struct Whole\r\n{\r\n}\r\n", reloadSolution: true);
        var committed = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, CancellationToken.None);

        var inSolution = await DeleteFileEditEffectFacts.DescribeDeclaredTypesAsync(committed, new FilePathWrapper(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "Del", "Parts.cs")), CancellationToken.None);
        var outside = await DeleteFileEditEffectFacts.DescribeDeclaredTypesAsync(committed, new FilePathWrapper(Path.Combine(_fixture.SolutionDirectory, "NotInTheSolution.cs")), CancellationToken.None);

        Assert.That(inSolution.Select(t => t.Display), Is.EqualTo(new[] { "Del.Whole" }), "a partial type survives the deletion of one part");
        Assert.That(outside, Is.Empty);
    }
}

// Seeding at LoadSolution, and the undo marker that keeps a reverted change out of the seeded index. A real on-disk workspace; the
// blobs are written the way OperationBlobWriter names them.
[TestFixture]
public class EditEffectSeedAtLoadTests
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
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private static string WriteBlob(string operationsDir, string changeId, DateTime stamp, EditEffect effect)
    {
        Directory.CreateDirectory(operationsDir);
        var timestamp = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var fraction = (stamp.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture);
        var path = Path.Combine(operationsDir, $"RenameSymbol_{timestamp}_{fraction}_{changeId}.json");
        var payload = new
        {
            toolName = "RenameSymbol",
            changeId,
            toolCallId = "a76e9f1c",
            generatedUtc = stamp.ToString("O"),
            itemCount = 0,
            effects = JsonSerializer.SerializeToElement(new[] { effect }, EditEffect.JsonOptions),
            items = Array.Empty<object>(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task LoadSolution_SeedsTheIndexFromRecentBlobs_AndASecondLoadDoesNotDuplicate()
    {
        var root = _workspaceManager.GetSolutionRoot()!;
        var operations = Path.Combine(root, ".roslynsentinel", "operations");
        var now = DateTime.UtcNow;
        var effect = new EditEffect(EditEffectKind.Renamed, "Cf.Widget.Size", "M:Cf.Widget.Size", "Size", "Dimension", null, "Widget.cs", null, "RenameSymbol", null, "session-1", "server-before-restart", "agent-1", now.AddMinutes(-5));
        WriteBlob(operations, "abc12345", now.AddMinutes(-5), effect);

        await _workspaceManager.LoadSolutionAsync(_fixture!.SolutionPath);
        await WaitUntilAsync(() => _workspaceManager.EditEffects.Count > 0);

        var seeded = _workspaceManager.EditEffects.Find("Size").Single();
        Assert.Multiple(() =>
        {
            Assert.That(seeded.ChangeId, Is.EqualTo("abc12345"), "the blob's change id");
            Assert.That(seeded.ToolCallId, Is.EqualTo("a76e9f1c"), "the blob's call id");
            Assert.That(seeded.SessionId, Is.EqualTo("session-1"), "identity is filtered at lookup, not at seeding");
            Assert.That(seeded.Reverted, Is.False);
        });

        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await Task.Delay(500);
        EditEffectSeeder.Seed(_workspaceManager.EditEffects, root, DateTime.UtcNow);
        Assert.That(_workspaceManager.EditEffects.Count, Is.EqualTo(1), "an effect already in the index is not seeded again");
    }

    [Test]
    public async Task RenameThenUndo_WritesAnUndoMarker_AndAFreshIndexIsNotSeededFromTheUndoneChange()
    {
        await _fixture!.AddFileToSolution(_workspaceManager, WidgetsPath, WidgetsSource, reloadSolution: true);
        var context = new ToolCallContext("agent-a", "implementer", "RenameSymbol", "rename Create", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        ToolCallContext.Set(context);
        var rename = await Battery.Basic.TestTools.RefactoringSignatureImpl(_workspaceManager).RenameSymbol("rename the factory method", "ContosoOrders.Core", CreateFactory, "Build");
        Assert.That(rename.IsError, Is.False, rename.ErrorData?.Message);
        var changeId = JsonSerializer.SerializeToElement(rename.SuccessData).GetProperty("changeId").GetString()!;
        var root = _workspaceManager.GetSolutionRoot()!;
        var blobPath = OperationBlobWriter.FindBlobPath(changeId, root)!;

        var beforeUndo = new EditEffectIndex();
        EditEffectSeeder.Seed(beforeUndo, root, DateTime.UtcNow);
        Assert.That(beforeUndo.Find("Create").Single().ChangeId, Is.EqualTo(changeId), "control: the committed rename is seeded from its blob");
        Assert.That(OperationBlobWriter.IsUndone(blobPath), Is.False);

        var edit = new WorkspaceFileEditImpl(
            _workspaceManager,
            Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager),
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await edit.UndoLastApply(new ToolCallReason("undo the rename"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(OperationBlobWriter.IsUndone(blobPath), Is.True, "the undo left a marker beside the blob");
        Assert.That(File.Exists(blobPath), Is.True, "the blob itself is untouched");
        Assert.That(OperationBlobWriter.FindBlobPath(changeId, root), Is.EqualTo(blobPath), "the marker does not disturb blob lookup by change id");
        var afterUndo = new EditEffectIndex();
        EditEffectSeeder.Seed(afterUndo, root, DateTime.UtcNow);
        Assert.That(afterUndo.Find("Create"), Is.Empty, "a seeded index skips the undone change");
    }
}
