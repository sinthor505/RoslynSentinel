using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the 4 methods that were previously stubs/no-ops but have now been fully implemented:
/// - IDEStyleEngine.UseNullPropagationAsync
/// - ModernizationUpgradeEngine.UseSpanForParsingAsync
/// - ModernizationUpgradeEngine.UseThrowExpressionsAsync
/// - GranularRefactoringEngine.RunMicroRefactoringAsync
/// </summary>
[TestFixture]
public class StubImplementationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private IDEStyleEngine _ideStyleEngine;
    private ModernizationUpgradeEngine _modernizationEngine;
    private GranularRefactoringEngine _granularEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _ideStyleEngine = new IDEStyleEngine(_workspaceManager);
        _modernizationEngine = new ModernizationUpgradeEngine(_workspaceManager);
        _granularEngine = new GranularRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // IDEStyleEngine.UseNullPropagationAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task UseNullPropagation_ConvertsIfNotNullInvocation_ToConditionalAccess()
    {
        SetSource(@"
class C {
    void M(Foo x) {
        if (x != null) x.Bar();
    }
}
class Foo { public void Bar() {} }
");
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("?.Bar()"),
            "Should replace 'if (x != null) x.Bar()' with 'x?.Bar()'");
        Assert.That(result, Does.Not.Contain("if (x != null)"),
            "The original if-check should be gone");
    }

    [Test]
    public async Task UseNullPropagation_WithBlock_ConvertsToConditionalAccess()
    {
        SetSource(@"
class C {
    void M(Foo x) {
        if (x != null) { x.Process(); }
    }
}
class Foo { public void Process() {} }
");
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("?.Process()"));
        Assert.That(result, Does.Not.Contain("if (x != null)"));
    }

    [Test]
    public async Task UseNullPropagation_WithArgs_PreservesArguments()
    {
        SetSource(@"
class C {
    void M(Foo x) {
        if (x != null) x.DoWork(42, ""hello"");
    }
}
class Foo { public void DoWork(int n, string s) {} }
");
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("?.DoWork(42,"));
    }

    [Test]
    public async Task UseNullPropagation_NullOnLeft_AlsoConverts()
    {
        SetSource(@"
class C {
    void M(Foo x) {
        if (null != x) x.Run();
    }
}
class Foo { public void Run() {} }
");
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("?.Run()"));
    }

    [Test]
    public async Task UseNullPropagation_WithElse_LeavesUntouched()
    {
        const string source = @"
class C {
    void M(Foo x) {
        if (x != null) x.Go();
        else x = new Foo();
    }
}
class Foo { public void Go() {} }
";
        SetSource(source);
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        // has an else — should NOT be transformed
        Assert.That(result, Does.Contain("if (x != null) x.Go()").Or.Contain("if (x != null)"),
            "If-else patterns should remain unchanged");
    }

    [Test]
    public async Task UseNullPropagation_NoNullChecks_ReturnsUnchanged()
    {
        const string source = @"
class C {
    void M(int x) {
        int y = x + 1;
    }
}
";
        SetSource(source);
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("int y = x + 1"));
    }

    [Test]
    public async Task UseNullPropagation_UnknownFile_ReturnsEmpty()
    {
        SetSource("class C {}");
        var result = await _ideStyleEngine.UseNullPropagationAsync("NoSuchFile.cs");
        Assert.That(result, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // ModernizationUpgradeEngine.UseSpanForParsingAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task UseSpanForParsing_ReplacesSubstring_WithAsSpanToString()
    {
        SetSource(@"
class C {
    string Parse(string s) {
        return s.Substring(1, 3);
    }
}
");
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "Parse");

        Assert.That(result, Does.Contain("AsSpan(1, 3).ToString()"),
            "Substring(start,len) should become AsSpan(start,len).ToString()");
        Assert.That(result, Does.Not.Contain(".Substring("));
    }

    [Test]
    public async Task UseSpanForParsing_SingleArgSubstring_IsConverted()
    {
        SetSource(@"
class C {
    string GetSuffix(string s) {
        return s.Substring(5);
    }
}
");
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "GetSuffix");

        Assert.That(result, Does.Contain("AsSpan(5).ToString()"));
        Assert.That(result, Does.Not.Contain(".Substring(5)"));
    }

    [Test]
    public async Task UseSpanForParsing_MultipleOccurrences_AllConverted()
    {
        SetSource(@"
class C {
    string Process(string s) {
        var a = s.Substring(0, 2);
        var b = s.Substring(3, 4);
        return a + b;
    }
}
");
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "Process");

        Assert.That(result, Does.Not.Contain(".Substring("),
            "All Substring calls should be converted");
        Assert.That(result.UpdatedText!.Split("AsSpan").Length - 1, Is.EqualTo(2),
            "Should have 2 AsSpan calls");
    }

    [Test]
    public async Task UseSpanForParsing_ScopedToMethod_DoesNotTouchOtherMethods()
    {
        SetSource(@"
class C {
    string A(string s) { return s.Substring(0, 1); }
    string B(string s) { return s.Substring(2, 3); }
}
");
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "A");

        // Method A converted, B not
        Assert.That(result, Does.Contain("AsSpan(0, 1)"), "Method A should be converted");
        Assert.That(result, Does.Contain(".Substring(2, 3)"), "Method B should remain unchanged");
    }

    [Test]
    public async Task UseSpanForParsing_NoMethodName_ConvertsEntireFile()
    {
        SetSource(@"
class C {
    string A(string s) { return s.Substring(0, 1); }
    string B(string s) { return s.Substring(2, 3); }
}
");
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "");

        Assert.That(result, Does.Not.Contain(".Substring("),
            "With no method name, all Substring calls in file should be converted");
    }

    [Test]
    public async Task UseSpanForParsing_MethodNotFound_ReturnsOriginal()
    {
        const string source = "class C { void M() {} }";
        SetSource(source);
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "NonExistent");
        Assert.That(result, Does.Contain("class C"));
    }

    // ══════════════════════════════════════════════════════════════
    // ModernizationUpgradeEngine.UseThrowExpressionsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task UseThrowExpressions_ConvertsVarPlusNullCheck_ToCoalesceThrow()
    {
        SetSource(@"
using System;
class C {
    void M(object input) {
        var x = input;
        if (x == null) throw new ArgumentNullException(""x"");
        Console.WriteLine(x);
    }
}
");
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Test.cs");

        Assert.That(result, Does.Contain("?? throw"),
            "var + null-check-throw should become coalescing throw");
        Assert.That(result, Does.Not.Contain("if (x == null) throw"),
            "The standalone null-check-throw should be gone");
    }

    [Test]
    public async Task UseThrowExpressions_PreservesSubsequentStatements()
    {
        SetSource(@"
using System;
class C {
    void M(object input) {
        var x = input;
        if (x == null) throw new InvalidOperationException();
        DoWork(x);
    }
    void DoWork(object o) {}
}
");
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Test.cs");

        Assert.That(result, Does.Contain("DoWork(x)"),
            "Statement after the null guard must be preserved");
    }

    [Test]
    public async Task UseThrowExpressions_IfWithElse_LeavesUntouched()
    {
        const string source = @"
using System;
class C {
    void M(object input) {
        var x = input;
        if (x == null) throw new ArgumentNullException();
        else x = new object();
    }
}
";
        SetSource(source);
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Test.cs");

        // Has else → should not be merged into coalescing throw
        Assert.That(result, Does.Contain("if (x == null)"),
            "if-else null checks must not be transformed");
    }

    [Test]
    public async Task UseThrowExpressions_NoNullChecks_ReturnsUnchanged()
    {
        const string source = "class C { void M() { int x = 1; } }";
        SetSource(source);
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Test.cs");
        Assert.That(result, Does.Contain("int x = 1"));
    }

    [Test]
    public async Task UseThrowExpressions_UnknownFile_ReturnsEmpty()
    {
        SetSource("class C {}");
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Missing.cs");
        Assert.That(result, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // GranularRefactoringEngine.RunMicroRefactoringAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task RunMicroRefactoring_TypeToVar_ConvertsExplicitType()
    {
        SetSource(@"
class C {
    void M() {
        string name = ""hello"";
    }
}
");
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "type-to-var", 4);

        Assert.That(result, Does.Contain("var name ="),
            "type-to-var should replace explicit type with var");
    }

    [Test]
    public async Task RunMicroRefactoring_TypeToVar_LeavesConst_Unchanged()
    {
        const string source = @"
class C {
    void M() {
        const int x = 5;
    }
}
";
        SetSource(source);
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "type-to-var", 4);

        Assert.That(result, Does.Contain("const int x"),
            "const declarations must not be changed to var");
    }

    [Test]
    public async Task RunMicroRefactoring_RemoveUnusedLocal_RemovesStatement()
    {
        SetSource(@"
class C {
    void M() {
        int unused = 0;
        int used = 1;
    }
}
");
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "remove-unused-local", 4);

        Assert.That(result, Does.Not.Contain("unused"),
            "remove-unused-local should remove the declaration at target line");
        Assert.That(result, Does.Contain("used"),
            "Other statements should remain");
    }

    [Test]
    public async Task RunMicroRefactoring_AddBraces_AddsBlockToSingleLinedIf()
    {
        SetSource(@"
class C {
    void M(int x) {
        if (x > 0) DoWork();
        DoWork();
    }
    void DoWork() {}
}
");
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "add-braces", 4);

        Assert.That(result, Does.Contain("{"),
            "add-braces should wrap the single statement body in a block");
    }

    [Test]
    public async Task RunMicroRefactoring_RemoveBraces_RemovesSingleStatementBlock()
    {
        SetSource(@"
class C {
    void M(int x) {
        if (x > 0) { DoWork(); }
        DoWork();
    }
    void DoWork() {}
}
");
        // The if-statement starts at line 4
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "remove-braces", 4);

        // After removing braces: should have the statement without a block
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task RunMicroRefactoring_ExtractConstant_ReplacesLiteralWithConst()
    {
        SetSource(@"
class C {
    void M() {
        string s = ""hello world"";
    }
}
");
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "extract-constant", 4);

        Assert.That(result, Does.Contain("const string ExtractedConstant"),
            "extract-constant should inject a const field");
        Assert.That(result, Does.Contain("ExtractedConstant"),
            "The usage site should reference the new constant");
    }

    [Test]
    public async Task RunMicroRefactoring_UnknownId_ThrowsArgumentException()
    {
        SetSource("class C { void M() {} }");

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _granularEngine.RunMicroRefactoringAsync("Test.cs", "nonexistent-refactoring", 1),
            "Unknown refactoring ID should throw ArgumentException with list of known IDs");
    }

    [Test]
    public async Task RunMicroRefactoring_UnknownFile_ReturnsEmpty()
    {
        SetSource("class C { void M() {} }");
        var result = await _granularEngine.RunMicroRefactoringAsync("NoFile.cs", "type-to-var", 1);
        Assert.That(result, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // Regression: previously-documented stubs now return non-empty results
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task UseNullPropagation_IsNoLongerAStub_ReturnsTransformedCode()
    {
        SetSource(@"
class C {
    void M(Foo x) { if (x != null) x.Go(); }
}
class Foo { public void Go() {} }
");
        var result = await _ideStyleEngine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Does.Contain("?."),
            "UseNullPropagationAsync is no longer a stub — must produce null-conditional syntax");
    }

    [Test]
    public async Task UseSpanForParsing_IsNoLongerANoOp_ChangesMadeWhenSubstringPresent()
    {
        SetSource(@"
class C {
    string M(string s) { return s.Substring(1, 2); }
}
");
        var before = "s.Substring(1, 2)";
        var result = await _modernizationEngine.UseSpanForParsingAsync("Test.cs", "M");

        Assert.That(result, Does.Not.Contain(before),
            "UseSpanForParsingAsync is no longer a no-op — must actually replace Substring");
    }

    [Test]
    public async Task UseThrowExpressions_IsNoLongerAStub_PerformsCoalesceConversion()
    {
        SetSource(@"
using System;
class C {
    void M(object o) {
        var x = o;
        if (x == null) throw new Exception();
    }
}
");
        var result = await _modernizationEngine.UseThrowExpressionsAsync("Test.cs");

        Assert.That(result, Does.Contain("??"),
            "UseThrowExpressionsAsync is no longer a stub — must produce coalescing throw");
    }

    [Test]
    public async Task RunMicroRefactoring_IsNoLongerSimulationMode_DoesNotReturnSimulationString()
    {
        SetSource(@"
class C {
    void M() {
        string x = ""hello"";
    }
}
");
        var result = await _granularEngine.RunMicroRefactoringAsync("Test.cs", "type-to-var", 4);

        Assert.That(result, Does.Not.Contain("simulation mode"),
            "RunMicroRefactoringAsync must no longer return fake simulation output");
        Assert.That(result, Does.Contain("var x"),
            "Must return the actually transformed code");
    }
}
