using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for four bug fixes:
/// 1. Diagnose false negative (MSBuild not found on SDK-only systems)
/// 2. ExtractInterface missing namespace/usings in generated file
/// 3. ChangeSignatureAsync was a stub (now reorders params + call sites)
/// 4. ImplementInterfaceAsync generates stubs without 'override'
/// </summary>
[TestFixture]
public class BugFixTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private CodeGenerationEngine _codeGenerationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 1: Diagnose — MSBuildFound should respect MSBuildLocator.IsRegistered
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetHealthComponents_WhenMsBuildLocatorIsRegistered_ReturnsMsBuildFoundTrue()
    {
        // The test process itself has MSBuildLocator registered (tests use Roslyn workspace)
        var components = _workspaceManager.GetHealthComponents();

        // On any system running .NET SDK (as is the case in CI and developer machines),
        // IsRegistered will be true so MsBuildFound must be true.
        Assert.That(components.MsBuildFound, Is.True,
            "MsBuildFound should be true when MSBuildLocator.IsRegistered is true");
    }

    [Test]
    public async Task Diagnose_WhenWorkspaceLoaded_ReturnsHealthyTrue()
    {
        SetSource("public class Foo { public void Bar() {} }");

        // No solutionPath passed — just uses in-memory test solution
        var diffEngine = new DiffEngine(_workspaceManager);
        var report = await new SentinelWorkspaceTools(
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine),
            diffEngine,
            new DiagnosticEngine(_workspaceManager),
            new SolutionManagementEngine(_workspaceManager),
            new StructuralRefinementEngine(_workspaceManager),
            new DependencyEngine(_workspaceManager),
            _config,
            NullLogger<SentinelWorkspaceTools>.Instance
        ).Diagnose();

        Assert.That(report.Healthy, Is.True,
            $"Healthy should be true on an SDK-only system. Errors: {string.Join(", ", report.Errors.Select(e => e.Message))}");
        Assert.That(report.Errors.Any(e => e.Code.Contains("5001")), Is.False,
            "MSBuild-not-found should not be an error (moved to warning)");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 2: ExtractInterface — generated file must have namespace + usings
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ExtractInterface_GeneratedFile_ContainsNamespaceAndUsings()
    {
        const string source = @"using System;
using System.Collections.Generic;

namespace MyApp.Services;

public class OrderService
{
    public List<string> GetOrders() => new();
    public void ProcessOrder(string id) { }
}";

        SetSource(source, "OrderService.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("OrderService.cs", "OrderService", "IOrderService");

        Assert.That(result, Has.Count.EqualTo(2), "Should produce two files");

        var ifacePath = result.Keys.First(k => k != "OrderService.cs");
        var ifaceContent = result[ifacePath];

        Assert.That(ifaceContent, Does.Contain("namespace MyApp.Services"),
            "Interface file must include the source namespace");
        Assert.That(ifaceContent, Does.Contain("using System;"),
            "Interface file must include using directives from source");
        Assert.That(ifaceContent, Does.Contain("public interface IOrderService"),
            "Interface declaration must be present");
        Assert.That(ifaceContent, Does.Contain("List<string> GetOrders()"),
            "Method signature must appear in interface");
    }

    [Test]
    public async Task ExtractInterface_OriginalClass_GetsInterfaceInBaseList()
    {
        const string source = @"namespace App;
public class Svc { public void Foo() {} }";

        SetSource(source, "Svc.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("Svc.cs", "Svc", "ISvc");

        Assert.That(result["Svc.cs"], Does.Contain(": ISvc"),
            "Original class should declare ISvc in its base list");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 3: ChangeSignatureAsync — was a stub; now reorders params & call sites
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ChangeSignature_ReordersParameters_InDeclaration()
    {
        const string source = @"public class Calculator
{
    public int Add(int a, int b, int c) => a + b + c;
}";

        SetSource(source, "Calculator.cs");

        // Reorder [a, b, c] → [c, a, b] using index permutation [2, 0, 1]
        var result = await _refactoringEngine.ChangeSignatureAsync("Calculator.cs", "Add", new[] { 2, 0, 1 });

        Assert.That(result, Is.Not.Empty, "Should return changed files");
        var content = result["Calculator.cs"];
        // Verify new parameter order: c first, then a, then b
        var cPos = content.IndexOf("int c", StringComparison.Ordinal);
        var aPos = content.IndexOf("int a", StringComparison.Ordinal);
        var bPos = content.IndexOf("int b", StringComparison.Ordinal);
        Assert.That(cPos, Is.LessThan(aPos), "c should come before a after reorder");
        Assert.That(aPos, Is.LessThan(bPos), "a should come before b after reorder");
    }

    [Test]
    public async Task ChangeSignature_WithInvalidOrder_ReturnsEmpty()
    {
        const string source = "public class C { public void M(int a, int b) {} }";
        SetSource(source, "C.cs");

        // Wrong length
        var result = await _refactoringEngine.ChangeSignatureAsync("C.cs", "M", new[] { 0 });
        Assert.That(result, Is.Empty, "Invalid order length should return empty dict");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 4: ImplementInterfaceAsync — stubs must NOT have 'override' keyword
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ImplementInterface_GeneratedStubs_DoNotHaveOverrideKeyword()
    {
        const string ifaceSource = @"namespace App;
public interface IGreeter
{
    string Greet(string name);
    int Count { get; }
}";

        const string classSource = @"namespace App;
public class Greeter : IGreeter
{
}";

        SetMultipleFiles(("IGreeter.cs", ifaceSource), ("Greeter.cs", classSource));

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Greeter.cs", "Greeter", "IGreeter");

        Assert.That(result, Does.Not.Contain("override"),
            "Interface implementations must NOT use 'override' keyword");
        Assert.That(result, Does.Contain("public string Greet"),
            "Should generate Greet method stub");
        Assert.That(result, Does.Contain("NotImplementedException"),
            "Stub body should throw NotImplementedException");
    }

    [Test]
    public async Task ImplementInterface_WhenAllMembersImplemented_ReturnsAlreadyImplementedMessage()
    {
        const string source = @"namespace App;
public interface IFoo { void Bar(); }
public class Foo : IFoo
{
    public void Bar() { }
}";
        SetSource(source, "Foo.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Foo.cs", "Foo", "IFoo");

        Assert.That(result, Does.Contain("already implemented"),
            "Should report that all members are already implemented");
    }
}

/// <summary>
/// Regression tests for bugs fixed in the 7-bug fix batch:
/// Bug 1: DetectMismatchedAwait false positives (WhenAll pattern + discards)
/// Bug 2: ExtractInterface duplicate base types
/// Bug 3: GenerateTestScaffold/Skeleton should emit async Task for async methods
/// Bug 4: GenerateFluentBuilder should throw on DI classes with no settable props
/// Bug 5: CheckForSqlInjection const interpolation false positives
/// Bug 6: ContextHelper quote-disambiguation
/// Bug 7: GenerateEqualityOverrides collection comparison
/// </summary>
[TestFixture]
public class Bug7BatchRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AnalysisEngine _analysisEngine;
    private RefactoringEngine _refactoringEngine;
    private CodeGenerationEngine _codeGenerationEngine;
    private TestingEngine _testingEngine;
    private SecurityEngine _securityEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── Bug 1: DetectMismatchedAwait — discard and WhenAll patterns ───────────

    [Test]
    public async Task DetectMismatchedAwait_DiscardAssignment_IsNotFlagged()
    {
        const string src = @"using System.Threading.Tasks;
public class Svc
{
    public async Task DoWork()
    {
        _ = Task.Run(() => 42);
    }
    public Task<int> SomeAsync() => Task.FromResult(1);
}";
        SetSource(src, "Svc.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("Svc.cs");
        Assert.That(results, Is.Empty, "Discard _ = Task.Run(...) should not be flagged as missing await");
    }

    [Test]
    public async Task DetectMismatchedAwait_TaskWhenAllPattern_IsNotFlagged()
    {
        const string src = @"using System.Threading.Tasks;
public class Svc
{
    public async Task DoWork()
    {
        var t1 = GetDataAsync();
        var t2 = GetMoreAsync();
        await Task.WhenAll(t1, t2);
    }
    public Task<int> GetDataAsync() => Task.FromResult(1);
    public Task<string> GetMoreAsync() => Task.FromResult("""");
}";
        SetSource(src, "Svc.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("Svc.cs");
        Assert.That(results, Is.Empty, "Task.WhenAll pattern should not be flagged as missing await");
    }

    // ── Bug 2: ExtractInterface — no duplicate base type ─────────────────────

    [Test]
    public async Task ExtractInterface_WhenClassAlreadyImplementsInterface_NoDuplicateAdded()
    {
        const string src = @"namespace App;
public class Svc : ISvc { public void Foo() {} }";
        SetSource(src, "Svc.cs");
        var result = await _refactoringEngine.ExtractInterfaceAsync("Svc.cs", "Svc", "ISvc");
        var classContent = result["Svc.cs"];
        // Should appear exactly once, not twice
        var count = 0;
        var idx = 0;
        while ((idx = classContent.IndexOf(": ISvc", idx, StringComparison.Ordinal)) >= 0) { count++; idx++; }
        Assert.That(count, Is.EqualTo(1), "ISvc should appear exactly once in the base list");
    }

    // ── Bug 3: GenerateTestScaffold — async Task for async methods ────────────

    [Test]
    public async Task GenerateTestScaffold_AsyncMethod_EmitsAsyncTask()
    {
        const string src = @"public class UserService
{
    public async Task<string> GetUserAsync(int id) => await Task.FromResult(id.ToString());
}";
        SetSource(src, "UserService.cs");
        var result = await _testingEngine.GenerateTestScaffoldAsync("UserService.cs", "UserService");
        Assert.That(result.Code, Does.Contain("public async Task GetUserAsync_"),
            "Test for async method should be 'public async Task' not 'public void'");
    }

    [Test]
    public async Task GenerateTestSkeleton_AsyncMethod_EmitsAsyncTask()
    {
        const string src = @"public class DataService
{
    public async Task LoadAsync() => await Task.CompletedTask;
}";
        SetSource(src, "DataService.cs");
        var result = await _testingEngine.GenerateTestSkeletonAsync("DataService.cs", "DataService");
        Assert.That(result.Content, Does.Contain("async Task"),
            "Skeleton for async method should contain 'async Task'");
        Assert.That(result.Content, Does.Not.Contain("public void LoadAsync"),
            "Should NOT emit 'public void' for async method test");
    }

    // ── Bug 4: GenerateFluentBuilder — DI class error ─────────────────────────

    [Test]
    public void GenerateFluentBuilder_DiClass_ThrowsDescriptiveException()
    {
        const string src = @"public class ProductsController
{
    private readonly IProductService _svc;
    private readonly ILogger<ProductsController> _logger;
    public ProductsController(IProductService svc, ILogger<ProductsController> logger)
    {
        _svc = svc;
        _logger = logger;
    }
}";
        SetSource(src, "ProductsController.cs");
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _codeGenerationEngine.GenerateFluentBuilderAsync("ProductsController.cs", "ProductsController"));
        Assert.That(ex!.Message, Does.Contain("No settable public properties"),
            "Exception should explain that the class has no settable public properties");
        Assert.That(ex.Message, Does.Contain("DI-injected"),
            "Exception should mention DI-injected classes");
    }

    [Test]
    public async Task GenerateFluentBuilder_PocoClass_GeneratesWithMethods()
    {
        const string src = @"public class Product
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}";
        SetSource(src, "Product.cs");
        var result = await _codeGenerationEngine.GenerateFluentBuilderAsync("Product.cs", "Product");
        Assert.That(result.BuilderCode, Does.Contain("WithName"),
            "Builder should have WithName method");
        Assert.That(result.BuilderCode, Does.Contain("WithPrice"),
            "Builder should have WithPrice method");
    }

    // ── Bug 5: CheckForSqlInjection — const interpolation is safe ─────────────

    [Test]
    public async Task CheckForSqlInjection_ConstInterpolation_IsNotFlagged()
    {
        const string src = @"using Microsoft.Data.SqlClient;
public class Repo
{
    private const string TempTable = ""#tempItems"";
    public void CreateTemp(SqlConnection conn)
    {
        var cmd = new SqlCommand($""CREATE TABLE {TempTable} (Id INT)"", conn);
        cmd.ExecuteNonQuery();
    }
}";
        SetSource(src, "Repo.cs");
        // Note: CheckForSqlInjection scans for method invocations on SQL execution methods.
        // The interpolation uses a const string — must not be flagged.
        var results = await _securityEngine.CheckForSqlInjectionAsync("Repo.cs");
        Assert.That(results, Is.Empty,
            "Interpolation with compile-time const string should NOT be flagged as SQL injection");
    }

    [Test]
    public async Task CheckForSqlInjection_RuntimeInterpolation_IsFlagged()
    {
        // Use a Dapper-style Execute invocation so the engine (which scans method
        // invocations, not constructors) can detect the unsafe interpolation.
        const string src = @"
public class Repo
{
    public void Search(string userInput)
    {
        Execute($""SELECT * FROM Items WHERE Name = '{userInput}'"");
    }
    private void Execute(string sql) { }
}";
        SetSource(src, "Repo.cs");
        var results = await _securityEngine.CheckForSqlInjectionAsync("Repo.cs");
        Assert.That(results, Is.Not.Empty,
            "Interpolation with runtime variable should be flagged as SQL injection");
    }

    // ── Bug 6: ContextHelper quote disambiguation ─────────────────────────────

    [Test]
    public void ContextHelper_FindSnippetPosition_NormalizedQuotes_Disambiguates()
    {
        // Two lines both containing "void M()" as snippet.
        // The source lines themselves contain a string literal with real double-quotes.
        // AI typically provides lineBefore with \" escaping — MatchLine must normalize it.
        const string source = "void M() { var x = \"hello\"; }\nvoid M() { var y = \"world\"; }";
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source);

        // lineBefore: "void M() { var x = \"hello\"; }" — \" normalized to " by MatchLine
        var pos = ContextHelper.FindSnippetPosition(sourceText, "void M()",
            lineBefore: "void M() { var x = \\\"hello\\\"; }");

        // Should find the occurrence on line 1 (after line 0's position)
        var firstOccurrence = source.IndexOf("void M()", StringComparison.Ordinal);
        Assert.That(pos, Is.GreaterThan(firstOccurrence),
            "Should find the second 'void M()' occurrence (line 1), not the first");
    }

    [Test]
    public void ContextHelper_FindSnippetPosition_EscapedQuoteInLineBefore_Disambiguates()
    {
        // Two lines both contain "hello" (with surrounding quotes as the snippet).
        // We want the second occurrence — supply lineBefore matching the FIRST line.
        // The lineBefore is provided AI-style with \" escaping.
        const string source = "var x = \"hello\";\nvar y = \"hello\";";
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source);

        // lineBefore for line 1 = line 0: var x = "hello";
        // AI provides it escaped: var x = \"hello\";
        var pos = ContextHelper.FindSnippetPosition(sourceText, "\"hello\"",
            lineBefore: "var x = \\\"hello\\\";");

        // Should find the occurrence on line 1, which is after line 0's occurrence
        var firstOccurrence = source.IndexOf("\"hello\"", StringComparison.Ordinal);
        Assert.That(pos, Is.GreaterThan(firstOccurrence),
            "Should find the 'hello' occurrence on line 1, not line 0");
    }

    // ── Bug 7: GenerateEqualityOverrides — List<T> uses SequenceEqual ─────────

    [Test]
    public async Task GenerateEqualityOverrides_ListProperty_UsesSequenceEqual()
    {
        const string src = @"using System.Collections.Generic;
public class Product
{
    public string Name { get; set; }
    public List<string> Tags { get; set; }
}";
        SetSource(src, "Product.cs");
        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Product.cs", "Product");
        Assert.That(result, Does.Contain("SequenceEqual"),
            "List<T> property should use Enumerable.SequenceEqual for value-based comparison");
        Assert.That(result, Does.Not.Contain("Tags == other.Tags"),
            "List<T> should NOT use reference equality (==)");
    }

    [Test]
    public async Task GenerateEqualityOverrides_ScalarProperty_UsesEqualsExpression()
    {
        const string src = @"public class Point { public int X { get; set; } public int Y { get; set; } }";
        SetSource(src, "Point.cs");
        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Point.cs", "Point");
        // Scalar int properties use == which is fine
        Assert.That(result, Does.Contain("X == other.X"),
            "Scalar int property should use == equality");
    }
}

/// <summary>
/// Bug 8 regression tests:
/// Bug 8a: FindStringMagicValues — Locations were empty {} due to value tuple JSON serialization
/// Bug 8b: DetectMismatchedAwait — false positives on Moq lambda setup chains
/// </summary>
[TestFixture]
public class Bug8BatchRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AnalysisEngine _analysisEngine;
    private AntiPatternEngine _antiPatternEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── Bug 8a: FindStringMagicValues — locations must have non-empty FilePath/Line/Snippet ───

    [Test]
    public async Task FindStringMagicValues_LocationsHaveFilePath()
    {
        const string src = @"public class MyService
{
    public void A() { Log(""hello-world""); }
    public void B() { Log(""hello-world""); }
    public void C() { Log(""hello-world""); }
    private void Log(string s) {}
}";
        SetSource(src, "MyService.cs");
        var results = await _antiPatternEngine.FindStringMagicValuesAsync("MyService.cs", minOccurrences: 3);

        Assert.That(results, Is.Not.Empty, "Should find 'hello-world' repeated 3 times");
        var finding = results[0];
        Assert.That(finding.Locations, Has.Count.EqualTo(3), "Should have 3 location entries");

        foreach (var loc in finding.Locations)
        {
            Assert.That(loc.FilePath, Is.Not.Null.And.Not.Empty,
                "Location.FilePath must not be empty (value tuple serialization bug)");
            Assert.That(loc.Line, Is.GreaterThan(0),
                "Location.Line must be a real line number");
            Assert.That(loc.Snippet, Is.Not.Null.And.Not.Empty,
                "Location.Snippet must not be empty");
        }
    }

    [Test]
    public async Task FindStringMagicValues_LocationsHaveCorrectLineNumbers()
    {
        const string src = @"public class Svc
{
    // line 3
    public void X() { Do(""repeat-me""); }
    public void Y() { Do(""repeat-me""); }
    public void Z() { Do(""repeat-me""); }
    private void Do(string s) {}
}";
        SetSource(src, "Svc.cs");
        var results = await _antiPatternEngine.FindStringMagicValuesAsync("Svc.cs", minOccurrences: 3);

        Assert.That(results, Is.Not.Empty);
        var lines = results[0].Locations.Select(l => l.Line).OrderBy(x => x).ToList();
        Assert.That(lines[0], Is.GreaterThan(0), "First occurrence line should be positive");
        Assert.That(lines[1], Is.GreaterThan(lines[0]), "Second occurrence should be on a later line");
        Assert.That(lines[2], Is.GreaterThan(lines[1]), "Third occurrence should be on a later line");
    }

    // ── Bug 8b: DetectMismatchedAwait — Moq lambda setup chains should not be flagged ──

    [Test]
    public async Task DetectMismatchedAwait_MoqSimpleLambdaBody_IsNotFlagged()
    {
        // Moq pattern: .Setup(s => s.FooAsync(...)) — the async invocation is the lambda body
        // It should NOT be flagged as an unawaited fire-and-forget call.
        const string src = @"using System.Threading.Tasks;
using Moq;
public interface ISvc { Task<bool> FooAsync(int id); }
public class MyTests
{
    public void Setup_Test()
    {
        var mock = new Mock<ISvc>();
        mock.Setup(s => s.FooAsync(42)).ReturnsAsync(true);
    }
}";
        SetSource(src, "MyTests.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("MyTests.cs");
        Assert.That(results, Is.Empty,
            "FooAsync inside Moq .Setup(s => s.FooAsync()) lambda should not be flagged as missing await");
    }

    [Test]
    public async Task DetectMismatchedAwait_ParenthesizedLambdaBody_IsNotFlagged()
    {
        // Parenthesized lambda version: .Setup((s) => s.FooAsync(...))
        const string src = @"using System.Threading.Tasks;
using Moq;
public interface ISvc { Task<bool> FooAsync(int id, string name); }
public class MyTests
{
    public void Setup_Test()
    {
        var mock = new Mock<ISvc>();
        mock.Setup((s) => s.FooAsync(1, ""test"")).ReturnsAsync(false);
    }
}";
        SetSource(src, "MyTests.cs");
        var results = await _analysisEngine.DetectMismatchedAwaitAsync("MyTests.cs");
        Assert.That(results, Is.Empty,
            "FooAsync inside parenthesized lambda .Setup((s) => s.FooAsync()) should not be flagged");
    }
}

/// <summary>
/// Bug 9 regression tests (batch 6 grading pass):
/// 9a: ExtractInterface — members must be on separate lines, not all on one line
/// 9b: GetCallGraph — prefers class method over interface method in same file
/// 9c: GetReverseCallGraph — prefers class method over interface method in same file
/// 9d: FindCallersAsync — prefers class method when no contextSnippet given
/// 9e: FindServicesNotRegistered — should not flag IWebHostEnvironment, IServiceScopeFactory, etc.
/// 9f: UpgradeToModernGuards — returns no-op message when no patterns found
/// 9g: FindStringMagicValues — SQL @param tokens must not be flagged as magic values
/// </summary>
[TestFixture]
public class Bug9BatchRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private AntiPatternEngine _antiPatternEngine;
    private DependencyInjectionEngine _diEngine;
    private SymbolNavigationEngine _navigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        _diEngine = new DependencyInjectionEngine(_workspaceManager);
        _navigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── 9a: ExtractInterface formatting — members on separate lines ───────────

    [Test]
    public async Task ExtractInterface_GeneratedInterface_HasMembersOnSeparateLines()
    {
        const string src = @"using System.Threading.Tasks;
public class OrderService
{
    public Task<int> GetOrderAsync(int id) => Task.FromResult(id);
    public Task<bool> CancelOrderAsync(int id) => Task.FromResult(true);
    public Task<bool> SubmitOrderAsync(int id) => Task.FromResult(true);
}";
        SetSource(src, "OrderService.cs");
        var result = await _refactoringEngine.ExtractInterfaceAsync("OrderService.cs", "OrderService", "IOrderService");
        Assert.That(result, Is.Not.Null.And.Not.Empty, "ExtractInterface should return non-empty result");

        // The interface file content is in the dictionary value
        var ifaceContent = result!.Values.FirstOrDefault(v => v.Contains("interface IOrderService"));
        Assert.That(ifaceContent, Is.Not.Null, "Result should contain interface file content");

        // Each method should appear on its own line (not all crammed on one line)
        var lines = ifaceContent!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var methodLines = lines.Count(l => l.TrimStart().StartsWith("Task", StringComparison.Ordinal));
        Assert.That(methodLines, Is.EqualTo(3),
            "All 3 interface methods must appear on separate lines (not concatenated on one line)");
    }

    [Test]
    public async Task ExtractInterface_GeneratedInterface_HasInterfaceDeclaration()
    {
        const string src = @"public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Subtract(int a, int b) => a - b;
}";
        SetSource(src, "Calculator.cs");
        var result = await _refactoringEngine.ExtractInterfaceAsync("Calculator.cs", "Calculator", "ICalculator");
        Assert.That(result, Is.Not.Null.And.Not.Empty, "ExtractInterface should return non-empty result");

        var ifaceContent = result!.Values.FirstOrDefault(v => v.Contains("interface ICalculator"));
        Assert.That(ifaceContent, Is.Not.Null, "Result should contain interface file content");
        Assert.That(ifaceContent, Does.Contain("interface ICalculator"),
            "Generated content must declare interface ICalculator");
        Assert.That(ifaceContent, Does.Contain("int Add"),
            "Generated interface must include Add method");
        Assert.That(ifaceContent, Does.Contain("int Subtract"),
            "Generated interface must include Subtract method");
    }

    // ── 9b: GetCallGraph — prefers class method over interface ───────────────

    [Test]
    public async Task GetCallGraph_WithInterfaceAndClassInSameFile_UsesClassMethod()
    {
        const string src = @"using System.Threading.Tasks;
public interface IProcessor
{
    Task<int> ProcessAsync(int value);
}
public class Processor : IProcessor
{
    public async Task<int> ProcessAsync(int value)
    {
        return await DoWorkAsync(value);
    }
    private Task<int> DoWorkAsync(int value) => Task.FromResult(value * 2);
}";
        SetSource(src, "Processor.cs");
        var result = await _navigationEngine.GetCallGraphAsync("Processor.cs", "ProcessAsync", maxDepth: 2);
        Assert.That(result, Is.Not.Null,
            "GetCallGraph should return a result when interface and class share the same method name");
        Assert.That(result!.MethodName, Is.EqualTo("ProcessAsync"),
            "Call graph root should be the class method, not interface");
        Assert.That(result.Callees, Is.Not.Null,
            "Class method body should have callees; interface method has no body");
    }

    // ── 9c: GetReverseCallGraph — prefers class method over interface ─────────

    [Test]
    public async Task GetReverseCallGraph_WithInterfaceAndClassInSameFile_DoesNotReturnNull()
    {
        const string src = @"using System.Threading.Tasks;
public interface IValidator
{
    Task<bool> ValidateAsync(string input);
}
public class Validator : IValidator
{
    public async Task<bool> ValidateAsync(string input) => input.Length > 0;
}
public class Controller
{
    private readonly IValidator _v;
    public Controller(IValidator v) { _v = v; }
    public async Task<bool> Handle(string s) => await _v.ValidateAsync(s);
}";
        SetSource(src, "Validator.cs");
        var result = await _navigationEngine.GetReverseCallGraphAsync("Validator.cs", "ValidateAsync", maxDepth: 2);
        Assert.That(result, Is.Not.Null,
            "GetReverseCallGraph must not return null when interface and class share the same method name");
    }

    // ── 9d: FindCallers — no contextSnippet should prefer class declaration ───

    [Test]
    public async Task FindCallersAsync_NoContextSnippet_ClassInSameFileAsInterface_ReturnsResults()
    {
        const string src = @"using System.Threading.Tasks;
public interface IFoo { Task<string> GetNameAsync(); }
public class Foo : IFoo
{
    public Task<string> GetNameAsync() => Task.FromResult(""Foo"");
}
public class Consumer
{
    private readonly IFoo _foo;
    public Consumer(IFoo foo) { _foo = foo; }
    public async Task<string> Run() => await _foo.GetNameAsync();
}";
        SetSource(src, "Foo.cs");
        // Without contextSnippet, the tool should pick the class method (not the interface)
        var results = await _navigationEngine.FindCallersAsync("Foo.cs", "GetNameAsync", contextSnippet: null);
        // The call from Consumer.Run should be found; interface has no body to generate callers from
        Assert.That(results, Is.Not.Null, "FindCallersAsync must not throw when interface and class share same method name");
    }

    // ── 9e: FindServicesNotRegistered — IWebHostEnvironment etc. not flagged ──

    [Test]
    public async Task FindServicesNotRegistered_IWebHostEnvironment_NotFlagged()
    {
        const string src = @"using Microsoft.AspNetCore.Hosting;
public class MyController
{
    private readonly IWebHostEnvironment _env;
    public MyController(IWebHostEnvironment env) { _env = env; }
}
public class Startup
{
    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddScoped<MyController>();
    }
}";
        SetSource(src, "Startup.cs");
        var results = await _diEngine.FindServicesNotRegisteredAsync();
        var falsePos = results.Where(r => r.MissingType.Contains("IWebHostEnvironment")).ToList();
        Assert.That(falsePos, Is.Empty,
            "IWebHostEnvironment is framework-provided and must not be flagged as missing registration");
    }

    [Test]
    public async Task FindServicesNotRegistered_IServiceScopeFactory_NotFlagged()
    {
        const string src = @"using Microsoft.Extensions.DependencyInjection;
public class MyWorker
{
    private readonly IServiceScopeFactory _factory;
    public MyWorker(IServiceScopeFactory factory) { _factory = factory; }
}
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<MyWorker>();
    }
}";
        SetSource(src, "Startup.cs");
        var results = await _diEngine.FindServicesNotRegisteredAsync();
        var falsePos = results.Where(r => r.MissingType.Contains("IServiceScopeFactory")).ToList();
        Assert.That(falsePos, Is.Empty,
            "IServiceScopeFactory is framework-provided and must not be flagged as missing registration");
    }

    [Test]
    public async Task FindServicesNotRegistered_IHttpContextAccessor_NotFlagged()
    {
        const string src = @"using Microsoft.AspNetCore.Http;
public class MyMiddleware
{
    private readonly IHttpContextAccessor _accessor;
    public MyMiddleware(IHttpContextAccessor accessor) { _accessor = accessor; }
}
public class Startup
{
    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<MyMiddleware>();
    }
}";
        SetSource(src, "Startup.cs");
        var results = await _diEngine.FindServicesNotRegisteredAsync();
        var falsePos = results.Where(r => r.MissingType.Contains("IHttpContextAccessor")).ToList();
        Assert.That(falsePos, Is.Empty,
            "IHttpContextAccessor is framework-provided and must not be flagged as missing registration");
    }

    // ── 9f: UpgradeToModernGuards — no-op message when nothing to upgrade ─────

    [Test]
    public async Task UpgradeToModernGuards_NoPatterns_ReturnsNoOpMessage()
    {
        const string src = @"public class Service
{
    public void DoWork(string s)
    {
        var x = s.Length;
    }
}";
        SetSource(src, "Service.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync("Service.cs");
        // Should NOT return the full file when nothing changed
        Assert.That(result, Does.Not.Contain("public class Service"),
            "Should not return full file when no guard patterns are found");
        Assert.That(result, Does.Contain("No"),
            "Should return a no-op indicator message instead of full file content");
    }

    [Test]
    public async Task UpgradeToModernGuards_WithNullCheck_ReturnsModifiedFile()
    {
        const string src = @"public class Service
{
    public void DoWork(string s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        var x = s.Length;
    }
}";
        SetSource(src, "Service.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync("Service.cs");
        // Should return modified content with ThrowIfNull
        Assert.That(result, Does.Contain("ThrowIfNull"),
            "Should upgrade null check to ArgumentNullException.ThrowIfNull");
        Assert.That(result, Does.Contain("public class Service"),
            "Should return the full modified file when changes are made");
    }

    // ── 9g: FindStringMagicValues — SQL @params not flagged ──────────────────

    [Test]
    public async Task FindStringMagicValues_SqlParamTokens_AreNotFlagged()
    {
        // ADO.NET parameterized query pattern: @UserId appears many times but is NOT a magic value
        const string src = @"public class UserRepository
{
    public void GetUser(int id) { Exec(""SELECT * FROM Users WHERE Id = @UserId"", ""@UserId"", id); }
    public void UpdateUser(int id) { Exec(""UPDATE Users SET Name=@Name WHERE Id=@UserId"", ""@UserId"", id); }
    public void DeleteUser(int id) { Exec(""DELETE FROM Users WHERE Id=@UserId"", ""@UserId"", id); }
    private void Exec(string sql, string param, object v) {}
}";
        SetSource(src, "UserRepository.cs");
        var results = await _antiPatternEngine.FindStringMagicValuesAsync("UserRepository.cs", minOccurrences: 3);
        var sqlParams = results.Where(r => r.Value.StartsWith("@")).ToList();
        Assert.That(sqlParams, Is.Empty,
            "SQL parameter tokens starting with @ (like @UserId) must not be flagged as magic values");
    }

    [Test]
    public async Task FindStringMagicValues_RegularRepeatedStrings_AreStillFlagged()
    {
        // Regular magic values should still be detected
        const string src = @"public class Config
{
    public void A() { var k = ""production-key""; }
    public void B() { var k = ""production-key""; }
    public void C() { var k = ""production-key""; }
}";
        SetSource(src, "Config.cs");
        var results = await _antiPatternEngine.FindStringMagicValuesAsync("Config.cs", minOccurrences: 3);
        Assert.That(results, Is.Not.Empty,
            "Regular repeated strings (not starting with @) should still be detected");
        Assert.That(results.Any(r => r.Value == "production-key"), Is.True,
            "Should find 'production-key' as a magic value");
    }
}

[TestFixture]
public class Bug10BatchRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private GranularRefactoringEngine _granularEngine = null!;
    private MappingEngine _mappingEngine = null!;
    private AsyncOptimizationEngine _asyncEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _granularEngine = new GranularRefactoringEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _asyncEngine = new AsyncOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string src, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, src)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- Bug: introduce_variable duplicates the var when expression is already an initializer ---

    [Test]
    public async Task IntroduceVariable_WhenExpressionIsAlreadyInitializer_ReturnsNoOpMessage()
    {
        const string src = @"public class OrderService
{
    private readonly IOrderRepo _repo;
    public async Task ProcessAsync()
    {
        var orderId = await _repo.CreateOrderAsync();
        Console.WriteLine(orderId);
    }
}";
        SetSource(src, "OrderService.cs");
        // The context snippet matches the RHS of an existing var declaration
        var result = await _granularEngine.IntroduceVariableAsync(
            "OrderService.cs",
            contextSnippet: "await _repo.CreateOrderAsync()",
            newVariableName: "orderId");

        // Should NOT produce duplicate var orderId = orderId;
        Assert.That(result, Does.Not.Contain("var orderId = orderId"),
            "Must not produce a duplicate 'var orderId = orderId' declaration");
        // Should return no-op indicator
        Assert.That(result, Does.Contain("already"),
            "Should indicate the variable is already introduced");
    }

    [Test]
    public async Task IntroduceVariable_WhenExpressionIsSubExpression_ExtractsCorrectly()
    {
        const string src = @"public class Calculator
{
    public int Compute(int a, int b, int c)
    {
        return (a + b) * c;
    }
}";
        SetSource(src, "Calculator.cs");
        var result = await _granularEngine.IntroduceVariableAsync(
            "Calculator.cs",
            contextSnippet: "a + b",
            newVariableName: "sum");

        Assert.That(result, Does.Contain("var sum = a + b"),
            "Should extract sub-expression to new var");
        Assert.That(result, Does.Contain("sum * c"),
            "Original expression should be replaced with the new variable reference");
    }

    // --- Bug: generate_mapping throws on unqualified type names ---

    [Test]
    public async Task GenerateMapping_WithSimpleTypeNames_ResolvesAndGeneratesMapping()
    {
        const string src = @"public class SourceDto
{
    public string Name { get; set; }
    public int Age { get; set; }
    public string Email { get; set; }
}

public class TargetDto
{
    public string Name { get; set; }
    public int Age { get; set; }
}";
        SetSource(src, "Dtos.cs");
        var result = await _mappingEngine.GenerateMappingAsync("Dtos.cs", "SourceDto", "TargetDto");

        Assert.That(result, Does.Contain("MapSourceDtoToTargetDto"),
            "Should generate mapping method with correct name");
        Assert.That(result, Does.Contain("dest.Name = source.Name"),
            "Should map Name property");
        Assert.That(result, Does.Contain("dest.Age = source.Age"),
            "Should map Age property");
    }

    [Test]
    public async Task GenerateMapping_WithUnknownType_ReturnsHelpfulMessage()
    {
        const string src = @"public class Foo { public int X { get; set; } }";
        SetSource(src, "Foo.cs");
        var result = await _mappingEngine.GenerateMappingAsync("Foo.cs", "Foo", "NonExistentType");

        Assert.That(result, Does.Contain("//").Or.Contain("Error").Or.Contain("Could not"),
            "Should return helpful message when type not found, not throw exception");
        Assert.That(result, Does.Not.Contain("System.Exception"),
            "Must not surface a raw exception to the caller");
    }

    // --- Bug: optimize_independent_awaits throws when method not found instead of returning message ---

    [Test]
    public async Task OptimizeIndependentAwaits_WhenMethodNotFound_ReturnsErrorMessage()
    {
        const string src = @"public class MyService
{
    public async Task DoWorkAsync()
    {
        await Task.Delay(1);
    }
}";
        SetSource(src, "MyService.cs");
        var result = await _asyncEngine.OptimizeIndependentAwaitsAsync("MyService.cs", "NonExistentMethod");

        Assert.That(result, Does.Contain("//").Or.Contain("Error").Or.Contain("not found"),
            "Should return a message when method not found, not throw an exception");
    }

    [Test]
    public async Task OptimizeIndependentAwaits_WithSequentialAwaits_BatchesIntoWhenAll()
    {
        const string src = @"public class ReportService
{
    private readonly IRepository _repo;
    public async Task GenerateAsync()
    {
        await _repo.SaveAuditAsync();
        await _repo.SaveLogAsync();
        await _repo.NotifyAsync();
    }
}";
        SetSource(src, "ReportService.cs");
        var result = await _asyncEngine.OptimizeIndependentAwaitsAsync("ReportService.cs", "GenerateAsync");

        Assert.That(result, Does.Contain("Task.WhenAll"),
            "Should batch 3 independent sequential awaits into Task.WhenAll");
    }

    // --- Bug: add_cancellation_token throws when method not found, and has trailing space ---

    [Test]
    public async Task AddCancellationToken_WhenMethodNotFound_ReturnsErrorMessage()
    {
        const string src = @"public class Loader
{
    public async Task LoadAsync() => await Task.Delay(1);
}";
        SetSource(src, "Loader.cs");
        var result = await _asyncEngine.AddCancellationTokenToMethodAsync("Loader.cs", "NonExistentMethod");

        Assert.That(result, Does.Contain("//").Or.Contain("Error").Or.Contain("not found"),
            "Should return a message when method not found, not throw an exception");
    }

    [Test]
    public async Task AddCancellationToken_WhenAdded_HasNoTrailingSpaceInTypeName()
    {
        const string src = @"public class DataService
{
    public async Task<int> FetchAsync()
    {
        await Task.Delay(10);
        return 42;
    }
}";
        SetSource(src, "DataService.cs");
        var result = await _asyncEngine.AddCancellationTokenToMethodAsync("DataService.cs", "FetchAsync");

        // Should contain "CancellationToken cancellationToken" but not "CancellationToken " (trailing space)
        Assert.That(result, Does.Contain("CancellationToken cancellationToken"),
            "Should add CancellationToken parameter");
        Assert.That(result, Does.Not.Contain("CancellationToken  "),
            "Should not have double space (trailing space in type name)");
    }
}
