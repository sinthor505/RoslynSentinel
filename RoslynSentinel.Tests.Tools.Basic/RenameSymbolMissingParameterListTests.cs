using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Eval run 20261109-233953-684 T8/T10: the model called RenameSymbol with docCommentId "M:Ns.Type.CalcDisc" (no parameter list),
// got "no longer resolves", and concluded its rejected edit had changed the file. RenameSymbol now resolves a method id that
// lacks its parameter list by containing type plus name: one method of that name is used (and the response says so), several
// overloads are refused with every full id listed. Runs on a real PersistentWorkspaceManager; dryRun keeps the files untouched.
[TestFixture]
[Category("RefactoringSignatureTools")]
public class RenameSymbolMissingParameterListTests
{
    private const string ProjectName = "RenameMissingParams";

    private const string Source = "namespace T;\r\n"
        + "public class Calc\r\n{\r\n"
        + "    public decimal CalcDisc(decimal amount, decimal rate) => amount * rate;\r\n"
        + "}\r\n"
        + "public class Overloaded\r\n{\r\n"
        + "    public int Get(int a) => a;\r\n"
        + "    public int Get(string s) => s.Length;\r\n"
        + "}\r\n";

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RenameMissingParams", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PersistentWorkspaceManager NewManager(string file)
    {
        var project = new AdhocWorkspace().AddProject(ProjectName, LanguageNames.CSharp);
        // A framework reference keeps the candidate compile free of environment errors, as in StaleReadEngineToolsTests.
        var solution = project.WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).Solution;
        solution = solution.GetProject(project.Id)!
            .AddDocument(Path.GetFileName(file), SourceText.From(Source), filePath: file).Project.Solution;
        var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(solution);
        return manager;
    }

    private static async Task<SentinelCallToolResult<object>> RenameAsync(string docCommentId, string newName)
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "Calc.cs");
            File.WriteAllText(file, Source);
            using var manager = NewManager(file);
            var tools = new RefactoringSignatureTools(TestTools.RefactoringSignatureImpl(manager));
            return await tools.RenameSymbolTool(reason: "test message", projectName: ProjectName, docCommentId: docCommentId, newName: newName, dryRun: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RenameSymbol_MethodIdWithoutParameterList_UniqueMethod_ResolvesAndNotesTheFullIdAsync()
    {
        var result = await RenameAsync("M:T.Calc.CalcDisc", "CalculateDiscountedTotal");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var json = JsonSerializer.Serialize(result.SuccessData);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("CalculateDiscountedTotal"));
            Assert.That(json, Does.Contain("docCommentIdNote"));
            Assert.That(json, Does.Contain("M:T.Calc.CalcDisc(System.Decimal,System.Decimal)"), "the note names the full id");
        });
    }

    [Test]
    public async Task RenameSymbol_MethodIdWithoutParameterList_TwoOverloads_ErrorsAndListsBothFullIdsAsync()
    {
        var result = await RenameAsync("M:T.Overloaded.Get", "Fetch");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument), result.ErrorData.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.Message, Does.Contain("missing its parameter list"));
            Assert.That(result.ErrorData.Message, Does.Contain("M:T.Overloaded.Get(System.Int32)"));
            Assert.That(result.ErrorData.Message, Does.Contain("M:T.Overloaded.Get(System.String)"));
        });
    }

    [Test]
    public async Task RenameSymbol_MethodIdWithoutParameterList_NoSuchMethod_StaysNotFoundWithTheNoSimilarNameTextAsync()
    {
        var result = await RenameAsync("M:T.Calc.Nope", "Whatever");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.Message, Does.Contain("Nope"), "the wanted name is echoed");
            Assert.That(result.ErrorData.Message, Does.Contain("No symbol in the solution has a similar name"));
            Assert.That(result.ErrorData.Message, Does.Contain("the symbol does not exist in this solution."), "the fact that a symbol missing from the orientation result does not exist is stated");
            Assert.That(result.ErrorData.Message, Does.Not.Contain("tell the user"));
            Assert.That(result.ErrorData.Message, Does.Not.Contain("no longer resolves"));
        });
    }

    [Test]
    public async Task RenameSymbol_ChangeIdPassedAsDocCommentId_SaysItIsNotAnIdInsteadOfRenamedAsync()
    {
        // Batch H4 R4 T6.1: the changeId from an undo hint was passed as the symbol id.
        var result = await RenameAsync("0cbca485", "Whatever");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData.Message);
        Assert.That(result.ErrorData.Message, Does.Contain("is not a documentation-comment ID"));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("no longer resolves"));
    }
}
