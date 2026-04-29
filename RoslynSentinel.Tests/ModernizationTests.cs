using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

public class ModernizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ModernizationEngine _modernEngine;
    private CodeStyleEngine _styleEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _modernEngine = new ModernizationEngine(_workspaceManager);
        _styleEngine = new CodeStyleEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager);
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
    public async Task ClassToRecord_Should_Modernize_SimpleDataClass()
    {
        var source = "public class Person { public string Name { get; set; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Person.cs"));
        var result = await _modernEngine.ClassToRecordAsync("Person.cs", "Person");
        Assert.That(result, Contains.Substring("public record Person"));
        Assert.That(result, Contains.Substring("string Name"));
    }

    [Test]
    public async Task UseCollectionExpressions_Should_Modernize_ArrayCreation()
    {
        var source = "public class C { int[] x = new int[] { 1, 2, 3 }; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _styleEngine.UseCollectionExpressionsAsync("C.cs");
        Assert.That(result, Contains.Substring("int[] x = [1, 2, 3];"));
    }

    [Test]
    public async Task ConvertSwitchToExpression_Should_Upgrade_Statements()
    {
        var source = @"public class C { 
            public int M(int x) { 
                switch(x) { 
                    case 1: return 10; 
                    default: return 20; 
                } 
            } 
        }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync("C.cs", "M");
        Assert.That(result, Contains.Substring("x switch"));
        Assert.That(result, Contains.Substring("1 => 10"));
        Assert.That(result, Contains.Substring("_ => 20"));
    }

    [Test]
    public async Task SimplifyAllNames_Should_Remove_Global_Alias()
    {
        var source = "public class C { global::System.String s; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _styleEngine.SimplifyAllNamesAsync("C.cs");
        Assert.That(result, Contains.Substring("System.String s;"));
        Assert.That(result, Does.Not.Contain("global::"));
    }
}
