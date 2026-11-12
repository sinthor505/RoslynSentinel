using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// Miss text of WorkspaceProjectManagementImpl (ListSolutionItems kind: files project miss, ListWorkspaceSolutions directory miss) and
// WorkspaceFileEditImpl (ReadFile of an absolute path with no solution loaded), moved onto facts and options only
// (plan_not_found_error_orientation, Step 5e slice 5). Each producer is driven through the tool that hosts it. The in-memory workspace
// names its only project "InMemoryProject", so the candidate case misspells that name.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class WorkspaceLookupMissTests
{
    private const string FileName = "Types.cs";

    private const string Source = "public class Alpha\r\n{\r\n}\r\n";

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // ---- ListSolutionItems(kind: files, projectName) ----

    private static async Task<string> ListFilesMissAsync(string projectName)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Source));
        var before = workspace.ReadText(FileName);
        var r = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).ListSolutionItemsTool(reason: "test message", SolutionItemsKind.files, projectName);
        Assert.That(r.IsError, Is.True, r.ErrorData?.Message);
        Assert.That(r.ErrorData!.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), r.ErrorData.Message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused read must not write the file.");
        return r.ErrorData.Message;
    }

    [Test]
    [Category("WorkspaceProjectManagementImpl")]
    public async Task ListSolutionItemsFiles_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await ListFilesMissAsync("InMemoryProjct");

        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with projectName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Contain("Project 'InMemoryProjct' was not found among the 1 loaded project(s)"), "The wanted name is echoed.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("WorkspaceProjectManagementImpl")]
    public async Task ListSolutionItemsFiles_NoSimilarProject_StatesTheNoCandidateFactAndListsProjectsAsync()
    {
        var message = await ListFilesMissAsync("Zzz.Missing");

        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- ListWorkspaceSolutions(workspacePath): a state-only miss (fact of what exists at the path) ----

    [Test]
    [Category("WorkspaceProjectManagementImpl")]
    public void ListWorkspaceSolutions_MissingDirectory_StatesNothingExistsAtThePath()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Source));
        var missing = Path.Combine(Path.GetTempPath(), "WorkspaceLookupMiss_" + Guid.NewGuid().ToString("n"));

        var r = Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).ListWorkspaceSolutionsTool(reason: "test message", missing);

        Assert.That(r.IsError, Is.True);
        // TODO the code is still InvalidArgument (a missing directory is a NotFound); the text is what this slice changed.
        Assert.That(r.ErrorData!.ErrorCode.ToString(), Is.EqualTo("InvalidArgument"));
        Assert.That(r.ErrorData.Message, Does.Contain("is not an existing directory (nothing exists at that path)."));
        Assert.That(r.ErrorData.Message, Does.Not.Contain("Directory not found"));
        AssertFactsAndOptionsOnly(r.ErrorData.Message);
        Assert.That(Directory.Exists(missing), Is.False, "A refused call must not create the directory.");
    }

    [Test]
    [Category("WorkspaceProjectManagementImpl")]
    public void ListWorkspaceSolutions_PathIsAFile_StatesItIsAFile()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Source));
        var file = Path.Combine(Path.GetTempPath(), "WorkspaceLookupMiss_" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(file, "x");
        try
        {
            var r = Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).ListWorkspaceSolutionsTool(reason: "test message", file);

            Assert.That(r.IsError, Is.True);
            Assert.That(r.ErrorData!.Message, Does.Contain("is not an existing directory"));
            Assert.That(r.ErrorData.Message, Does.Contain("is a file)."));
            AssertFactsAndOptionsOnly(r.ErrorData.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- ReadFile of an absolute path with no solution loaded: a state-only miss (what was checked, and the LoadSolution option) ----

    [Test]
    [Category("WorkspaceFileEditImpl")]
    public async Task ReadFile_NoSolutionLoadedAndMissingFile_StatesOnlyTheExactPathWasCheckedAndNamesLoadSolutionAsync()
    {
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var missing = Path.Combine(Path.GetTempPath(), "WorkspaceLookupMiss_" + Guid.NewGuid().ToString("n"), "Missing.cs");

        var r = await Battery.Basic.TestTools.WorkspaceTools(manager).ReadFileTool(reason: "test message", missing);

        Assert.That(r.IsError, Is.True);
        Assert.That(r.ErrorData!.ErrorCode.ToString(), Is.EqualTo("FileNotFound"));
        Assert.That(r.ErrorData.Message, Does.Contain($"'Missing.cs' does not exist at '{missing}'"));
        Assert.That(r.ErrorData.Message, Does.Contain("only that exact path was checked"));
        Assert.That(r.ErrorData.Message, Does.Contain("no search by file name or similar name ran"));
        Assert.That(r.ErrorData.Message, Does.Contain("No solution is loaded"));
        Assert.That(r.ErrorData.Message, Does.Contain("LoadSolution"), "The option to load a solution is named.");
        AssertFactsAndOptionsOnly(r.ErrorData.Message);
        Assert.That(File.Exists(missing), Is.False, "A refused read must not create the file.");
    }
}
