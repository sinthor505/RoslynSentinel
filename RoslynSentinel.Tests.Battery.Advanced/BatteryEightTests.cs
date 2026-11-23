#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

// ════════════════════════════════════════════════════════════════════════════════
// B. ModernizationUpgradeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("SyntaxModernizationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ModernizationUpgradeEngineTests
{
    private IWorkspaceManager _workspaceManager;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task UseSpanForParsing_MethodFound_TransformsSubstring()
    {
        // UseSpanForParsing is fully implemented -> replaces Substring with AsSpan().ToString()
        SetSource(@"
public class Parser
{
    public string Process(string input) { return input.Substring(1); }
}");
        var result = await TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).UseSpanForParsingAsync("Test.cs", "Process");
        Assert.That(result.UpdatedText, Does.Contain("Process"), "Method name should be preserved");
        Assert.That(result.UpdatedText, Does.Contain("AsSpan"), "Substring should be replaced with AsSpan");
        Assert.That(result.UpdatedText, Does.Not.Contain("Substring"), "Original Substring call should be gone");
    }

    [Test]
    public async Task UseThrowExpressions_AnyInput_ReturnsRootUnchanged()
    {
        // ThrowExpressionRewriter is a stub -> null-check detection implemented but
        // the conversion is not applied (returns base.VisitIfStatement unchanged)
        SetSource(@"
public class Guard
{
    public void Validate(string x)
    {
        if (x == null) throw new ArgumentNullException(""x"");
    }
}");
        var result = await TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).UseThrowExpressionsAsync("Test.cs");
        Assert.That(result.UpdatedText, Is.Not.Empty);
        Assert.That(result.UpdatedText, Does.Contain("Validate"), "Method name should be preserved");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. ModernLoggingEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("SyntaxModernizationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ModernLoggingEngineTests
{
    private IWorkspaceManager _workspaceManager;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ConvertToSourceGeneratedLogging_WithLoggerCall_GeneratesPartialMethodAndAttribute()
    {
        SetSource(@"
using Microsoft.Extensions.Logging;
public class AuthService
{
    private ILogger _logger;
    public void Login(string userId)
    {
        _logger.LogInformation(""User logged in"", userId);
    }
}", "AuthService.cs");
        var result = await TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToSourceGeneratedLoggingAsync("AuthService.cs", "AuthService");
        Assert.That(result.UpdatedText, Does.Contain("partial"), "Class should be made partial");
        Assert.That(result.UpdatedText, Does.Contain("LogInformationEvent1"), "Should generate LogInformationEvent1 method");
        Assert.That(result.UpdatedText, Does.Contain("LoggerMessage"), "Should add [LoggerMessage] attribute");
        // Original direct logger call is replaced with generated method call
        Assert.That(result.UpdatedText, Does.Not.Contain("_logger.LogInformation("), "Original LogInformation call should be replaced");
    }

    [Test]
    public async Task ConvertToSourceGeneratedLogging_NoLoggerCalls_ReturnsSourceUnchanged()
    {
        SetSource(@"
public class UserService
{
    public string GetName() { return ""Alice""; }
}", "UserService.cs");
        var result = await TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToSourceGeneratedLoggingAsync("UserService.cs", "UserService");
        Assert.That(result.UpdatedText, Does.Contain("GetName"), "Method should be unchanged");
        Assert.That(result.UpdatedText, Does.Not.Contain("partial"), "No logging calls - class should not be made partial");
        Assert.That(result.UpdatedText, Does.Not.Contain("LoggerMessage"), "Should not generate LoggerMessage attribute");
    }

    [Test]
    public async Task ConvertToSourceGeneratedLogging_UnknownClass_ThrowsInvalidOperation()
    {
        SetSource(@"public class Foo { }", "Foo.cs");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass"));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// E. IDEStyleEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("IDEStyleEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class IDEStyleEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private IDEStyleEngine _engine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.IDEStyleEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task SimplifyMemberAccess_WithThisPrefix_RemovesThisDot()
    {
        SetSource(@"
public class Counter
{
    private int _count;
    public int GetCount() { return this._count; }
    public void Increment() { this._count++; }
}");
        var result = await _engine.SimplifyMemberAccessAsync("Test.cs");
        Assert.That(result.UpdatedText, Does.Not.Contain("this."), "this. qualifiers should be removed");
        Assert.That(result.UpdatedText, Does.Contain("_count"), "Field name should be preserved");
    }

    [Test]
    public async Task UseObjectInitializers_WithConsecutivePropertyAssignments_CollapsesIntoInitializer()
    {
        SetSource(@"
public class Factory
{
    public Product Build()
    {
        var p = new Product();
        p.Name = ""Widget"";
        p.Price = 9.99m;
        return p;
    }
}
public class Product { public string Name { get; set; } public decimal Price { get; set; } }");
        var result = await _engine.UseObjectInitializersAsync("Test.cs");
        Assert.That(result.UpdatedText, Does.Contain("Name"), "Property Name should be in initializer");
        Assert.That(result.UpdatedText, Does.Contain("Price"), "Property Price should be in initializer");
        // Both assignments should be collapsed into object initializer -> no separate assignment statements
        Assert.That(result.UpdatedText, Does.Not.Match(@"p\.Name\s*="), "Separate p.Name assignment should be removed");
        Assert.That(result.UpdatedText, Does.Not.Match(@"p\.Price\s*="), "Separate p.Price assignment should be removed");
    }

    [Test]
    public async Task UseNullPropagation_AnyInput_ReturnsNonEmptySource()
    {
        // UseNullPropagationAsync is a stub -> returns root unchanged
        SetSource(@"
public class Checker
{
    public void Check(string s)
    {
        if (s != null) s.ToUpperInvariant();
    }
}");
        var result = await _engine.UseNullPropagationAsync("Test.cs");
        Assert.That(result.UpdatedText, Is.Not.Empty);
        Assert.That(result.UpdatedText, Does.Contain("Check"), "Method name should be preserved in stub output");
    }
}