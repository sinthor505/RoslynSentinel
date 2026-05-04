using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Regression tests for Bug #11 (GenerateToStringSafe) and Bug #12 (ExtractMethodSafe).
///
/// Bug #11 — MS generate_tostring produces unescaped literal braces in the interpolated
/// string format section, e.g.:
///     $"AuthUser { Id = {Id}, Email = {Email} }"
/// The outer `{ Id =` is parsed as an interpolation hole → CS8086 compiler error.
/// Fix: emit {{ and }} for literal braces:
///     $"AuthUser {{ Id = {Id}, Email = {Email} }}"
///
/// Bug #12 — MS extract_method generates `private void MethodName(...)` when the
/// selected block ends with `return <expression>`, producing a compile error because
/// the extracted method has the wrong (void) return type.
/// Fix: use SemanticModel.GetTypeInfo() on the return expression.
/// </summary>
[TestFixture]
public class AugmentToolsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Bug #11 — GenerateToStringSafe
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Bug #11 regression: generated output must use {{ and }} for literal braces")]
    public async Task GenerateToStringSafe_WithPublicProperties_EmitsEscapedBraces()
    {
        const string source = @"
public class AuthUser
{
    public Guid Id { get; set; }
    public string Email { get; set; }
    public bool IsActive { get; set; }
}";
        SetSource(source, "AuthUser.cs");

        var result = await _engine.GenerateToStringSafeAsync("AuthUser.cs", "AuthUser");

        Assert.That(result.Success, Is.True, result.Error);
        // CRITICAL: literal braces must be escaped as {{ and }}
        Assert.That(result.UpdatedContent, Does.Contain("{{ "),
            "Must use {{ for the literal opening brace (CS8086 regression)");
        Assert.That(result.UpdatedContent, Does.Contain(" }}"),
            "Must use }} for the literal closing brace (CS8086 regression)");
        // Member interpolations must still be present (unescaped)
        Assert.That(result.UpdatedContent, Does.Contain("{Id}"),
            "Id interpolation must be present");
        Assert.That(result.UpdatedContent, Does.Contain("{Email}"),
            "Email interpolation must be present");
        Assert.That(result.UpdatedContent, Does.Contain("{IsActive}"),
            "IsActive interpolation must be present");
        // The correct escaped pattern must appear (NOT the single-brace MS bug pattern).
        // We check for the escaped form positively: "{{ Id =" — double braces mean literal brace.
        Assert.That(result.UpdatedContent, Does.Contain("{{ Id ="),
            "Must use {{ (escaped) before member list — single { Id = triggers CS8086");
    }

    [Test]
    [Description("Generated code must compile without CS8086 or any other error")]
    public async Task GenerateToStringSafe_GeneratedCode_CompilesCleanly()
    {
        const string source = @"
public class MyModel
{
    public int Value { get; set; }
    public string Name { get; set; }
}";
        SetSource(source, "MyModel.cs");

        var result = await _engine.GenerateToStringSafeAsync("MyModel.cs", "MyModel");

        Assert.That(result.Success, Is.True, result.Error);

        // Verify the generated file compiles without errors
        // Must specify DynamicallyLinkedLibrary — the file is a class, not a console app.
        var tree = CSharpSyntaxTree.ParseText(result.UpdatedContent!);
        var compilation = CSharpCompilation.Create("TestCompile",
            syntaxTrees: [tree],
            options: new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary),
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.That(errors, Is.Empty,
            $"Generated code must compile without errors:\n"
            + string.Join("\n", errors.Select(d => d.ToString())));
    }

    [Test]
    [Description("Explicit member list restricts which properties appear in ToString")]
    public async Task GenerateToStringSafe_WithExplicitMembers_OnlyIncludesSpecifiedMembers()
    {
        const string source = @"
public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string PasswordHash { get; set; }
}";
        SetSource(source, "User.cs");

        var result = await _engine.GenerateToStringSafeAsync(
            "User.cs", "User", ["Id", "Name"]);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{Id}"),
            "Should include Id");
        Assert.That(result.UpdatedContent, Does.Contain("{Name}"),
            "Should include Name");
        Assert.That(result.UpdatedContent, Does.Not.Contain("{PasswordHash}"),
            "PasswordHash must not appear when not in explicit member list");
    }

    [Test]
    [Description("Public fields (not properties) must also be included by default")]
    public async Task GenerateToStringSafe_WithPublicField_IncludesFieldInOutput()
    {
        const string source = @"
public class Point
{
    public int X;
    public int Y;
}";
        SetSource(source, "Point.cs");

        var result = await _engine.GenerateToStringSafeAsync("Point.cs", "Point");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{X}"), "Should include field X");
        Assert.That(result.UpdatedContent, Does.Contain("{Y}"), "Should include field Y");
    }

    [Test]
    [Description("Static members must not be included in ToString")]
    public async Task GenerateToStringSafe_SkipsStaticMembers()
    {
        const string source = @"
public class Config
{
    public static int MaxRetry { get; set; }
    public string Name { get; set; }
}";
        SetSource(source, "Config.cs");

        var result = await _engine.GenerateToStringSafeAsync("Config.cs", "Config");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{Name}"),
            "Instance property Name should be included");
        Assert.That(result.UpdatedContent, Does.Not.Contain("{MaxRetry}"),
            "Static property MaxRetry should be excluded");
    }

    [Test]
    [Description("Missing type returns a Fail result with a descriptive message")]
    public async Task GenerateToStringSafe_TypeNotFound_ReturnsFail()
    {
        SetSource("public class Foo { public int X { get; set; } }", "Foo.cs");

        var result = await _engine.GenerateToStringSafeAsync("Foo.cs", "NonExistentType");

        Assert.That(result.Success, Is.False,
            "Should return Fail when the type does not exist");
        Assert.That(result.Error, Does.Contain("NonExistentType"),
            "Error message should identify the missing type");
    }

    [Test]
    [Description("Calling the tool a second time when ToString() already exists returns Fail")]
    public async Task GenerateToStringSafe_ToStringAlreadyExists_ReturnsFail()
    {
        const string source = @"
public class Foo
{
    public int X { get; set; }
    public override string ToString() => $""Foo {X}"";
}";
        SetSource(source, "Foo.cs");

        var result = await _engine.GenerateToStringSafeAsync("Foo.cs", "Foo");

        Assert.That(result.Success, Is.False,
            "Should return Fail when ToString() is already present");
        Assert.That(result.Error, Does.Contain("already"),
            "Error message should mention the conflict");
    }

    [Test]
    [Description("Type with no public members and no explicit list returns Fail")]
    public async Task GenerateToStringSafe_NoPublicMembers_ReturnsFail()
    {
        const string source = @"
public class InternalOnly
{
    private int _x;
    internal string _y;
}";
        SetSource(source, "InternalOnly.cs");

        var result = await _engine.GenerateToStringSafeAsync("InternalOnly.cs", "InternalOnly");

        Assert.That(result.Success, Is.False,
            "Should return Fail when there are no public members to include");
    }

    [Test]
    [Description("File not found returns Fail with a descriptive message")]
    public async Task GenerateToStringSafe_FileNotFound_ReturnsFail()
    {
        var result = await _engine.GenerateToStringSafeAsync(
            @"C:\does\not\exist.cs", "SomeType");

        Assert.That(result.Success, Is.False,
            "Should return Fail for a non-existent file");
        Assert.That(result.Error, Does.Contain("exist.cs").Or.Contain("not"),
            "Error should reference the missing file");
    }

    [Test]
    [Description("Record types with properties are supported just like classes")]
    public async Task GenerateToStringSafe_RecordType_Works()
    {
        const string source = @"
public record OrderItem
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}";
        SetSource(source, "OrderItem.cs");

        var result = await _engine.GenerateToStringSafeAsync("OrderItem.cs", "OrderItem");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{{ "),
            "Must use escaped braces even for record types");
        Assert.That(result.UpdatedContent, Does.Contain("{ProductId}"));
        Assert.That(result.UpdatedContent, Does.Contain("{Quantity}"));
        Assert.That(result.UpdatedContent, Does.Contain("{UnitPrice}"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Bug #12 — ExtractMethodSafe
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Bug #12 regression: return type must not be void when extracting 'return new T{}'")]
    public async Task ExtractMethodSafe_ReturnNewObject_HasCorrectReturnType()
    {
        const string source = @"
public class Payload
{
    public int Value { get; set; }
}

public class Builder
{
    public Payload Build(int x)
    {
        if (x > 0)
        {
            return new Payload { Value = x };
        }
        return new Payload();
    }
}";
        SetSource(source, "Builder.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Builder.cs", "CreatePayload", "return new Payload { Value = x }");

        Assert.That(result.Success, Is.True, result.Error);
        // CRITICAL: must not be void (the MS bug)
        Assert.That(result.UpdatedContent, Does.Not.Contain("void CreatePayload("),
            "Must NOT generate void return type for a value-returning extraction (Bug #12)");
        // Must have the correct return type
        Assert.That(result.UpdatedContent, Does.Contain("Payload CreatePayload("),
            "Extracted method must have 'Payload' return type");
    }

    [Test]
    [Description("Extracting a void block should correctly produce void return type")]
    public async Task ExtractMethodSafe_VoidBlock_HasVoidReturnType()
    {
        const string source = @"
public class Logger
{
    private string _log = string.Empty;

    public void Process(string msg)
    {
        var formatted = msg.Trim();
        _log += formatted;
    }
}";
        SetSource(source, "Logger.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Logger.cs", "AppendLog", "_log += formatted");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("void AppendLog("),
            "Void block extraction should produce void return type");
    }

    [Test]
    [Description("Extracting 'return value * 2' should produce int return type and include parameter")]
    public async Task ExtractMethodSafe_ReturnArithmetic_HasCorrectReturnTypeAndParameter()
    {
        const string source = @"
public class Calc
{
    public int Double(int value)
    {
        return value * 2;
    }
}";
        SetSource(source, "Calc.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Calc.cs", "ComputeDouble", "return value * 2");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("int ComputeDouble("),
            "Should have int return type (not void)");
        // 'value' flows in from outside and must become a parameter
        Assert.That(result.UpdatedContent, Does.Contain("value"),
            "Should include 'value' as a parameter in extracted method signature");
    }

    [Test]
    [Description("Extracting a string return must produce string return type")]
    public async Task ExtractMethodSafe_ReturnString_HasStringReturnType()
    {
        const string source = @"
public class Formatter
{
    public string Format(string input)
    {
        return input.Trim().ToUpperInvariant();
    }
}";
        SetSource(source, "Formatter.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Formatter.cs", "NormalizeInput", "return input.Trim().ToUpperInvariant()");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("string NormalizeInput("),
            "Should have string return type");
    }

    [Test]
    [Description("Invalid method name returns Fail with a descriptive error")]
    public async Task ExtractMethodSafe_InvalidMethodName_ReturnsFail()
    {
        const string source = @"
public class C
{
    public void M()
    {
        int x = 1;
    }
}";
        SetSource(source, "C.cs");

        var result = await _engine.ExtractMethodSafeAsync("C.cs", "123Invalid", "int x = 1");

        Assert.That(result.Success, Is.False,
            "Invalid C# identifier should return Fail");
        Assert.That(result.Error, Does.Contain("123Invalid").Or.Contain("identifier"),
            "Error should mention the invalid name");
    }

    [Test]
    [Description("contextSnippet that doesn't exist in the file returns Fail")]
    public async Task ExtractMethodSafe_SnippetNotFound_ReturnsFail()
    {
        const string source = @"
public class C
{
    public void M()
    {
        int x = 1;
    }
}";
        SetSource(source, "C.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "C.cs", "ExtractedMethod", "this snippet does not exist anywhere");

        Assert.That(result.Success, Is.False,
            "A snippet that cannot be found should return Fail");
    }

    [Test]
    [Description("File not in the loaded solution returns Fail with a descriptive error")]
    public async Task ExtractMethodSafe_FileNotInSolution_ReturnsFail()
    {
        SetSource("public class C { }", "C.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            @"C:\not\in\solution.cs", "NewMethod", "some code");

        Assert.That(result.Success, Is.False,
            "File not in solution should return Fail");
        Assert.That(result.Error, Does.Contain("solution").Or.Contain("not found"),
            "Error should explain the file is not in the loaded solution");
    }

    [Test]
    [Description("Static method extraction preserves static modifier on extracted method")]
    public async Task ExtractMethodSafe_InStaticMethod_ExtractedMethodIsAlsoStatic()
    {
        const string source = @"
public class MathHelper
{
    public static int Compute(int input)
    {
        return input * input;
    }
}";
        SetSource(source, "MathHelper.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "MathHelper.cs", "Square", "return input * input");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("static"),
            "Extracted method inside a static method should also be static");
    }

    [Test]
    [Description("Call site in the original method is replaced with the extracted method call")]
    public async Task ExtractMethodSafe_ReplacesCallSiteCorrectly()
    {
        const string source = @"
public class Counter
{
    public int Triple(int n)
    {
        return n * 3;
    }
}";
        SetSource(source, "Counter.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Counter.cs", "ComputeTriple", "return n * 3");

        Assert.That(result.Success, Is.True, result.Error);
        // The original return statement should be replaced with a call to the new method
        Assert.That(result.UpdatedContent, Does.Contain("return ComputeTriple("),
            "Original call site should call the extracted method");
        // The extracted method body should contain the original code
        Assert.That(result.UpdatedContent, Does.Contain("n * 3"),
            "Extracted method should contain the original statements");
    }
}
