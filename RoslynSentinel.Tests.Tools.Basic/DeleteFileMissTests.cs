using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Miss text of DeleteFile (WholeFileWriteTools): with a solution loaded the target is compared with the loaded documents that exist on
// disk (similar names, each with a prefilled DeleteFile retry, or the no-similar-name fact); with no solution loaded the path is never
// resolved, which the text says. The Search option is named in both. A refused DeleteFile must leave every file on disk
// (plan_not_found_error_orientation, Step 5e slice 5). Runs on a real PersistentWorkspaceManager over a temp directory.
[TestFixture]
[Category("WholeFileWriteTools")]
public sealed class DeleteFileMissTests
{
    private const string Source = "namespace T;\r\npublic class OrderService\r\n{\r\n}\r\n";

    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DeleteFileMiss", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    // A solution whose one document is a real file in the temp directory, so the candidate filter (File.Exists) accepts it.
    private PersistentWorkspaceManager NewManagerWithDocument(string file)
    {
        File.WriteAllText(file, Source);
        var project = new AdhocWorkspace().AddProject("DeleteFileMiss", LanguageNames.CSharp);
        var solution = project.AddDocument(Path.GetFileName(file), SourceText.From(Source), filePath: file).Project.Solution;
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.SetTestSolution(solution);
        return manager;
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager) =>
        Battery.Basic.TestTools.WholeFileWriteTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager));

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("does not exist. "), "The old sentence ended before any fact about what was searched.");
    }

    private static string SearchOption() => ToolCallHint.Next("Search", "to find the right path.", ("mode", "text"));

    [Test]
    public async Task DeleteFile_MisspeltName_OffersTheNearExistingFileWithAPrefilledDeleteFileCallAndDeletesNothingAsync()
    {
        var file = Path.Combine(_dir, "OrderService.cs");
        using var manager = NewManagerWithDocument(file);
        var requested = Path.Combine(_dir, "OrderServise.cs");

        var result = await BuildTools(manager).DeleteFileTool(reason: "test message", filePath: requested);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain($"DeleteFile: '{requested}' does not exist on disk."));
        Assert.That(message, Does.Contain("Searched 1 loaded project(s) by exact path, bare file name and similar file name"));
        Assert.That(message, Does.Contain($"Did you mean '{file}'? Retry with {ToolCallHint.Call("DeleteFile", ("filePath", file))}."));
        Assert.That(message, Does.Contain(SearchOption()), "The Search option stays.");
        AssertFactsAndOptionsOnly(message);
        Assert.That(File.Exists(file), Is.True, "A refused DeleteFile must delete nothing.");
        Assert.That(Directory.GetFiles(_dir), Is.EqualTo(new[] { file }));
    }

    [Test]
    public async Task DeleteFile_NoSimilarName_StatesTheNoCandidateFactAndKeepsTheSearchOptionAsync()
    {
        var file = Path.Combine(_dir, "OrderService.cs");
        using var manager = NewManagerWithDocument(file);
        var requested = Path.Combine(_dir, "Zzz.cs");

        var result = await BuildTools(manager).DeleteFileTool(reason: "test message", filePath: requested);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain($"DeleteFile: '{requested}' does not exist on disk."));
        Assert.That(message, Does.Contain("No existing file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain(SearchOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(File.Exists(file), Is.True, "A refused DeleteFile must delete nothing.");
    }

    [Test]
    public async Task DeleteFile_TargetIsADirectory_StatesItIsADirectoryAsync()
    {
        var file = Path.Combine(_dir, "OrderService.cs");
        using var manager = NewManagerWithDocument(file);
        var sub = Path.Combine(_dir, "Models");
        Directory.CreateDirectory(sub);

        var result = await BuildTools(manager).DeleteFileTool(reason: "test message", filePath: sub);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain($"DeleteFile: '{sub}' is a directory, not a file; this tool deletes single files."));
        AssertFactsAndOptionsOnly(result.ErrorData.Message);
        Assert.That(Directory.Exists(sub), Is.True, "A refused DeleteFile must delete nothing.");
        Assert.That(File.Exists(file), Is.True);
    }

    // State-only miss: no solution is loaded, so the wire path is never resolved and no file is checked, even one that exists.
    [Test]
    public async Task DeleteFile_NoSolutionLoaded_StatesNothingWasResolvedAndKeepsTheSearchOptionAsync()
    {
        var existing = Path.Combine(_dir, "OrderService.cs");
        File.WriteAllText(existing, Source);
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var result = await BuildTools(manager).DeleteFileTool(reason: "test message", filePath: existing);

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain($"DeleteFile: no solution is loaded, so '{existing}' was not resolved to a path and no file was checked."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain(SearchOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(File.Exists(existing), Is.True, "A refused DeleteFile must delete nothing.");
    }
}
