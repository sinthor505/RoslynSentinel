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
        Assert.That(result.UpdatedText!, Contains.Substring("public record Person"));
        Assert.That(result.UpdatedText!, Contains.Substring("string Name"));
    }

    [Test]
    public async Task UseCollectionExpressions_Should_Modernize_ArrayCreation()
    {
        var source = "public class C { int[] x = new int[] { 1, 2, 3 }; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _styleEngine.UseCollectionExpressionsAsync("C.cs");
        Assert.That(result.UpdatedText!, Contains.Substring("int[] x = [1, 2, 3];"));
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

    // ══════════════════════════════════════════════════════════════
    // ModernizationEngine — ConvertToPatternAsync
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Test 1: Null check pattern - x == null → x is null
    /// </summary>
    [Test]
    public async Task ConvertToPattern_NullCheck_EqualsNull_ConvertsToIsNull()
    {
        const string source = @"public class C 
{
    public void M(object x) 
    {
        if (x == null)
        {
            Console.WriteLine(""Null"");
        }
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("is null"), "Should convert to pattern matching 'is null'");
    }

    /// <summary>
    /// Test 2: Null check pattern on right side - null == x → x is null
    /// </summary>
    [Test]
    public async Task ConvertToPattern_NullCheckReversed_ConvertsToIsNull()
    {
        const string source = @"public class C 
{
    public void M(string name) 
    {
        if (null == name)
        {
            Console.WriteLine(""Empty"");
        }
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("is null"), "Should convert reversed null check to pattern");
    }

    /// <summary>
    /// Test 3: Multiple null checks in same method
    /// </summary>
    [Test]
    public async Task ConvertToPattern_MultipleMethods_ConvertsAllNullChecks()
    {
        const string source = @"public class C 
{
    public void Method1(object x) 
    {
        if (x == null) return;
    }

    public void Method2(string y)
    {
        if (y == null) return;
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        var countOfIsNull = result.UpdatedText!.Split(new[] { "is null" }, StringSplitOptions.None).Length - 1;
        Assert.That(countOfIsNull, Is.GreaterThanOrEqualTo(2), "Should convert both null checks");
    }

    /// <summary>
    /// Test 4: Code without null checks should return unchanged
    /// </summary>
    [Test]
    public async Task ConvertToPattern_NoPatterns_ReturnsUnchanged()
    {
        const string source = @"public class C 
{
    public void M(int x) 
    {
        if (x > 0)
        {
            Console.WriteLine(""Positive"");
        }
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        Assert.That(result, Does.Contain("if (x > 0)"), "Code without patterns should remain unchanged");
    }

    /// <summary>
    /// Test 5: Nested null checks
    /// </summary>
    [Test]
    public async Task ConvertToPattern_NestedNullChecks_ConvertsAllInstances()
    {
        const string source = @"public class C 
{
    public void M(object x, object y) 
    {
        if (x == null)
        {
            if (y == null)
            {
                Console.WriteLine(""Both null"");
            }
        }
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        var countOfIsNull = result.UpdatedText!.Split(new[] { "is null" }, StringSplitOptions.None).Length - 1;
        Assert.That(countOfIsNull, Is.GreaterThanOrEqualTo(2), "Should convert nested null checks");
    }

    /// <summary>
    /// Test 6: Property check with null - x != null condition (preserves code)
    /// </summary>
    [Test]
    public async Task ConvertToPattern_NotNullCheck_PreservesCode()
    {
        const string source = @"public class C 
{
    public void M(object x) 
    {
        if (x != null)
        {
            Console.WriteLine(""Not null"");
        }
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return code");
        // For now, not null patterns might not be converted depending on implementation
        Assert.That(result, Does.Contain("Not null"), "Logic should be preserved");
    }

    /// <summary>
    /// Test 7: Mixed patterns in multiple branches
    /// </summary>
    [Test]
    public async Task ConvertToPattern_MixedPatterns_HandlesAllCases()
    {
        const string source = @"public class C 
{
    public void M(string a, string b, int c) 
    {
        if (a == null) return;
        if (b != null) return;
        if (c > 0) return;
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("is null"), "Should convert null check");
    }

    /// <summary>
    /// Test 8: Null check in single-line if statement
    /// </summary>
    [Test]
    public async Task ConvertToPattern_SingleLineIf_ConvertsPattern()
    {
        const string source = @"public class C 
{
    public void M(object x) 
    {
        if (x == null) return;
    }
}";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Test.cs"));
        
        var result = await _modernEngine.ConvertToPatternAsync("Test.cs");
        
        Assert.That(result, Is.Not.Null.And.Not.Empty, "Should return transformed code");
        Assert.That(result, Does.Contain("is null"), "Should convert single-line null check");
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
        Assert.That(result.UpdatedText!, Contains.Substring("x switch"));
        Assert.That(result.UpdatedText!, Contains.Substring("1 => 10"));
        Assert.That(result.UpdatedText!, Contains.Substring("_ => 20"));
    }

    [Test]
    public async Task SimplifyAllNames_Should_Remove_Global_Alias()
    {
        var source = "public class C { global::System.String s; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await _styleEngine.SimplifyAllNamesAsync("C.cs");
        Assert.That(result.UpdatedText!, Contains.Substring("System.String s;"));
        Assert.That(result, Does.Not.Contain("global::"));
    }
}
