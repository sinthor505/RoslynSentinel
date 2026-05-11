using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

public class RefactoringTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;
    private RefinementEngine _refinementEngine;
    private AdvancedLogicEngine _advancedLogicEngine;
    private GranularRefactoringEngine _granularEngine;
    private CodeHealingEngine _healingEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _granularEngine = new GranularRefactoringEngine(_workspaceManager);
        _healingEngine = new CodeHealingEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    [Test]
    public async Task MoveTypeToFile_Should_ExtractClass_And_RemoveFromOriginal()
    {
        using var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);

        var filePath = "E:\\source\\repos\\Mixed.cs";
        var sourceCode = "namespace MyNamespace; public class ClassOne {} public class ClassTwo {}";

        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(docId, "Mixed.cs", SourceText.From(sourceCode), filePath: filePath);
        _workspaceManager.SetTestSolution(solution);

        var results = await _refactoringEngine.MoveTypeToFileAsync(filePath, "ClassTwo");

        Assert.That(results.Count, Is.EqualTo(2));
        Assert.That(results[filePath], Does.Not.Contain("class ClassTwo"));
        Assert.That(results["E:\\source\\repos\\ClassTwo.cs"], Contains.Substring("class ClassTwo"));
    }

    [Test]
    public async Task RenameSymbol_Should_UpdateAllReferences()
    {
        var sourceCode = "public class Service { public void OldName() {} public void Usage() { OldName(); } }";
        _workspaceManager.SetTestSolution(CreateSolution(sourceCode, "Service.cs"));

        var results = await _refactoringEngine.RenameSymbolAsync("Service.cs", "OldName", "void OldName()", "NewName");

        Assert.That(results.Error, Is.Null, $"Rename failed: {results.Error}");
        var updatedContent = results.PendingChanges["Service.cs"];
        Assert.That(updatedContent, Contains.Substring("public void NewName()"));
        Assert.That(updatedContent, Contains.Substring("NewName();"));
    }

    [Test]
    public async Task InlineMethod_Should_ReplaceCallSites_With_Expression()
    {
        var source = "public class C { public int GetTen() { return 10; } public void M() { var x = GetTen(); } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _refinementEngine.InlineMethodAsync("C.cs", "GetTen");
        var updatedContent = result.Values.FirstOrDefault(v => !v.StartsWith("// Error:")) ?? "";
        Assert.That(updatedContent, Contains.Substring("var x = 10;"));
        Assert.That(updatedContent, Does.Not.Contain("GetTen()"));
    }

    [Test]
    public async Task ExtensionToStatic_Should_Remove_This_Modifier()
    {
        var source = "public static class Ext { public static void M(this string s) { } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Ext.cs"));
        var result = await _advancedLogicEngine.ExtensionToStaticAsync("Ext.cs", "M");
        Assert.That(result, Contains.Substring("public static void M(string s)"));
        Assert.That(result, Does.Not.Contain("this string"));
    }

    [Test]
    public async Task ConvertStaticToExtension_Should_Add_This_Modifier()
    {
        var source = "public static class Ext { public static void M(string s) { } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Ext.cs"));
        var result = await _advancedLogicEngine.ConvertStaticToExtensionAsync("Ext.cs", "M");
        Assert.That(result, Contains.Substring("public static void M(this string s)"));
    }

    [Test]
    public async Task InlineField_Should_Replace_Field_With_Value()
    {
        var source = "public class C { private const int X = 42; public int M() => X; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _granularEngine.InlineFieldAsync("C.cs", "X");
        Assert.That(result, Contains.Substring("=> 42;"));
        Assert.That(result, Does.Not.Contain("const int X"));
    }

    [Test]
    public async Task ExtractMembersToPartial_Should_Split_Class()
    {
        var source = "public class C { public void M1() {} public void M2() {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _granularEngine.ExtractMembersToPartialAsync("C.cs", "C", new[] { "M2" });
        Assert.That(result.Count, Is.EqualTo(1));
        var partialCode = result.Values.First();
        Assert.That(partialCode, Contains.Substring("partial class C"));
        Assert.That(partialCode, Contains.Substring("void M2()"));
    }

}
