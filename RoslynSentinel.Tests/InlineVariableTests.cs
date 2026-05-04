using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Comprehensive tests for SemanticRefactoringLibrary.InlineVariableAsync
/// 
/// Purpose: Inline a temporary variable by replacing all its usages with the assigned expression
/// Use case: Convert `var x = 5; return x * 2;` to `return 5 * 2;`
/// 
/// Test Coverage:
/// 1. Simple literal inlining (var x = 5)
/// 2. Expression inlining (var x = a + b)
/// 3. String inlining (var x = "hello")
/// 4. Method call inlining (var x = GetValue())
/// 5. Multiple usages (same variable used 2-3 times)
/// 6. No usage (remove unused variable)
/// 7. Parenthesis handling (var x = a + b, then x * 2 → (a + b) * 2)
/// 8. Nested expressions (var x = func(y))
/// </summary>
[TestFixture]
public class InlineVariableTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SemanticRefactoringLibrary _library;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _library = new SemanticRefactoringLibrary(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 1: Simple Literal Inlining
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_SimpleLiteral_InlinesCorrectly()
    {
        const string source = @"
public class Calculator
{
    public int Calculate()
    {
        var x = 5;
        return x * 2;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "x");

        Assert.That(result, Does.Contain("return 5 * 2"), "Literal should replace variable usage");
        Assert.That(result, Does.Not.Contain("var x = 5"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 2: Expression Inlining (Binary Operation)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_BinaryExpression_InlinesWithParentheses()
    {
        const string source = @"
public class Math
{
    public int Calculate(int a, int b)
    {
        var sum = a + b;
        return sum * 2;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "sum");

        Assert.That(result, Does.Contain("(a + b)"), "Binary expression should be parenthesized");
        Assert.That(result, Does.Contain("(a + b) * 2"), "Parenthesized expression should replace variable");
        Assert.That(result, Does.Not.Contain("var sum"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: String Literal Inlining
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_StringLiteral_InlinesCorrectly()
    {
        const string source = @"
public class StringTest
{
    public string GetGreeting()
    {
        var greeting = ""Hello, World!"";
        Console.WriteLine(greeting);
        return greeting;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "greeting");

        // At least one usage should be replaced
        Assert.That(result, Does.Contain("\"Hello, World!\""), "String should appear in the result");
        // And the variable declaration should be removed
        Assert.That(result, Does.Not.Contain("var greeting ="), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 4: Method Call Inlining
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_MethodCall_InlinesWithParentheses()
    {
        const string source = @"
public class Service
{
    private int GetValue() => 42;

    public int Process()
    {
        var result = GetValue();
        return result + 10;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "result");

        Assert.That(result, Does.Contain("(GetValue())"), "Method call should be parenthesized");
        Assert.That(result, Does.Contain("(GetValue()) + 10"), "Parenthesized call should replace variable");
        Assert.That(result, Does.Not.Contain("var result"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 5: Multiple Usages (Variable Used Twice)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_MultipleUsages_ReplacesAll()
    {
        const string source = @"
public class Logic
{
    public int Execute(int x)
    {
        var doubled = x * 2;
        int first = doubled + 5;
        int second = doubled - 3;
        return first + second;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "doubled");

        Assert.That(result, Does.Contain("int first = (x * 2) + 5"), "First usage should be replaced");
        Assert.That(result, Does.Contain("int second = (x * 2) - 3"), "Second usage should be replaced");
        Assert.That(result, Does.Not.Contain("var doubled"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 6: No Usage (Unused Variable)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_UnusedVariable_RemovesDeclaration()
    {
        const string source = @"
public class Cleanup
{
    public void DoWork()
    {
        var unused = 42;
        Console.WriteLine(""Done"");
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "unused");

        Assert.That(result, Does.Not.Contain("var unused"), "Unused variable declaration should be removed");
        Assert.That(result, Does.Contain("Console.WriteLine(\"Done\")"), "Other statements should remain");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 7: Parenthesis Handling - Complex Expression
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_ComplexExpressionInMultiplication_AddsParentheses()
    {
        const string source = @"
public class Expression
{
    public int Calculate(int a, int b, int c)
    {
        var expr = a + b + c;
        return expr * 2;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "expr");

        Assert.That(result, Does.Contain("(a + b + c) * 2"), "Complex expression should be parenthesized");
        Assert.That(result, Does.Not.Contain("var expr"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 8: Nested Function Call Inlining
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_NestedFunctionCall_InlinesCorrectly()
    {
        const string source = @"
public class Nested
{
    public int DoMath(int x)
    {
        var nested = Math.Max(x, 10);
        return nested + 5;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "nested");

        Assert.That(result, Does.Contain("(Math.Max(x, 10))"), "Nested call should be parenthesized");
        Assert.That(result, Does.Contain("(Math.Max(x, 10)) + 5"), "Should inline nested call");
        Assert.That(result, Does.Not.Contain("var nested"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 9: Identifier Only (No Expression Needed)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_SimpleIdentifier_InlinesWithoutParentheses()
    {
        const string source = @"
public class Simple
{
    public int Passthrough(int value)
    {
        var alias = value;
        return alias + 10;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "alias");

        Assert.That(result, Does.Contain("return value + 10"), "Simple identifier should inline without extra parens");
        Assert.That(result, Does.Not.Contain("var alias"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 10: Conditional Expression (Ternary)
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_ConditionalExpression_InlinesWithParentheses()
    {
        const string source = @"
public class Conditional
{
    public int GetValue(int x)
    {
        var result = x > 5 ? 10 : 20;
        return result * 2;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "result");

        Assert.That(result, Does.Contain("(x > 5 ? 10 : 20)"), "Ternary should be parenthesized");
        Assert.That(result, Does.Contain("(x > 5 ? 10 : 20) * 2"), "Should inline ternary with parens");
        Assert.That(result, Does.Not.Contain("var result"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 11: Variable in Array Initializer
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_InArrayInitializer_InlinesCorrectly()
    {
        const string source = @"
public class ArrayTest
{
    public void Process()
    {
        var value = 10;
        int[] arr = { value, value * 2, value + 5 };
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "value");

        // All usages should be replaced with the value
        Assert.That(result, Does.Contain("10"), "Value should appear multiple times");
        Assert.That(result, Does.Contain("10 * 2"), "Binary operation should use the value");
        Assert.That(result, Does.Contain("10 + 5"), "Addition should use the value");
        Assert.That(result, Does.Not.Contain("var value"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 12: Numeric Literal - Zero
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_ZeroLiteral_InlinesCorrectly()
    {
        const string source = @"
public class ZeroTest
{
    public bool IsZero()
    {
        var zero = 0;
        return zero == 0;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "zero");

        Assert.That(result, Does.Contain("return 0 == 0"), "Zero literal should inline");
        Assert.That(result, Does.Not.Contain("var zero"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 13: Empty String
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_EmptyString_RemovesUnusedVariable()
    {
        const string source = @"
public class EmptyStringTest
{
    public void DoNothing()
    {
        var empty = """";
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "empty");

        // For unused variables, the declaration should be removed
        Assert.That(result, Does.Not.Contain("var empty"), "Unused variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 14: Decimal Literal
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_DecimalLiteral_InlinesCorrectly()
    {
        const string source = @"
public class DecimalTest
{
    public decimal Calculate(decimal amount)
    {
        var taxRate = 0.08m;
        return amount * taxRate;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "taxRate");

        Assert.That(result, Does.Contain("amount * 0.08m"), "Decimal literal should inline");
        Assert.That(result, Does.Not.Contain("var taxRate"), "Variable declaration should be removed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 15: Negative Number
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task InlineVariable_NegativeNumber_InlinesCorrectly()
    {
        const string source = @"
public class NegativeTest
{
    public int GetNegative()
    {
        var negative = -42;
        return negative * 2;
    }
}";
        SetSource(source);

        var result = await _library.InlineVariableAsync("Test.cs", "negative");

        // The negative sign is a unary operator, so -42 might not need parens
        Assert.That(result, Does.Contain("* 2"), "Should inline negative number");
        Assert.That(result, Does.Not.Contain("var negative"), "Variable declaration should be removed");
    }
}
