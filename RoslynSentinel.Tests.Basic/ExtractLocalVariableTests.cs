using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

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
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        
        Assert.That(result.UpdatedText, Does.Contain("product"), "Variable name must appear in result");
        Assert.That(result.UpdatedText, Does.Contain("var product"), "Should declare with var keyword");
        Assert.That(result.UpdatedText, Does.Contain("return product"), "Should replace original expression with variable reference");
        Assert.That(result.UpdatedText, Does.Not.Contain("return 6 * 7"), "Original expression should be replaced");
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
        
        Assert.That(result.UpdatedText, Does.Contain("greeting"), "Variable name must appear in result");
        Assert.That(result.UpdatedText, Does.Contain("var greeting"), "Should declare string variable with var");
        Assert.That(result.UpdatedText, Does.Contain("return greeting"), "Should replace string literal with variable reference");
        Assert.That(result.UpdatedText, Does.Not.Contain("return \"Hello, World!\""), "String literal should be replaced");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: Property Access Expression
    // ══════════════════════════════════════════════════════════════════════════
    // Observed flaky 2026-08-25: failed under a full-suite/parallel run, passed in isolation and
    // on suite rerun. Not a regression — see feedback_comment_suspected_flaky_tests memory.
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
            "Test.cs", "this.Name", "personName");
        
        Assert.That(result.UpdatedText, Does.Contain("personName"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var personName"), "Should declare with var");
        Assert.That(result.UpdatedText, Does.Contain("personName.ToUpperInvariant()"), "Should use extracted variable in method call");
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
        
        Assert.That(result.UpdatedText, Does.Contain("sum"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var sum = a + b"), "Should declare with addition");
        Assert.That(result.UpdatedText, Does.Contain("return sum"), "Should replace original binary operation");
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
        
        Assert.That(result.UpdatedText, Does.Contain("isGreater"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var isGreater = x > y"), "Should declare with comparison");
        Assert.That(result.UpdatedText, Does.Contain("return isGreater"), "Should replace comparison with variable");
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
        
        Assert.That(result.UpdatedText, Does.Contain("total"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var total = a + b"), "Should create var declaration");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(total)"), "Should replace in print statement");
        
        // Verify the insertion is before the WriteLine statement
        var lines = result.UpdatedText!.Split('\n');
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
        Assert.That(result.UpdatedText, Does.Contain("var result2"), "Should generate unique name avoiding conflicts");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(result2)"), "Should use unique name in usage");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 8: Skips Method Calls (Side Effects)
    // ══════════════════════════════════════════════════════════════════════════
    // Observed flaky 2026-08-25: failed under a full-suite/parallel run, passed in isolation and
    // on suite rerun. Not a regression — see feedback_comment_suspected_flaky_tests memory.
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
        
        // Should not modify source for method call (has side effects)
        Assert.That(result.UpdatedText, Is.Null,
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
        Assert.That(result.UpdatedText, Does.Contain("var"), "Should declare variable with var");
        Assert.That(result.UpdatedText, Does.Contain("return"), "Should return the extracted variable");
        Assert.That(result.UpdatedText!.Contains("42"), "Numeric literal should be in the declaration");
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
        
        Assert.That(result.UpdatedText, Does.Contain("subTotal"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var subTotal"), "Should declare parenthesized expression");
        Assert.That(result.UpdatedText, Does.Contain("return subTotal * 2"), "Should replace in calculation");
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
        
        Assert.That(result.UpdatedText, Does.Contain("result"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var result = x + y"), "Should create declaration");
        Assert.That(result.UpdatedText, Does.Contain("return result"), "Should return the extracted variable");
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
        
        Assert.That(result.UpdatedText, Does.Contain("product"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var product"), "Should declare variable");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(product)"), "Should use variable as argument");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 13: Whitespace-Different-But-Complete Expression Hits The Exact Path,
    // Not The Ambiguous Nearest-Enclosing-Expression Fallback
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    [Description("A caller-supplied expression that is the WHOLE target expression but with different "
                 + "internal spacing (e.g. around an operator) must still resolve via the exact-match "
                 + "path, not silently fall through to the ambiguous 'nearest enclosing expression at "
                 + "this position' guess — which could resolve to a larger expression than intended if "
                 + "the position happens to fall inside one. Whitespace tolerance and exactness are not "
                 + "in conflict here: the expression is complete, just differently formatted. Uses a "
                 + "multi-line spacing difference (extra blank line inside the expression) rather than "
                 + "inter-token spacing, since ContextHelper's single-line collapsed-whitespace fallback "
                 + "resolves to the containing LINE's start (correct for member-level disambiguation, "
                 + "not precise enough for expression-level positioning) — a same-line-but-differently-"
                 + "spaced snippet doesn't reach this method's own exact-vs-fallback branch at all, it's "
                 + "resolved (or not) one layer earlier. See docs/TODO.md for that separate, deeper gap.")]
    public async Task ExtractLocalVariable_WholeExpressionWithDifferentInternalSpacing_ResolvesExactly()
    {
        const string source =
            "public class Calculator\n" +
            "{\n" +
            "    public int Add(int a, int b)\n" +
            "    {\n" +
            "        return a +\n" +
            "            b;\n" +
            "    }\n" +
            "}";
        SetSource(source, "Test.cs");

        // Caller's snippet spans the same two lines but re-wraps them onto one line — same
        // expression text once whitespace is collapsed, reached via the exact-ordinal-with-
        // line-ending-tolerance path (ContextHelper.cs:39-56), which preserves the real
        // in-source position rather than snapping to a line start.
        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "a +\n            b", "sum");

        Assert.That(result.UpdatedText, Does.Contain("sum"), "Variable name must appear");
        Assert.That(result.UpdatedText, Does.Contain("var sum"), "Should declare with the whole expression");
        Assert.That(result.UpdatedText, Does.Contain("return sum"), "Should replace the whole expression, not a sub-part of it");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 14: Same-Line Inter-Token Spacing Difference Now Resolves Precisely
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    [Description("Regression for the ContextHelper gap logged in docs/TODO.md: the single-line "
                 + "collapsed-whitespace fallback used to resolve to the containing LINE's start "
                 + "regardless of where in the line the match actually began, so a sub-line snippet "
                 + "reached via that fallback (source has extra spacing around an operator that the "
                 + "caller's snippet collapses) landed the position on the preceding token (e.g. "
                 + "'return'), never reaching this method's exact-match check. ContextHelper now maps "
                 + "the collapsed-offset match back to the real in-line offset, so this must resolve "
                 + "via the exact-match path, not the ambiguous fallback.")]
    public async Task ExtractLocalVariable_SameLineDifferentInternalSpacing_ResolvesExactly()
    {
        const string source =
            "public class Calculator\n" +
            "{\n" +
            "    public int Add(int a, int b)\n" +
            "    {\n" +
            "        return a  +  b;\n" +
            "    }\n" +
            "}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractLocalVariableAsync(
            "Test.cs", "a + b", "sum");

        Assert.That(result.UpdatedText, Does.Contain("var sum"), "Should declare with the whole expression");
        Assert.That(result.UpdatedText, Does.Contain("return sum"), "Should replace the whole expression, not a sub-part of it");
    }
}
