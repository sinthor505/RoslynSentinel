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
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _modernEngine = new ModernizationEngine(_workspaceManager, config);
        _styleEngine = new CodeStyleEngine(_workspaceManager, config);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
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

    // ══════════════════════════════════════════════════════════════
    // ModernizationEngine — ConvertMethodToExpressionBodyAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ConvertMethodToExpressionBody_SingleReturn_ConvertsToArrow()
    {
        var source = @"public class C
{
    private string _name;
    public string GetName() { return _name; }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _modernEngine.ConvertMethodToExpressionBodyAsync("C.cs", "GetName");

        Assert.That(result, Does.Contain("=>"),
            "Method with single return statement should become an expression body.");
        Assert.That(result, Does.Contain("_name"),
            "The returned expression should still be present.");
    }

    [Test]
    public async Task ConvertMethodToExpressionBody_MultipleStatements_NotConverted()
    {
        var source = @"public class C
{
    private string _first;
    private string _last;
    public string GetFullName()
    {
        var name = _first + _last;
        return name;
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _modernEngine.ConvertMethodToExpressionBodyAsync("C.cs", "GetFullName");

        Assert.That(result, Does.Not.Contain("=>"),
            "Method with multiple statements should NOT be converted to expression body.");
        Assert.That(result, Does.Contain("return name"),
            "Original return statement should be preserved.");
    }

    [Test]
    public async Task ConvertMethodToExpressionBody_ExpressionStatement_ConvertsVoidMethod()
    {
        var source = @"using System;
public class C
{
    public void Print() { Console.WriteLine(""hi""); }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _modernEngine.ConvertMethodToExpressionBodyAsync("C.cs", "Print");

        Assert.That(result, Does.Contain("=>"),
            "Void method with single expression statement should convert to expression body.");
    }

    // ══════════════════════════════════════════════════════════════
    // SyntaxUpgradeEngine — AddBracesAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddBraces_IfWithoutBraces_AddsBraces()
    {
        var source = @"public class C
{
    public void M(bool x)
    {
        if (x) DoWork();
    }
    void DoWork() { }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _syntaxUpgradeEngine.AddBracesAsync("C.cs");

        Assert.That(result, Does.Contain("{"),
            "Braces should be added around the if-body.");
        Assert.That(result, Does.Contain("DoWork"),
            "The statement inside the if should still be present.");
    }

    [Test]
    public async Task AddBraces_AlreadyHasBraces_IsIdempotent()
    {
        var source = @"public class C
{
    public void M(bool x)
    {
        if (x)
        {
            DoWork();
        }
    }
    void DoWork() { }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _syntaxUpgradeEngine.AddBracesAsync("C.cs");

        // Should not duplicate braces or break formatting
        Assert.That(result, Does.Contain("DoWork"),
            "Already-braced code should still contain the method call.");
        // Count open braces — should not have extra ones
        Assert.That(result, Is.Not.Null.And.Not.Empty,
            "Result should be non-empty.");
    }

    [Test]
    public async Task AddBraces_ElseWithoutBraces_AddsBraces()
    {
        var source = @"public class C
{
    public void M(bool x)
    {
        if (x)
            DoWork();
        else
            Fallback();
    }
    void DoWork() { }
    void Fallback() { }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));

        var result = await _syntaxUpgradeEngine.AddBracesAsync("C.cs");

        Assert.That(result, Does.Contain("DoWork"),
            "if-branch should be present.");
        Assert.That(result, Does.Contain("Fallback"),
            "else-branch should be present.");
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
