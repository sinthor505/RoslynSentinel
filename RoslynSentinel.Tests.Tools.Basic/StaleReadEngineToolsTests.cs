using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Engine-tool opt-in to the stale-read guard (Member, ModifyModifier, RenameSymbol, plus the ReplaceSnippet anchor
// tool used for own-write refresh). ValidateAndApplyHelper passes StaleReadPolicy.RefuseIfObservedStaleQuiet by default,
// so a target this agent read and that then changed must be refused with ToolErrorCode.StaleRead, naming the file, and
// nothing written; a never-read target must stay quiet (no "read them first" finding); dryRun never reaches the
// chokepoint; a successful write refreshes the writer's own observed entry. Runs on a real PersistentWorkspaceManager.
// The helpers below are copied from StaleReadWriteToolsTests because that file's helpers are private; keep the two in
// step if the harness changes.
[TestFixture]
[Category("RefactoringStructuralTools")]
public class StaleReadEngineToolsTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

    private const string ProjectName = "StaleReadEngineTools";

    private static string External => Original.Replace("=> 1", "=> 3");

    private static string Edited => Original.Replace("=> 1", "=> 2");

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static void AsAgent(string agentId) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "Member", null, StartSeqAfterEveryWrite));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StaleReadEngineTools", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A real document whose FilePath is the temp file, so the in-memory branch of the guard and ExistingTextReader both see it.
    private static PersistentWorkspaceManager NewManager(string file, string documentText)
    {
        var project = new AdhocWorkspace().AddProject(ProjectName, LanguageNames.CSharp);
        var solution = project.AddDocument(Path.GetFileName(file), SourceText.From(documentText), filePath: file).Project.Solution;
        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(solution);
        return manager;
    }

    // Two documents in one project, for a rename that touches more than one file.
    private static PersistentWorkspaceManager NewManagerWithDocuments(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject(ProjectName, LanguageNames.CSharp);
        // Without a framework reference the rename's candidate compile reports environment errors (the harness has no
        // MSBuild references), which the compile gate reads as "introduces new compiler errors" before the stale guard runs.
        var solution = project.WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).Solution;
        foreach (var (file, text) in documents)
        {
            solution = solution.GetProject(project.Id)!
                .AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project.Solution;
        }

        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(solution);
        return manager;
    }

    // The harness runs no file watcher. After an external disk write the workspace document keeps the old text until a
    // sync, and the guard needs both disk and memory to match what the agent read. This stands in for that sync, which a
    // real ReadFile is followed by.
    private static void SyncWorkspaceText(PersistentWorkspaceManager manager, string file, string text)
    {
        var solution = manager.CurrentSolution!;
        var documentId = solution.GetDocumentIdsWithFilePath(new FilePathWrapper(file)).Single();
        manager.SetTestSolution(solution.WithDocumentText(documentId, SourceText.From(text)));
    }

    private static void AssertStaleRead<T>(SentinelCallToolResult<T> result, string fileName)
    {
        Assert.That(result.IsError, Is.True, "a stale target must be refused");
        Assert.That(result.ErrorData, Is.Not.Null, "a refusal must carry a ResultError");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead), result.ErrorData.Message);
        Assert.That(result.ErrorData.ErrorCode, Is.Not.EqualTo(ToolErrorCode.Exception), "StaleRead must not be flattened to Exception");
        Assert.That(result.ErrorData.Message, Does.Contain(fileName));
        Assert.That(result.ErrorData.Message, Does.Contain("ReadFile"));
        Assert.That(result.ErrorData.Message, Does.Contain("wait a moment"));
    }

    private static RefactoringStructuralTools BuildStructuralTools(IWorkspaceManager manager) =>
        TestTools.RefactoringStructuralTools(manager);

    [Test]
    public async Task Member_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildStructuralTools(manager);

            var result = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");

            AssertStaleRead(result, Path.GetFileName(file));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ModifyModifier_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildStructuralTools(manager);

            var result = await tools.ModifyModifierTool(
                reason: "test message", filePath: file, targetName: "X",
                modifier: NonAccessibilityModifier.@virtual, action: AddRemoveAction.add);

            AssertStaleRead(result, Path.GetFileName(file));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RenameSymbol_MultiFileBatchWithObservedThenChangedFile_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var fileA = Path.Combine(dir, "A.cs");
            var fileB = Path.Combine(dir, "B.cs");
            const string usesA = "namespace T;\r\npublic class B\r\n{\r\n    public A Make() => new A();\r\n}\r\n";
            File.WriteAllText(fileA, Original);
            File.WriteAllText(fileB, usesA);
            using var manager = NewManagerWithDocuments((fileA, Original), (fileB, usesA));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(fileA), Original);
            File.WriteAllText(fileA, External);
            var tools = new RefactoringSignatureTools(TestTools.RefactoringSignatureImpl(manager));

            var result = await tools.RenameSymbolTool(
                reason: "test message", projectName: ProjectName, docCommentId: "T:T.A", newName: "Renamed");

            AssertStaleRead(result, Path.GetFileName(fileA));
            Assert.That(await File.ReadAllTextAsync(fileA), Is.EqualTo(External), "the stale file must be left untouched");
            Assert.That(await File.ReadAllTextAsync(fileB), Is.EqualTo(usesA), "the other file in the batch must not be written either");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row D: a never-read existing target is not refused and does not get the "read them first" finding under the quiet policy.
    [Test]
    public async Task Member_NeverObservedExistingTarget_SucceedsWithoutUnobservedFindingAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            var tools = BuildStructuralTools(manager);

            var result = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
            Assert.That((result.Findings ?? []).Any(f => f.Message.Contains("read them first")), Is.False,
                "a never-read target must stay quiet under RefuseIfObservedStaleQuiet");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row E: dryRun validates only and never reaches the chokepoint, so the stale entry cannot refuse it.
    [Test]
    public async Task Member_DryRunOnObservedThenChangedFile_IsNotRefusedAsStaleReadAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildStructuralTools(manager);

            var result = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;", dryRun: true);

            Assert.That(result.ErrorData == null || result.ErrorData.ErrorCode != ToolErrorCode.StaleRead, Is.True,
                "dryRun never reaches the chokepoint, so it must not be refused as StaleRead");
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External), "a dry run must not write");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row N: a StaleRead refusal is non-latching, and a re-read (RecordObserved of the fresh text) lets the retry through.
    [Test]
    public async Task Member_AfterStaleRefusal_SessionNotHalted_AndRetryAfterReadSucceedsAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, External);
            var tools = BuildStructuralTools(manager);

            var refused = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");

            AssertStaleRead(refused, Path.GetFileName(file));
            Assert.That(manager.IsSessionHalted(), Is.False, "a StaleRead refusal must not latch the session");

            manager.RecordObserved(new FilePathWrapper(file), External);
            SyncWorkspaceText(manager, file, External);
            var retry = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");

            Assert.That(retry.IsError, Is.False, retry.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row K (helper path): the writer's own successful write refreshes its observed entry, so a second edit needs no re-read.
    [Test]
    public async Task Member_TwoEditsAfterOneRead_SecondEditWithoutReReadSucceedsAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            var tools = BuildStructuralTools(manager);

            var first = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");
            Assert.That(first.IsError, Is.False, first.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));

            var second = await tools.MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 3;");

            Assert.That(second.IsError, Is.False, second.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row K (helper path, anchor tool first): ReplaceSnippet's own write refreshes the entry that the Member edit then checks.
    [Test]
    public async Task ReplaceSnippet_ThenMember_AfterOneRead_MemberEditSucceedsAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            var workspaceTools = TestTools.WorkspaceTools(manager);

            var snippet = await workspaceTools.ReplaceSnippetTool(
                reason: "test message", action: ProposedChangeAction.apply, filePath: file,
                oldContent: "=> 1;", newContent: "=> 2;", validateOnApply: false);
            Assert.That(snippet.IsError, Is.False, snippet.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));

            var member = await BuildStructuralTools(manager).MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 3;");

            Assert.That(member.IsError, Is.False, member.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Row K (helper path, engine tool first): Member's own write refreshes the entry that the ReplaceSnippet edit then checks.
    [Test]
    public async Task Member_ThenReplaceSnippet_AfterOneRead_ReplaceSnippetSucceedsAsync()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager(file, Original);
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);

            var member = await BuildStructuralTools(manager).MemberTool(
                reason: "test message", operation: MemberAction.replace, filePath: file,
                memberName: "X", newMemberSource: "public int X() => 2;");
            Assert.That(member.IsError, Is.False, member.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Edited));

            var snippet = await TestTools.WorkspaceTools(manager).ReplaceSnippetTool(
                reason: "test message", action: ProposedChangeAction.apply, filePath: file,
                oldContent: "=> 2;", newContent: "=> 3;", validateOnApply: false);

            Assert.That(snippet.IsError, Is.False, snippet.ErrorData?.Message);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(External));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
