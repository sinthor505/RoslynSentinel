using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Comprehensive tests for RefactoringEngine.ExtractLocalVariableAsync
/// 
/// Purpose: Extract an inline expression into a local variable declaration
/// Use case: Convert `return x + y;` to `var sum = x + y; return sum;`
/// 
/// Test Coverage:
/// 1. Simple arithmetic expression
/// 2. String expression
/// 3. Property access
/// 4. Binary operation (x + y)
/// 5. Comparison operation (x > y)
/// 6. Method return statement extraction
/// 7. Variable assignment expression
/// 8. Type inference and unique naming
/// </summary>
[TestFixture]
public class ExtractLocalVariableTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: Simple Arithmetic Expression
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_SimpleArithmetic_ExtractsCorrectly()
    {
        const string source = @"
public class Math
{
    public int Calculate()
    {
        return 6 * 7;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "6 * 7", "product");
        
        Assert.That(result, Does.Contain("product"), "Variable name must appear in result");
        Assert.That(result, Does.Contain("var product"), "Should declare with var keyword");
        Assert.That(result, Does.Contain("return product"), "Should replace original expression with variable reference");
        Assert.That(result, Does.Not.Contain("return 6 * 7"), "Original expression should be replaced");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 2: String Literal Expression
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_StringLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class StringTest
{
    public string GetGreeting()
    {
        return ""Hello, World!"";
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "\"Hello, World!\"", "greeting");
        
        Assert.That(result, Does.Contain("greeting"), "Variable name must appear in result");
        Assert.That(result, Does.Contain("var greeting"), "Should declare string variable with var");
        Assert.That(result, Does.Contain("return greeting"), "Should replace string literal with variable reference");
        Assert.That(result, Does.Not.Contain("return \"Hello, World!\""), "String literal should be replaced");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: Property Access Expression
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_PropertyAccess_ExtractsCorrectly()
    {
        const string source = @"
public class Person
{
    public string Name { get; set; }
    
    public string GetNameUppercase()
    {
        return this.Name.ToUpperInvariant();
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "this.Name", "personName", lineBefore: "return this.Name.ToUpperInvariant();");
        
        Assert.That(result, Does.Contain("personName"), "Variable name must appear");
        Assert.That(result, Does.Contain("var personName"), "Should declare with var");
        Assert.That(result, Does.Contain("personName.ToUpperInvariant()"), "Should use extracted variable in method call");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 4: Binary Operation (Addition)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_BinaryOperation_Addition_ExtractsCorrectly()
    {
        const string source = @"
public class Calculator
{
    public int Add(int a, int b)
    {
        return a + b;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "a + b", "sum");
        
        Assert.That(result, Does.Contain("sum"), "Variable name must appear");
        Assert.That(result, Does.Contain("var sum = a + b"), "Should declare with addition");
        Assert.That(result, Does.Contain("return sum"), "Should replace original binary operation");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 5: Comparison Operation (Greater Than)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_ComparisonOperation_ExtractsCorrectly()
    {
        const string source = @"
public class Comparison
{
    public bool IsGreater(int x, int y)
    {
        return x > y;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "x > y", "isGreater");
        
        Assert.That(result, Does.Contain("isGreater"), "Variable name must appear");
        Assert.That(result, Does.Contain("var isGreater = x > y"), "Should declare with comparison");
        Assert.That(result, Does.Contain("return isGreater"), "Should replace comparison with variable");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 6: Method Body with Multiple Statements
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_MethodBodyWithMultipleStatements_InsertsCorrectly()
    {
        const string source = @"
public class Logic
{
    public void Execute()
    {
        int a = 10;
        int b = 20;
        Console.WriteLine(a + b);
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "a + b", "total");
        
        Assert.That(result, Does.Contain("total"), "Variable name must appear");
        Assert.That(result, Does.Contain("var total = a + b"), "Should create var declaration");
        Assert.That(result, Does.Contain("Console.WriteLine(total)"), "Should replace in print statement");
        
        // Verify the insertion is before the WriteLine statement
        var lines = result.Split('\n');
        var totalDeclarationLine = Array.FindIndex(lines, l => l.Contains("var total"));
        var printLine = Array.FindIndex(lines, l => l.Contains("Console.WriteLine"));
        Assert.That(totalDeclarationLine, Is.GreaterThanOrEqualTo(0), "Declaration should exist");
        Assert.That(printLine, Is.GreaterThanOrEqualTo(0), "Print statement should exist");
        Assert.That(totalDeclarationLine, Is.LessThan(printLine), "Declaration should come before usage");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 7: Unique Name Generation (Avoiding Conflicts)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_UniqueNameGeneration_AvoidingConflicts()
    {
        const string source = @"
public class Counter
{
    public void Count()
    {
        int result = 5;
        int result1 = 10;
        Console.WriteLine(2 + 3);
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "2 + 3", "result");
        
        // Should automatically generate result2 since result and result1 exist
        Assert.That(result, Does.Contain("var result2"), "Should generate unique name avoiding conflicts");
        Assert.That(result, Does.Contain("Console.WriteLine(result2)"), "Should use unique name in usage");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 8: Skips Method Calls (Side Effects)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_SkipsMethodCallsWithSideEffects()
    {
        const string source = @"
public class Calculator
{
    public int Calculate()
    {
        return Add(5, 3) + 2;
    }
    
    private int Add(int x, int y) => x + y;
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "Add(5, 3)", "value");
        
        // Should return empty for method call (has side effects)
        Assert.That(result, Is.Empty, 
            "Should skip extraction of method calls due to potential side effects");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 9: Numeric Literal - Infers Correct Name
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_NumericLiteral_InfersCorrectName()
    {
        const string source = @"
public class Values
{
    public int GetNumber()
    {
        return 42;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "42", null);  // No variable name provided - should infer
        
        // Should generate a default name for numeric literal
        Assert.That(result, Does.Contain("var"), "Should declare variable with var");
        Assert.That(result, Does.Contain("return"), "Should return the extracted variable");
        Assert.That(result.Contains("42"), "Numeric literal should be in the declaration");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 10: Parenthesized Expression Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_ParenthesizedExpression_HandlesCorrectly()
    {
        const string source = @"
public class Calc
{
    public int Compute()
    {
        return (5 + 3) * 2;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "(5 + 3)", "subTotal");
        
        Assert.That(result, Does.Contain("subTotal"), "Variable name must appear");
        Assert.That(result, Does.Contain("var subTotal"), "Should declare parenthesized expression");
        Assert.That(result, Does.Contain("return subTotal * 2"), "Should replace in calculation");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 11: Simple Expression with Context
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_ExpressionWithAmbiguity_UsesContext()
    {
        const string source = @"
public class Multi
{
    public int Calculate()
    {
        int x = 5;
        int y = 10;
        return x + y;
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "x + y", "result");
        
        Assert.That(result, Does.Contain("result"), "Variable name must appear");
        Assert.That(result, Does.Contain("var result = x + y"), "Should create declaration");
        Assert.That(result, Does.Contain("return result"), "Should return the extracted variable");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 12: Complex Expression - Method Argument
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractLocalVariable_ExpressionAsArgument_ExtractsCorrectly()
    {
        const string source = @"
public class Printer
{
    public void Print(int value)
    {
        Console.WriteLine(10 * 5);
    }
}";
        SetSource(source, "Test.cs");
        
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "10 * 5", "product");
        
        Assert.That(result, Does.Contain("product"), "Variable name must appear");
        Assert.That(result, Does.Contain("var product"), "Should declare variable");
        Assert.That(result, Does.Contain("Console.WriteLine(product)"), "Should use variable as argument");
    }
}
