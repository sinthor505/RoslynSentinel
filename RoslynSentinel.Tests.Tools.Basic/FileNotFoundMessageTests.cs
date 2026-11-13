using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// One test per branch of <see cref="FileNotFoundMessage.Build"/>: directory, project file, file on disk outside the
/// loaded solution (with and without a LoadSolution target above it), loaded same-name candidates, nothing at all
/// (ListSolutionItems callable or not), and a non-rooted path that skips the disk checks.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileNotFoundMessageTests
{
    private static readonly IReadOnlyList<FileCandidate> NoCandidates = [];

    private string _root = "";

    [SetUp]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "fnfm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeFile(params string[] relativeParts)
    {
        var path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "class C { }");
        return path;
    }

    [Test]
    public void Build_DirectoryPassed_SaysDirectoryAndOffersListing()
    {
        var directory = Path.Combine(_root, "src");
        Directory.CreateDirectory(directory);

        var text = FileNotFoundMessage.Build(directory, NoCandidates, 2);

        Assert.That(text, Does.Contain("is a directory, not a file"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all"))));
        Assert.That(text, Does.Not.Contain("was not found anywhere"));
    }

    [Test]
    public void Build_DirectoryPassedAndListingNotCallable_NamesNoListingTool()
    {
        var directory = Path.Combine(_root, "src");
        Directory.CreateDirectory(directory);

        var text = FileNotFoundMessage.Build(directory, NoCandidates, 2, name => name == "ReadFile");

        Assert.That(text, Does.Contain("is a directory, not a file"));
        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
    }

    [Test]
    public void Build_ProjectFilePassed_SaysProjectFileNotSourceDocument()
    {
        var project = MakeFile("App", "App.csproj");

        var text = FileNotFoundMessage.Build(project, NoCandidates, 2);

        Assert.That(text, Does.Contain("is a project/solution file, not a source document"));
        Assert.That(text, Does.Not.Contain("outside the loaded solution"));
    }

    [Test]
    public void Build_FileOutsideSolutionWithSolutionFileAbove_NamesSolutionAsLoadTarget()
    {
        MakeFile("Sol", "A.csproj"); // a .csproj beside the .slnx must lose to the solution file
        var solution = MakeFile("Sol", "B.slnx");
        var source = MakeFile("Sol", "src", "Thing.cs");

        var text = FileNotFoundMessage.Build(source, NoCandidates, 2);

        Assert.That(text, Does.Contain("exists on disk but is outside the loaded solution"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("LoadSolution", ("solutionPath", solution))));
        Assert.That(text, Does.Contain("replaces the currently loaded solution"));
    }

    [Test]
    public void Build_FileOutsideSolutionWithOnlyCsprojAbove_NamesTheProject()
    {
        var project = MakeFile("Proj", "Proj.csproj");
        var source = MakeFile("Proj", "sub", "Thing.cs");

        var text = FileNotFoundMessage.Build(source, NoCandidates, 2);

        Assert.That(text, Does.Contain("outside the loaded solution"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("LoadSolution", ("solutionPath", project))));
    }

    [Test]
    public void Build_FileOutsideSolutionWithNothingAbove_StillSaysOutsideTheSolution()
    {
        // The temp folder has no project above it, but the walk-up may reach real ancestors, so only the common sentence is asserted.
        var source = MakeFile("Lonely", "Thing.cs");

        var text = FileNotFoundMessage.Build(source, NoCandidates, 2);

        Assert.That(text, Does.Contain("outside the loaded solution"));
        Assert.That(text, Does.Not.Contain("was not found anywhere"));
    }

    [Test]
    public void Build_FileOutsideSolutionAndLoadSolutionNotCallable_OmitsTheLoadCall()
    {
        MakeFile("Sol", "B.sln");
        var source = MakeFile("Sol", "Thing.cs");

        var text = FileNotFoundMessage.Build(source, NoCandidates, 2, name => name == "ReadFile");

        Assert.That(text, Does.Contain("outside the loaded solution"));
        Assert.That(text, Does.Not.Contain("LoadSolution"));
    }

    [Test]
    public void Build_FileOutsideSolutionWithSameNameCandidate_AppendsTheCandidateList()
    {
        MakeFile("Sol", "B.sln");
        var source = MakeFile("Sol", "Thing.cs");
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(@"C:\loaded\src\Thing.cs", 0, true)];

        var text = FileNotFoundMessage.Build(source, candidates, 2);

        Assert.That(text, Does.Contain("outside the loaded solution"));
        Assert.That(text, Does.Contain(@"  - C:\loaded\src\Thing.cs"));
    }

    [Test]
    public void Build_MissingFileWithCandidates_UsesTheDifferentPathWording()
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");
        IReadOnlyList<FileCandidate> candidates =
        [
            new FileCandidate(@"C:\loaded\a\Thing.cs", 0, true),
            new FileCandidate(@"C:\loaded\b\Thing.cs", 0, true),
        ];

        var text = FileNotFoundMessage.Build(missing, candidates, 3);

        Assert.That(text, Does.Contain($"File '{missing}' was not found in the loaded solution (searched 3 project(s)"));
        Assert.That(text, Does.Contain("A file with the same name exists at a different path. Did you mean one of these?"));
        Assert.That(text, Does.Contain("  - " + @"C:\loaded\a\Thing.cs" + " -> " + ToolCallHint.Call("ReadFile", ("filePath", @"C:\loaded\a\Thing.cs"))));
        Assert.That(text, Does.Contain("  - " + @"C:\loaded\b\Thing.cs" + " -> " + ToolCallHint.Call("ReadFile", ("filePath", @"C:\loaded\b\Thing.cs"))));
        Assert.That(text, Does.Contain("Retry with the exact path of the file you meant."));
        Assert.That(text, Does.Contain("it does not exist in this solution."));
        Assert.That(text, Does.Not.Contain("tell the user"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public void Build_MissingFileNoCandidatesListingCallable_OffersListSolutionItems()
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");

        var text = FileNotFoundMessage.Build(missing, NoCandidates, 3, name => name == "ListSolutionItems");

        Assert.That(text, Does.Contain("was not found in the loaded solution (searched 3 project(s) by exact path"));
        Assert.That(text, Does.Contain("No file in the solution has a similar name"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all")) + " lists every file in the solution."));
        Assert.That(text, Does.Contain("A file not in that list does not exist in this solution."));
        Assert.That(text, Does.Contain("may since have been renamed or moved"));
        Assert.That(text, Does.Not.Contain("CreateFile"), "CreateFile is never offered in the no-candidate text");
        Assert.That(text, Does.Not.Contain("tell the user"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public void Build_MissingFileNoCandidatesListingNotCallable_NamesNoListingTool()
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");

        var text = FileNotFoundMessage.Build(missing, NoCandidates, 3, name => name is "ReadFile" or "GetLargeResult");

        Assert.That(text, Does.Contain("was not found in the loaded solution"));
        Assert.That(text, Does.Contain("No file in the solution has a similar name"));
        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
        Assert.That(text, Does.Contain("may since have been renamed or moved"));
        Assert.That(text, Does.Not.Contain("CreateFile"), "CreateFile is never offered in the no-candidate text");
        Assert.That(text, Does.Not.Contain("tell the user"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    // Builds the no-candidate text with the ambient ToolCallContext carrying the given WorkspaceCurrent (null = no context at all). The slot is
    // an AsyncLocal that other tests may set, so it is always cleared afterwards.
    private string BuildNoCandidateText(bool? workspaceCurrent, Func<string, bool>? isCallable)
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");
        try
        {
            ToolCallContext.Set(workspaceCurrent is null
                ? null
                : new ToolCallContext(null, null, "ReadFile", null, 0) { WorkspaceCurrent = workspaceCurrent, SolutionRoot = _root });
            return FileNotFoundMessage.Build(missing, NoCandidates, 3, isCallable);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Build_MissingFileNoCandidatesWorkspaceCurrent_LeadsWithTheWellFormedFactAndKeepsSearchFacts()
    {
        var text = BuildNoCandidateText(workspaceCurrent: true, name => name == "ListSolutionItems");
        var relative = Path.Combine("NoSuchDir", "Thing.cs");

        Assert.That(text, Does.StartWith($"Your tool call and file path were well-formed, but file `{relative}` does not exist in the solution. "));
        Assert.That(text, Does.EndWith("Searched 3 project(s) by exact path, bare file name and similar file name; path comparison ignores case, so casing is not the cause; no file in the solution has a similar name."));
        Assert.That(text, Does.Not.Contain(_root), "the requested path is shown solution-relative");
        Assert.That(text, Does.Not.Contain("renamed or moved"));
        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
        Assert.That(text, Does.Not.Contain("lists every file"));
        Assert.That(text, Does.Not.Contain("No file with this name or a similar name exists in this solution."));
        Assert.That(text, Does.Not.Contain("was not found in the loaded solution"));
    }

    [Test]
    public void Build_MissingFileNoCandidatesWorkspaceCurrentWithDeclaringType_PutsTheDeclaringSentenceAfterTheLead()
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");
        try
        {
            ToolCallContext.Set(new ToolCallContext(null, null, "ReadFile", null, 0) { WorkspaceCurrent = true, SolutionRoot = _root });
            var text = FileNotFoundMessage.Build(missing, NoCandidates, 3, declaringTypeFiles: [("Thing", "Core/Other.cs")]);

            Assert.That(text, Does.StartWith("Your tool call and file path were well-formed, but file `"));
            Assert.That(text, Does.Contain("does not exist in the solution. Type 'Thing' is declared in Core/Other.cs. Searched 3 project(s)"));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Build_MissingFileNoCandidatesWorkspaceCurrentWithRecordedFileDelete_KeepsTheEditEffectFact()
    {
        var missing = Path.Combine(_root, "NoSuchDir", "Thing.cs");
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("agent-1", null, "DeleteFile", "delete", 0) { SessionId = "s1" };
        writer.AssignToolCallId("d41f7a02");
        writer.RecordEffect(EditEffectKind.FileDeleted, "NoSuchDir/Thing.cs", null, "Thing", null, null, "NoSuchDir/Thing.cs");
        writer.CommitPendingEffects(index);
        try
        {
            ToolCallContext.Set(new ToolCallContext("agent-1", null, "ReadFile", "look", 0)
            {
                SessionId = "s1",
                TargetFilePath = missing,
                EditEffects = index,
                WorkspaceCurrent = true,
                SolutionRoot = _root,
            });
            var text = FileNotFoundMessage.Build(missing, NoCandidates, 3);

            Assert.That(text, Does.StartWith("Your tool call and file path were well-formed, but file `"));
            Assert.That(text, Does.Contain("no file in the solution has a similar name. Call `d41f7a02` (`DeleteFile`, this agent, "));
            Assert.That(text, Does.EndWith(" ago) deleted `NoSuchDir/Thing.cs`."));
            Assert.That(text, Does.Not.Contain("renamed or moved"));
            Assert.That(ToolCallContext.Current!.HasEditEffectFact, Is.True);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public void Build_MissingFileNoCandidatesWorkspaceStale_TextEqualsUnknownStateText()
    {
        var stale = BuildNoCandidateText(workspaceCurrent: false, name => name == "ListSolutionItems");
        var unknown = BuildNoCandidateText(workspaceCurrent: null, name => name == "ListSolutionItems");

        Assert.That(stale, Is.EqualTo(unknown));
        Assert.That(stale, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all")) + " lists every file in the solution."));
        Assert.That(stale, Does.Contain("A file not in that list does not exist in this solution."));
    }

    [Test]
    public void Build_MissingFileNoCandidatesListingNotCallable_OnlyTheCurrentWorkspaceTextDiffers()
    {
        var current = BuildNoCandidateText(workspaceCurrent: true, name => name is "ReadFile" or "GetLargeResult");
        var stale = BuildNoCandidateText(workspaceCurrent: false, name => name is "ReadFile" or "GetLargeResult");
        var unknown = BuildNoCandidateText(workspaceCurrent: null, name => name is "ReadFile" or "GetLargeResult");

        Assert.That(current, Is.Not.EqualTo(unknown));
        Assert.That(current, Does.StartWith("Your tool call and file path were well-formed"));
        Assert.That(unknown, Does.StartWith("File '"));
        Assert.That(unknown, Does.EndWith("may since have been renamed or moved."));
        Assert.That(stale, Is.EqualTo(unknown));
        Assert.That(unknown, Does.Not.Contain("ListSolutionItems"));
        Assert.That(current, Does.Not.Contain("ListSolutionItems"));
    }

    [Test]
    public void Build_NonRootedPath_SkipsTheDiskBranches()
    {
        // "." and ".." exist relative to any working directory; a non-rooted path must not be probed, so they are reported as not found
        // (the process working directory is deliberately left alone: tests run in parallel).
        foreach (var relative in new[] { ".", ".." })
        {
            var text = FileNotFoundMessage.Build(relative, NoCandidates, 1, _ => true);

            Assert.That(text, Does.Contain("was not found in the loaded solution"), relative);
            Assert.That(text, Does.Not.Contain("is a directory"), relative);
        }
    }
}
