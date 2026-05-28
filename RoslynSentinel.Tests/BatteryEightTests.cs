#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #8 — Tests for five engines at 5-6 mention coverage:
///   A. InventoryEngine              (2 tests) — GetCodeInventory
///   B. ModernizationUpgradeEngine   (3 tests) — UseSpan stub, UpgradePatternMatching, UseThrowExpressions stub
///   C. DependencyEngine             (3 tests) — GetProjectDependencies, unknown project throws, FindUnusedRefs
///   D. ModernLoggingEngine          (3 tests) — ConvertToSourceGeneratedLogging (real, no calls, unknown class)
///   E. IDEStyleEngine               (3 tests) — SimplifyMemberAccess, UseObjectInitializers, UseNullPropagation stub
///
/// Total: 14 tests.
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. InventoryEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class InventoryEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private InventoryEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new InventoryEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task GetCodeInventory_ValidFile_ReportsAllMemberTypes()
    {
        SetSource(@"
namespace ExpressRecipe.Services
{
    public interface IProductService { }
    
    public class ProductService : IProductService
    {
        public string Name { get; set; }
        public int Count { get; set; }
        
        public void Create() { }
        public async Task<string> GetAsync() { return """"; }
    }
}");
        var report = await _engine.GetCodeInventoryAsync("Test.cs");

        Assert.That(report.FilePath, Is.EqualTo("Test.cs"));
        Assert.That(report.Namespaces, Contains.Item("ExpressRecipe.Services"));
        Assert.That(report.Classes, Contains.Item("ProductService"));
        Assert.That(report.Interfaces, Contains.Item("IProductService"));
        Assert.That(report.Methods, Contains.Item("Create"));
        Assert.That(report.Methods, Contains.Item("GetAsync"));
        Assert.That(report.Properties, Contains.Item("Name"));
        Assert.That(report.Properties, Contains.Item("Count"));
    }

    [Test]
    public void GetCodeInventory_UnknownFile_Throws()
    {
        SetSource("public class Foo { }");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GetCodeInventoryAsync("NonExistent.cs"));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. ModernizationUpgradeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ModernizationUpgradeEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ModernizationUpgradeEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ModernizationUpgradeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task UseSpanForParsing_MethodFound_TransformsSubstring()
    {
        // UseSpanForParsing is fully implemented — replaces Substring with AsSpan().ToString()
        SetSource(@"
public class Parser
{
    public string Process(string input) { return input.Substring(1); }
}");
        var result = await _engine.UseSpanForParsingAsync("Test.cs", "Process");
        Assert.That(result, Does.Contain("Process"), "Method name should be preserved");
        Assert.That(result, Does.Contain("AsSpan"), "Substring should be replaced with AsSpan");
        Assert.That(result, Does.Not.Contain("Substring"), "Original Substring call should be gone");
    }

    [Test]
    public async Task UpgradePatternMatching_OldStyleIsPattern_ReturnsNonNullString()
    {
        // PatternMatchingRewriter runs on any file — this is a smoke test confirming
        // the method completes without throwing regardless of match/no-match
        SetSource(@"
public class Processor
{
    public void Process(object obj)
    {
        if (obj is string)
        {
            var s = (string)obj;
            System.Console.WriteLine(s);
        }
    }
}");
        var result = await _engine.UpgradePatternMatchingAsync("Test.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("Process"), "Method name should be preserved");
        Assert.That(result.Length, Is.GreaterThan(10), "Should return non-empty code");
    }

    [Test]
    public async Task UseThrowExpressions_AnyInput_ReturnsRootUnchanged()
    {
        // ThrowExpressionRewriter is a stub — null-check detection implemented but
        // the conversion is not applied (returns base.VisitIfStatement unchanged)
        SetSource(@"
public class Guard
{
    public void Validate(string x)
    {
        if (x == null) throw new ArgumentNullException(""x"");
    }
}");
        var result = await _engine.UseThrowExpressionsAsync("Test.cs");

        Assert.That(result, Is.Not.Null.And.Not.Empty);
        Assert.That(result, Does.Contain("Validate"), "Method name should be preserved");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. DependencyEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DependencyEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private DependencyEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DependencyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GetProjectDependencies_KnownInMemoryProject_ReturnsEmptyLists()
    {
        // AdhocWorkspace projects have no ProjectReferences and no .csproj file
        var solution = TestSolutionBuilder.CreateSolutionWithProject("OrderService",
            [("Order.cs", "public class Order { }")]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.GetProjectDependenciesAsync("OrderService");

        Assert.That(report.ProjectReferences, Is.Empty, "In-memory project has no project references");
        Assert.That(report.PackageReferences, Is.Empty, "In-memory project has no .csproj file to parse");
    }

    [Test]
    public void GetProjectDependencies_UnknownProject_Throws()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Test.cs", "public class Foo { }")]);
        _workspaceManager.SetTestSolution(solution);

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GetProjectDependenciesAsync("NonExistent"));
    }

    [Test]
    public async Task FindUnusedReferences_InMemoryProject_ReturnsEmpty()
    {
        // AdhocWorkspace compilation has no CompilationReference typed references (metadata only)
        // — FindUnusedReferences returns empty because the cast check for CompilationReference fails
        var solution = TestSolutionBuilder.CreateSolutionWithProject("PriceService",
            [("Price.cs", "public class Price { public decimal Amount { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindUnusedReferencesAsync("PriceService");

        Assert.That(result, Is.Empty, "AdhocWorkspace has no CompilationReferences to flag as unused");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. ModernLoggingEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ModernLoggingEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ModernLoggingEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ModernLoggingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

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
        var result = await _engine.ConvertToSourceGeneratedLoggingAsync("AuthService.cs", "AuthService");

        Assert.That(result, Does.Contain("partial"), "Class should be made partial");
        Assert.That(result, Does.Contain("LogInformationEvent1"), "Should generate LogInformationEvent1 method");
        Assert.That(result, Does.Contain("LoggerMessage"), "Should add [LoggerMessage] attribute");
        // Original direct logger call is replaced with generated method call
        Assert.That(result, Does.Not.Contain("_logger.LogInformation("), "Original LogInformation call should be replaced");
    }

    [Test]
    public async Task ConvertToSourceGeneratedLogging_NoLoggerCalls_ReturnsSourceUnchanged()
    {
        SetSource(@"
public class UserService
{
    public string GetName() { return ""Alice""; }
}", "UserService.cs");
        var result = await _engine.ConvertToSourceGeneratedLoggingAsync("UserService.cs", "UserService");

        Assert.That(result, Does.Contain("GetName"), "Method should be unchanged");
        Assert.That(result, Does.Not.Contain("partial"), "No logging calls — class should not be made partial");
        Assert.That(result, Does.Not.Contain("LoggerMessage"), "Should not generate LoggerMessage attribute");
    }

    [Test]
    public void ConvertToSourceGeneratedLogging_UnknownClass_Throws()
    {
        SetSource(@"public class Foo { }", "Foo.cs");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertToSourceGeneratedLoggingAsync("Foo.cs", "NonExistentClass"));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// E. IDEStyleEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class IDEStyleEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private IDEStyleEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new IDEStyleEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

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

        Assert.That(result, Does.Not.Contain("this."), "this. qualifiers should be removed");
        Assert.That(result, Does.Contain("_count"), "Field name should be preserved");
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

        Assert.That(result, Does.Contain("Name"), "Property Name should be in initializer");
        Assert.That(result, Does.Contain("Price"), "Property Price should be in initializer");
        // Both assignments should be collapsed into object initializer — no separate assignment statements
        Assert.That(result, Does.Not.Match(@"p\.Name\s*="), "Separate p.Name assignment should be removed");
        Assert.That(result, Does.Not.Match(@"p\.Price\s*="), "Separate p.Price assignment should be removed");
    }

    [Test]
    public async Task UseNullPropagation_AnyInput_ReturnsNonEmptySource()
    {
        // UseNullPropagationAsync is a stub — returns root unchanged
        SetSource(@"
public class Checker
{
    public void Check(string s)
    {
        if (s != null) s.ToUpperInvariant();
    }
}");
        var result = await _engine.UseNullPropagationAsync("Test.cs");

        Assert.That(result, Is.Not.Null.And.Not.Empty);
        Assert.That(result, Does.Contain("Check"), "Method name should be preserved in stub output");
    }
}
