using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Eval run 113 (Model_AppliesTenChainedRefactors T15): after RenameSymbol renamed class OrderPricingCalculator, the model read the
// new-stem file, which did not exist. RenameSymbol now moves the declaration file along when it renames a top-level type whose file
// stem equals the old type name, in the same apply as the symbol rename. Members, nested types and files with another stem stay put;
// a taken target name leaves the file alone and the response says why. Runs on a real PersistentWorkspaceManager with real files.
[TestFixture]
// Not parallel: the move-record tests below read FileMoveMemory.Shared, which every LoadSolutionAsync in another fixture clears.
[NonParallelizable]
[Category("RefactoringSignatureTools")]
public class RenameSymbolRenamesFileTests
{
    private const string ProjectName = "RenameFile";

    private sealed class Harness : IDisposable
    {
        public string Dir { get; }

        public PersistentWorkspaceManager Manager { get; }

        public RefactoringSignatureTools Tools { get; }

        public Harness(params (string Name, string Text)[] files)
            : this(true, files)
        {
        }

        // projectHasFolder false leaves the project without a .csproj path, so a moved file's new path maps to no project.
        public Harness(bool projectHasFolder, params (string Name, string Text)[] files)
        {
            Dir = Path.Combine(Path.GetTempPath(), "RenameFile", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Dir);
            var project = new AdhocWorkspace().AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), ProjectName, ProjectName, LanguageNames.CSharp,
                filePath: projectHasFolder ? Path.Combine(Dir, ProjectName + ".csproj") : null));
            // A framework reference keeps the candidate compile free of environment errors, as in StaleReadEngineToolsTests.
            var solution = project.WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).Solution;
            foreach (var (name, text) in files)
            {
                var path = Path.Combine(Dir, name);
                File.WriteAllText(path, text);
                solution = solution.GetProject(project.Id)!.AddDocument(name, SourceText.From(text), filePath: path).Project.Solution;
            }

            Manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            Manager.EolModeOverride = EolMode.Crlf;
            Manager.SetTestSolution(solution);
            Tools = new RefactoringSignatureTools(TestTools.RefactoringSignatureImpl(Manager));
        }

        public string PathOf(string name) => Path.Combine(Dir, name);

        public int WorkspaceDocumentCount(string name) =>
            Manager.CurrentSolution!.GetDocumentIdsWithFilePath(new FilePathWrapper(PathOf(name))).Length;

        public Task<SentinelCallToolResult<object>> RenameAsync(string docCommentId, string newName) =>
            Tools.RenameSymbolTool(reason: "test message", projectName: ProjectName, docCommentId: docCommentId, newName: newName);

        public void Dispose()
        {
            Manager.Dispose();
            Directory.Delete(Dir, recursive: true);
        }
    }

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    // The type's file cannot be moved when the validation compile could not place the new path in the project: the rename must still
    // apply and the response must say why the file stayed.
    [Test]
    public async Task RenameSymbol_NewPathMapsToNoProject_RenamesTheSymbolKeepsTheFileAndNotesWhyAsync()
    {
        using var h = new Harness(false, ("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var json = Json(result);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Calc.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("StandardCalc.cs")), Is.False);
            Assert.That(File.ReadAllText(h.PathOf("Calc.cs")), Does.Contain("public class StandardCalc"));
            Assert.That(json.GetProperty("fileRenamed").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(json.GetProperty("fileRenameNote").GetString(), Does.Contain("not renamed").And.Contain("project"));
        });
    }

    // The old file is removed through deletePaths, so the stale-read guard must cover it: a file this agent read and that then changed
    // on disk must not be deleted by the rename.
    [Test]
    public async Task RenameSymbol_ObservedThenChangedFileThatWouldMove_RefusesWithStaleReadAndKeepsBothFilesAsync()
    {
        const string original = "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n";
        const string external = "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 3;\r\n}\r\n";
        using var h = new Harness(("Calc.cs", original));
        ToolCallContext.Set(new ToolCallContext("agentA", null, "RenameSymbol", null, long.MaxValue));
        h.Manager.RecordObserved(new FilePathWrapper(h.PathOf("Calc.cs")), original);
        File.WriteAllText(h.PathOf("Calc.cs"), external);

        var result = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(result.IsError, Is.True, "a stale file must not be deleted by the rename");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead), result.ErrorData.Message);
            Assert.That(File.ReadAllText(h.PathOf("Calc.cs")), Is.EqualTo(external), "the externally written text survives");
            Assert.That(File.Exists(h.PathOf("StandardCalc.cs")), Is.False);
        });
    }

    private static JsonElement Json(SentinelCallToolResult<object> result) =>
        JsonSerializer.SerializeToElement(result.SuccessData, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Test]
    public async Task RenameSymbol_TypeInSameNamedFile_RenamesTheFileAndUpdatesReferencesAsync()
    {
        using var h = new Harness(
            ("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n"),
            ("User.cs", "namespace T;\r\npublic class User\r\n{\r\n    public Calc Make() => new Calc();\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message + "\n" + result.ErrorData?.Detail + string.Join("\n", result.Findings?.Select(f => f.Message) ?? []));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Calc.cs")), Is.False, "the old file must be gone on disk");
            Assert.That(File.Exists(h.PathOf("StandardCalc.cs")), Is.True, "the new file must exist on disk");
            Assert.That(File.ReadAllText(h.PathOf("StandardCalc.cs")), Does.Contain("public class StandardCalc"));
            Assert.That(h.WorkspaceDocumentCount("Calc.cs"), Is.EqualTo(0), "the old path must be gone from the workspace");
            Assert.That(h.WorkspaceDocumentCount("StandardCalc.cs"), Is.EqualTo(1), "the new path must be in the workspace");
            Assert.That(File.ReadAllText(h.PathOf("User.cs")), Does.Contain("public StandardCalc Make() => new StandardCalc();"), "references are updated");
            var fileRenamed = Json(result).GetProperty("fileRenamed");
            Assert.That(fileRenamed.GetProperty("from").GetString(), Does.EndWith("Calc.cs"));
            Assert.That(fileRenamed.GetProperty("to").GetString(), Does.EndWith("StandardCalc.cs"));
        });
    }

    [Test]
    public async Task RenameSymbol_TypeInFileWithDifferentStem_LeavesTheFileAloneAsync()
    {
        using var h = new Harness(("Misc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Misc.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("StandardCalc.cs")), Is.False);
            Assert.That(File.ReadAllText(h.PathOf("Misc.cs")), Does.Contain("public class StandardCalc"));
            Assert.That(Json(result).GetProperty("fileRenamed").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task RenameSymbol_Member_LeavesTheFileAloneAsync()
    {
        using var h = new Harness(("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n"));

        var result = await h.RenameAsync("M:T.Calc.X", "Calc2");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Calc.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("Calc2.cs")), Is.False);
            Assert.That(File.ReadAllText(h.PathOf("Calc.cs")), Does.Contain("public int Calc2()"));
            Assert.That(Json(result).GetProperty("fileRenamed").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task RenameSymbol_NestedTypeWithFileNamedAfterIt_LeavesTheFileAloneAsync()
    {
        using var h = new Harness(("Inner.cs", "namespace T;\r\npublic class Outer\r\n{\r\n    public class Inner\r\n    {\r\n    }\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Outer.Inner", "Core");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Inner.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("Core.cs")), Is.False);
            Assert.That(File.ReadAllText(h.PathOf("Inner.cs")), Does.Contain("public class Core"));
        });
    }

    [Test]
    public async Task RenameSymbol_TargetFileAlreadyExists_RenamesTheSymbolKeepsTheFileAndNotesWhyAsync()
    {
        using var h = new Harness(
            ("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n}\r\n"),
            ("StandardCalc.cs", "namespace T;\r\npublic class Other\r\n{\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var json = Json(result);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Calc.cs")), Is.True, "the file stays");
            Assert.That(File.ReadAllText(h.PathOf("Calc.cs")), Does.Contain("public class StandardCalc"), "the symbol rename still applies");
            Assert.That(File.ReadAllText(h.PathOf("StandardCalc.cs")), Does.Contain("public class Other"), "the existing file is not overwritten");
            Assert.That(json.GetProperty("fileRenamed").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(json.GetProperty("fileRenameNote").GetString(), Does.Contain("already exists").And.Contain("Calc.cs"));
        });
    }

    [Test]
    public async Task RenameSymbol_GenericTypeInStemNamedFile_RenamesTheFileAsync()
    {
        using var h = new Harness(("Box.cs", "namespace T;\r\npublic class Box<TItem>\r\n{\r\n    public TItem? Value { get; set; }\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Box`1", "Crate");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Box.cs")), Is.False);
            Assert.That(File.Exists(h.PathOf("Crate.cs")), Is.True, "the new file name carries no arity suffix");
            Assert.That(File.ReadAllText(h.PathOf("Crate.cs")), Does.Contain("public class Crate<TItem>"));
        });
    }

    [Test]
    public async Task RenameSymbol_PartialType_RenamesOnlyTheMatchingStemFileAsync()
    {
        using var h = new Harness(
            ("Foo.cs", "namespace T;\r\npublic partial class Foo\r\n{\r\n    public int A { get; set; }\r\n}\r\n"),
            ("Foo.Extra.cs", "namespace T;\r\npublic partial class Foo\r\n{\r\n    public int B { get; set; }\r\n}\r\n"));

        var result = await h.RenameAsync("T:T.Foo", "Bar");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Foo.cs")), Is.False);
            Assert.That(File.Exists(h.PathOf("Bar.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("Foo.Extra.cs")), Is.True, "a file whose stem differs stays");
            Assert.That(File.ReadAllText(h.PathOf("Foo.Extra.cs")), Does.Contain("partial class Bar"));
        });
    }

    [Test]
    public async Task RenameSymbol_DryRun_ReportsTheFileRenameButWritesNothingAsync()
    {
        using var h = new Harness(("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n}\r\n"));

        var result = await h.Tools.RenameSymbolTool(reason: "test message", projectName: ProjectName, docCommentId: "T:T.Calc", newName: "StandardCalc", dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(h.PathOf("Calc.cs")), Is.True);
            Assert.That(File.Exists(h.PathOf("StandardCalc.cs")), Is.False);
            Assert.That(Json(result).GetProperty("fileRenamed").GetProperty("to").GetString(), Does.EndWith("StandardCalc.cs"));
        });
    }

    // Eval run 20261112-151531-550 T8-T12: after RenameSymbol moved the file the model read the OLD path and got the hedged text ("an earlier rename
    // or move has changed its name"), then needed two more calls to find the new path. The server remembers its own moves (FileMoveMemory) and names the new path.
    [Test]
    public async Task RenameSymbol_FileMoved_ReadFileOfTheOldPathNamesTheNewPathAndToolAsync()
    {
        using var h = new Harness(
            ("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n"),
            ("User.cs", "namespace T;\r\npublic class User\r\n{\r\n    public Calc Make() => new Calc();\r\n}\r\n"));
        var rename = await h.RenameAsync("T:T.Calc", "StandardCalc");
        Assert.That(rename.IsError, Is.False, rename.ErrorData?.Message);

        var read = await TestTools.WorkspaceTools(h.Manager).ReadFileTool(reason: "test message", h.PathOf("Calc.cs"));

        Assert.That(read.IsError, Is.True);
        var message = read.ErrorData!.Message;
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("was moved to '").And.Contain("StandardCalc.cs' by an earlier RenameSymbol call in this session"));
            Assert.That(message, Does.Contain("(a rename of the type it declares). Use the new path."));
            Assert.That(message, Does.Contain("ReadFile"));
            Assert.That(message, Does.Not.Contain("find its current path"), "the hedged wording must not appear when the new path is known");
            Assert.That(message, Does.Not.Contain("Did you mean"));
        });
    }

    [Test]
    public async Task RenameSymbol_FileMoved_CallSitesNoteNamesThePostMoveFileAsync()
    {
        using var h = new Harness(
            ("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n    public int X() => 1;\r\n}\r\n"),
            ("User.cs", "namespace T;\r\npublic class User\r\n{\r\n    public Calc Make() => new Calc();\r\n}\r\n"));

        var rename = await h.RenameAsync("T:T.Calc", "StandardCalc");

        Assert.That(rename.IsError, Is.False, rename.ErrorData?.Message);
        var note = Json(rename).GetProperty("callSitesNote").GetString();
        Assert.Multiple(() =>
        {
            Assert.That(note, Does.Contain("  StandardCalc.cs:2"), "the moved file is named by its new name");
            Assert.That(note, Does.Not.Contain("  Calc.cs:"), "the pre-move file name must not appear");
            Assert.That(note, Does.Contain("  User.cs:4"), "an unmoved file keeps its name");
        });
    }

    [Test]
    public async Task RenameSymbol_DryRun_DoesNotRecordTheMoveAsync()
    {
        using var h = new Harness(("Calc.cs", "namespace T;\r\npublic class Calc\r\n{\r\n}\r\n"));

        var result = await h.Tools.RenameSymbolTool(reason: "test message", projectName: ProjectName, docCommentId: "T:T.Calc", newName: "StandardCalc", dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(FileMoveMemory.Shared.TryGetCurrentPath(h.PathOf("Calc.cs"), out _, out _), Is.False, "nothing was moved, so nothing is remembered");
    }
}
