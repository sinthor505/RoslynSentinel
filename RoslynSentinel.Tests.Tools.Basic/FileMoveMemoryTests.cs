using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// <see cref="FileMoveMemory"/> (record, chain, case, clear) and the moved-path branch of the candidate overload of
/// <see cref="FileNotFoundMessage.Build"/> (root-cause fix 4, eval run 20261112-151531-550 T8-T12). The pins at the end hold the text of
/// every other branch exactly as it was: another session compares that text in an A/B baseline.
/// </summary>
[TestFixture]
// Not parallel: these tests use FileMoveMemory.Shared, which every LoadSolutionAsync in another fixture clears (a record would vanish mid-test).
[NonParallelizable]
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileMoveMemoryTests
{
    private static readonly IReadOnlyList<FileCandidate> NoCandidates = [];

    // A rooted directory that does not exist, so no disk branch fires. Unique per run: FileMoveMemory.Shared is process-wide.
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "fmm_" + Guid.NewGuid().ToString("N"), "src");

    private static string P(string name) => Path.Combine(Dir, name);

    // ── FileMoveMemory ────────────────────────────────────────────────────────

    [Test]
    public void Record_ThenTryGetCurrentPath_ReturnsNewPathAndTool()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("Calc.cs"), P("StandardCalc.cs"), "RenameSymbol");

        var found = memory.TryGetCurrentPath(P("Calc.cs"), out var current, out var tool);

        Assert.That(found, Is.True);
        Assert.That(current, Is.EqualTo(P("StandardCalc.cs")));
        Assert.That(tool, Is.EqualTo("RenameSymbol"));
    }

    [Test]
    public void TryGetCurrentPath_UnknownPathOrNewPath_ReturnsFalse()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("Calc.cs"), P("StandardCalc.cs"), "RenameSymbol");

        Assert.Multiple(() =>
        {
            Assert.That(memory.TryGetCurrentPath(P("Other.cs"), out _, out _), Is.False);
            Assert.That(memory.TryGetCurrentPath(P("StandardCalc.cs"), out _, out _), Is.False, "the new path was not moved");
            Assert.That(memory.TryGetCurrentPath("", out _, out _), Is.False);
        });
    }

    [Test]
    public void TryGetCurrentPath_Chain_FollowsToTheEndAndNamesTheFirstTool()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("A.cs"), P("B.cs"), "RenameSymbol");
        memory.Record(P("B.cs"), P("C.cs"), "SyncTypeAndFilename");

        var fromA = memory.TryGetCurrentPath(P("A.cs"), out var currentFromA, out var toolFromA);
        var fromB = memory.TryGetCurrentPath(P("B.cs"), out var currentFromB, out var toolFromB);

        Assert.Multiple(() =>
        {
            Assert.That(fromA, Is.True);
            Assert.That(currentFromA, Is.EqualTo(P("C.cs")));
            Assert.That(toolFromA, Is.EqualTo("RenameSymbol"));
            Assert.That(fromB, Is.True);
            Assert.That(currentFromB, Is.EqualTo(P("C.cs")));
            Assert.That(toolFromB, Is.EqualTo("SyncTypeAndFilename"));
        });
    }

    [Test]
    public void Record_MoveBackToAnEarlierPath_DoesNotLoop()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("A.cs"), P("B.cs"), "RenameSymbol");
        memory.Record(P("B.cs"), P("A.cs"), "RenameSymbol");

        Assert.Multiple(() =>
        {
            Assert.That(memory.TryGetCurrentPath(P("A.cs"), out _, out _), Is.False, "the file is at A again");
            Assert.That(memory.TryGetCurrentPath(P("B.cs"), out var current, out _), Is.True);
            Assert.That(current, Is.EqualTo(P("A.cs")));
        });
    }

    [Test]
    public void TryGetCurrentPath_IgnoresCase()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("Calc.cs"), P("StandardCalc.cs"), "RenameSymbol");

        var found = memory.TryGetCurrentPath(P("CALC.CS").ToUpperInvariant(), out var current, out _);

        Assert.That(found, Is.True);
        Assert.That(current, Is.EqualTo(P("StandardCalc.cs")).IgnoreCase);
    }

    [Test]
    public void Clear_ForgetsEveryMove()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("Calc.cs"), P("StandardCalc.cs"), "RenameSymbol");

        memory.Clear();

        Assert.That(memory.TryGetCurrentPath(P("Calc.cs"), out _, out _), Is.False);
    }

    [Test]
    public void Record_SamePathOrBlank_IsIgnored()
    {
        var memory = new FileMoveMemory();
        memory.Record(P("Calc.cs"), P("CALC.cs"), "RenameSymbol");
        memory.Record("", P("X.cs"), "RenameSymbol");

        Assert.Multiple(() =>
        {
            Assert.That(memory.TryGetCurrentPath(P("Calc.cs"), out _, out _), Is.False);
            Assert.That(memory.TryGetCurrentPath(P("X.cs"), out _, out _), Is.False);
        });
    }

    // Cleared on a (re)load: LoadSolutionAsync calls FileMoveMemory.Shared.Clear(); the contract it relies on is Clear on the shared instance.
    [Test]
    public void Shared_IsOneProcessWideInstance_AndClearEmptiesIt()
    {
        var oldPath = P("SharedOld_" + Guid.NewGuid().ToString("N") + ".cs");
        FileMoveMemory.Shared.Record(oldPath, P("SharedNew.cs"), "RenameSymbol");
        Assert.That(FileMoveMemory.Shared.TryGetCurrentPath(oldPath, out _, out _), Is.True);

        FileMoveMemory.Shared.Clear();

        Assert.That(FileMoveMemory.Shared.TryGetCurrentPath(oldPath, out _, out _), Is.False);
    }

    // ── DocumentLookup.FindMovedTo (the new path must still be a loaded document) ──

    private static Solution SolutionWithDocument(string filePath)
    {
        var project = new AdhocWorkspace().AddProject("P", LanguageNames.CSharp);
        return project.AddDocument(Path.GetFileName(filePath), SourceText.From("public class X { }"), filePath: filePath).Project.Solution;
    }

    [Test]
    public void FindMovedTo_NewPathIsALoadedDocument_ReturnsItWithTheTool()
    {
        var oldPath = P("MovedOld_" + Guid.NewGuid().ToString("N") + ".cs");
        var newPath = P("MovedNew_" + Guid.NewGuid().ToString("N") + ".cs");
        FileMoveMemory.Shared.Record(oldPath, newPath, "RenameSymbol");

        var moved = DocumentLookup.FindMovedTo(SolutionWithDocument(newPath), oldPath);

        Assert.That(moved, Is.Not.Null);
        Assert.That(moved!.Value.NewPath, Is.EqualTo(newPath));
        Assert.That(moved.Value.ToolName, Is.EqualTo("RenameSymbol"));
    }

    [Test]
    public void FindMovedTo_NewPathIsNotALoadedDocument_ReturnsNull()
    {
        var oldPath = P("GoneOld_" + Guid.NewGuid().ToString("N") + ".cs");
        var newPath = P("GoneNew_" + Guid.NewGuid().ToString("N") + ".cs");
        FileMoveMemory.Shared.Record(oldPath, newPath, "RenameSymbol");

        var moved = DocumentLookup.FindMovedTo(SolutionWithDocument(P("Unrelated.cs")), oldPath);

        Assert.That(moved, Is.Null, "a stale record must not send the caller to a file that is gone");
    }

    [Test]
    public void TryGetDocument_MovedPath_DescribeNamesTheNewPath()
    {
        var oldPath = P("LookupOld_" + Guid.NewGuid().ToString("N") + ".cs");
        var newPath = P("LookupNew_" + Guid.NewGuid().ToString("N") + ".cs");
        FileMoveMemory.Shared.Record(oldPath, newPath, "SyncTypeAndFilename");

        var result = DocumentLookup.TryGetDocument(SolutionWithDocument(newPath), new FilePathWrapper(oldPath));

        Assert.That(result.IsFound, Is.False);
        Assert.That(result.Describe(), Does.Contain($"was moved to '{newPath}' by an earlier SyncTypeAndFilename call in this session"));
    }

    // ── FileNotFoundMessage.Build, moved-path branch ─────────────────────────

    [Test]
    public void Build_MovedPath_NamesNewPathToolAndReadFileCall()
    {
        var text = FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3, movedToPath: P("StandardCalc.cs"), movedByTool: "RenameSymbol");

        Assert.That(text, Is.EqualTo(
            $"File '{P("Calc.cs")}' was moved to '{P("StandardCalc.cs")}' by an earlier RenameSymbol call in this session (a rename of the type it declares). The file is now at '{P("StandardCalc.cs")}'.\n" +
            $"  - {P("StandardCalc.cs")} -> {ToolCallHint.Call("ReadFile", ("filePath", P("StandardCalc.cs")))}"));
    }

    [Test]
    public void Build_MovedPathReadFileNotCallable_ListsThePathWithoutAReadFileCall()
    {
        var text = FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3, name => name != "ReadFile", movedToPath: P("StandardCalc.cs"), movedByTool: "RenameSymbol");

        Assert.That(text, Does.EndWith($"The file is now at '{P("StandardCalc.cs")}'.\n  - {P("StandardCalc.cs")}"));
        Assert.That(text, Does.Not.Contain("Use the new path"));
        Assert.That(text, Does.Not.Contain("ReadFile"));
    }

    [Test]
    public void Build_MovedPathByAnotherTool_OmitsTheRenameParenthetical()
    {
        var text = FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3, movedToPath: P("StandardCalc.cs"), movedByTool: "SyncTypeAndFilename");

        Assert.That(text, Does.Contain($"by an earlier SyncTypeAndFilename call in this session. The file is now at '{P("StandardCalc.cs")}'."));
        Assert.That(text, Does.Not.Contain("Use the new path"));
        Assert.That(text, Does.Not.Contain("a rename of the type it declares"));
    }

    [Test]
    public void Build_MovedPath_DropsTheHedgeAndTheDidYouMeanList()
    {
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(P("Similar.cs"), 1, false)];

        var text = FileNotFoundMessage.Build(P("Calc.cs"), candidates, 3, movedToPath: P("StandardCalc.cs"), movedByTool: "RenameSymbol");

        Assert.That(text, Does.Not.Contain("Did you mean"));
        Assert.That(text, Does.Not.Contain("find its current path"));
        Assert.That(text, Does.Not.Contain("Similar.cs"));
    }

    [Test]
    public void Build_NotMoved_NoCandidates_TextIsUnchanged()
    {
        var text = FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3);

        Assert.That(text, Is.EqualTo(
            $"File '{P("Calc.cs")}' was not found in the loaded solution (searched 3 project(s) by exact path, bare file name and similar file name; path comparison ignores case, so casing is not the cause). " +
            "No file in the solution has a similar name. " +
            $"{ToolCallHint.Call("CreateFile", ("filePath", P("Calc.cs")))} creates a new file at this path. " +
            "A file read earlier under this name may since have been renamed or moved. " +
            $"{ToolCallHint.Call("ListSolutionItems", ("kind", "all"))} lists every file in the solution. A file not in that list does not exist in this solution."));
    }

    [Test]
    public void Build_NotMoved_WithCandidate_TextIsUnchanged()
    {
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(P("Similar.cs"), 1, false)];

        var text = FileNotFoundMessage.Build(P("Calc.cs"), candidates, 2);

        Assert.That(text, Is.EqualTo(
            $"File '{P("Calc.cs")}' was not found in the loaded solution (searched 2 project(s) by exact path, bare file name and similar file name; path comparison ignores case, so casing is not the cause). " +
            "Did you mean one of these?" +
            $"\n  - {P("Similar.cs")} -> {ToolCallHint.Call("ReadFile", ("filePath", P("Similar.cs")))}" +
            "\nRetry with the exact path of the file you meant. If none of these is the file you meant, it does not exist in this solution."));
    }

    [Test]
    public void Build_OnlyOneOfMovedPathAndToolGiven_TextIsUnchanged()
    {
        var plain = FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3);

        Assert.Multiple(() =>
        {
            Assert.That(FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3, movedToPath: P("StandardCalc.cs")), Is.EqualTo(plain));
            Assert.That(FileNotFoundMessage.Build(P("Calc.cs"), NoCandidates, 3, movedByTool: "RenameSymbol"), Is.EqualTo(plain));
        });
    }
}
