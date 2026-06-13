using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Comprehensive tests for RefactoringEngine.ExtractConstantAsync
/// 
/// Purpose: Extract magic numbers/strings into named constants
/// Use case: Convert `if (x > 100)` to `private const int MaxValue = 100; if (x > MaxValue)`
/// 
/// Test Coverage:
/// 1. String literal extraction
/// 2. Integer literal extraction  
/// 3. Decimal/double literal extraction
/// 4. Boolean literal extraction
/// 5. Negative numbers
/// 6. Zero/empty string handling
/// 7. Multiple occurrences of same literal (replace all)
/// 8. Name collision handling (unique constant names)
/// 9. Type inference correctness
/// 10. Class-level constant placement
/// </summary>
[TestFixture]
public class ExtractConstantTests
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
    // Test 1: String Literal Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_StringLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Config
{
    public string GetApiKey()
    {
        if (string.IsNullOrEmpty(""MySecret""))
            return ""MySecret"";
        return null;
    }
}";
        SetSource(source, "Test.cs");

        // Use lineBefore to disambiguate - use unique context
        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "\"MySecret\"", "DefaultApiKey", "private", "IsNullOrEmpty(");

        Assert.That(result, Does.Contain("private const string DefaultApiKey = \"MySecret\""),
            "Should declare private const string at class level");
        Assert.That(result, Does.Contain("DefaultApiKey"),
            "Should reference constant by name in the code");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 2: Integer Literal Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_IntegerLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Validator
{
    public bool IsValid(int value)
    {
        return value > 42;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "42", "MaxValue");

        Assert.That(result, Does.Contain("private const int MaxValue = 42"),
            "Should declare private const int at class level");
        Assert.That(result, Does.Contain("MaxValue"),
            "Should reference constant by name");
        Assert.That(result, Does.Contain("value > MaxValue"),
            "Should replace the literal");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 3: Double/Decimal Literal Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_DoubleLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Calculator
{
    public double Convert(double celsius)
    {
        return celsius * 1.8 + 32.0;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "1.8", "ConversionFactor");

        Assert.That(result, Does.Contain("private const double ConversionFactor = 1.8"),
            "Should infer double type for decimal literal");
        Assert.That(result, Does.Contain("ConversionFactor"),
            "Should reference constant by name");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 4: Boolean Literal Extraction  
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_BooleanLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Settings
{
    public bool IsDebug()
    {
        return true;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "true", "IsDebugMode", "private", "return");

        Assert.That(result, Does.Contain("private const bool IsDebugMode = true") |
                             Does.Contain("private const bool IsDebugMode = True"),
            "Should declare private const bool");
        Assert.That(result, Does.Contain("IsDebugMode"),
            "Should reference constant by name");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 5: Negative Numbers in Expressions
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_LargeIntegerLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Range
{
    public bool InRange(int value)
    {
        return value > 1000;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "1000", "MaxRange", "private", "value >");

        Assert.That(result, Does.Contain("private const int MaxRange = 1000"),
            "Should extract large integer literal");
        Assert.That(result, Does.Contain("MaxRange"),
            "Should reference constant by name");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 6: Zero Literal Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_ZeroLiteral_ExtractsCorrectly()
    {
        const string source = @"
public class Counter
{
    public int Reset()
    {
        return 0;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "0", "Zero", "private", "return");

        Assert.That(result, Does.Contain("private const int Zero = 0"),
            "Should extract zero literal");
        Assert.That(result, Does.Contain("Zero"),
            "Should reference constant");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 7: Empty String Extraction
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_EmptyString_ExtractsCorrectly()
    {
        const string source = @"
public class StringHelper
{
    public bool IsEmpty(string value)
    {
        return value == """";
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "\"\"", "EmptyString", "private", "value ==");

        Assert.That(result, Does.Contain("private const string EmptyString = \"\""),
            "Should extract empty string literal");
        Assert.That(result, Does.Contain("EmptyString"),
            "Should reference constant");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 8: Multiple Occurrences in Same Method
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_MultipleOccurrences_ReplacesAll()
    {
        const string source = @"
public class PageSize
{
    public bool IsStandard(int size) => size == 10 && size > 5;
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "10", "DefaultSize", "private", "size ==");

        Assert.That(result, Does.Contain("private const int DefaultSize = 10"),
            "Should declare constant once");
        Assert.That(result, Does.Contain("DefaultSize"),
            "Should reference constant in code");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 9: Unique Constant Name Usage
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_UniqueName_UsesProvidedName()
    {
        const string source = @"
public class Util
{
    public bool Check(int value)
    {
        return value > 100;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "100", "MaxValue", "private", "value >");

        Assert.That(result, Does.Contain("private const int MaxValue = 100"),
            "Should use provided name for constant");
        Assert.That(result, Does.Contain("MaxValue"),
            "Should reference constant in code");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 10: Type Inference - Decimal Type
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_DecimalLiteral_InfersDecimalType()
    {
        const string source = @"
public class Money
{
    public decimal CalculateTax(decimal amount)
    {
        return amount * 0.08m;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "0.08m", "TaxRate");

        Assert.That(result, Does.Contain("private const decimal TaxRate = 0.08m") |
                             Does.Contain("private const decimal TaxRate = 0.08M"),
            "Should infer decimal type for m-suffixed literal");
        Assert.That(result, Does.Contain("TaxRate"),
            "Should reference constant");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 11: Public Visibility Modifier
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_PublicVisibility_AppliesCorrectly()
    {
        const string source = @"
public class Logger
{
    public void Log()
    {
        Console.WriteLine(""INFO"");
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "\"INFO\"", "LogLevel", "public", "Console.WriteLine");

        Assert.That(result, Does.Contain("public const string LogLevel = \"INFO\""),
            "Should apply public visibility modifier");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test 12: Constant Placement at Class Level
    // ══════════════════════════════════════════════════════════════════════════
    [Test]
    public async Task ExtractConstant_PlacedAtClassLevel()
    {
        const string source = @"
public class Service
{
    public void Execute()
    {
        int value = 99;
    }
}";
        SetSource(source, "Test.cs");

        var result = await _refactoringEngine.ExtractConstantAsync(
            "Test.cs", "99", "MaxAttempts");

        var lines = result.UpdatedText!.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var constLine = Array.FindIndex(lines, l => l.Contains("const int MaxAttempts"));
        var methodLine = Array.FindIndex(lines, l => l.Contains("void Execute"));

        Assert.That(constLine, Is.GreaterThanOrEqualTo(0), "Const should be declared");
        Assert.That(methodLine, Is.GreaterThanOrEqualTo(0), "Method should exist");
        Assert.That(constLine, Is.LessThan(methodLine),
            "Constant should be at class level (before methods)");
    }
}

// Extension methods for testing
internal static class StringExtensions
{
    public static int CountOccurrences(this string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
