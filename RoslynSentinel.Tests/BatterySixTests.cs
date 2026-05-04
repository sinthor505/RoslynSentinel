#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #6 — Functional tests for three engines with 4–5 test-mentions but no real coverage:
///   A. DocumentationEngine   (4 tests) — GenerateXmlDocStubs, DocumentPocoFields
///   B. ArchitecturalEngine   (5 tests) — ConvertToBackgroundService, FindCircularDependencies
///   C. ApiAutomationEngine   (4 tests) — GenerateHttpClientForController, return-type mapping
///
/// SolutionManagementEngine is excluded (spawns real powershell.exe processes — integration only).
///
/// Total: 13 tests. All workspace-based (SetSource / SetMultipleFiles).
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. DocumentationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DocumentationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private DocumentationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DocumentationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task GenerateXmlDocStubs_PublicMethodsWithoutDocs_AddsXmlComments()
    {
        SetSource(@"
public class OrderService
{
    public int GetOrderCount(int userId) { return 0; }
    public void DeleteOrder(int id, string reason) { }
    private void AuditLog() { }
}");
        var result = await _engine.GenerateXmlDocumentationStubsAsync("Test.cs");

        Assert.That(result, Does.Contain("/// <summary>"), "Should add XML summary tags");
        Assert.That(result, Does.Contain("GetOrderCount"), "Public method name should appear in TODO comment");
        Assert.That(result, Does.Contain("DeleteOrder"), "Second public method should also get docs");
        Assert.That(result, Does.Contain("<param name=\"userId\">"), "Should add <param> for each parameter");
        Assert.That(result, Does.Contain("<returns>"), "Non-void return should get <returns> tag");
    }

    [Test]
    public async Task GenerateXmlDocStubs_PrivateMethods_NotDocumented()
    {
        SetSource(@"
public class Auditor
{
    private void LogInternal(string msg) { }
}");
        var result = await _engine.GenerateXmlDocumentationStubsAsync("Test.cs");

        // Private methods are filtered out — no summary should appear
        Assert.That(result, Does.Not.Contain("/// <summary>"),
            "Private methods should not receive XML doc stubs");
    }

    [Test]
    public async Task GenerateXmlDocStubs_FileNotFound_ThrowsException()
    {
        SetSource("public class C { }", "Test.cs");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.GenerateXmlDocumentationStubsAsync("Missing.cs"));
    }

    [Test]
    public async Task DocumentPocoFields_ClassWithProperties_AddsDescriptionAttributes()
    {
        SetSource(@"
public class UserDto
{
    public int UserId { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
}");
        var result = await _engine.DocumentPocoFieldsAsync("Test.cs", "UserDto");

        Assert.That(result, Does.Contain("[Description("), "Should add Description attributes");
        Assert.That(result, Does.Contain("UserId"), "Property names should be preserved");
        Assert.That(result, Does.Contain("System.ComponentModel"), "Should add using for ComponentModel");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. ArchitecturalEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ArchitecturalEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ArchitecturalEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ArchitecturalEngine(_workspaceManager);
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

    [Test]
    public async Task ConvertToBackgroundService_ValidClass_AddsBackgroundServiceBase()
    {
        SetSource(@"
public class DataSyncWorker
{
    public void DoWork() { }
}");
        var result = await _engine.ConvertToBackgroundServiceAsync("Test.cs", "DataSyncWorker");

        Assert.That(result, Does.Contain("BackgroundService"), "Should inherit from BackgroundService");
        Assert.That(result, Does.Contain("Microsoft.Extensions.Hosting"), "Should add Hosting using directive");
    }

    [Test]
    public async Task ConvertToBackgroundService_ValidClass_AddsExecuteAsyncOverride()
    {
        SetSource(@"
public class CacheWarmupWorker { public void Initialize() { } }");

        var result = await _engine.ConvertToBackgroundServiceAsync("Test.cs", "CacheWarmupWorker");

        Assert.That(result, Does.Contain("ExecuteAsync"), "Should inject ExecuteAsync override");
        Assert.That(result, Does.Contain("CancellationToken"), "ExecuteAsync must accept CancellationToken");
        Assert.That(result, Does.Contain("stoppingToken"), "Conventional parameter name for BackgroundService");
    }

    [Test]
    public async Task ConvertToBackgroundService_ClassNotFound_ThrowsException()
    {
        SetSource("public class Foo { }", "Test.cs");

        Assert.ThrowsAsync<Exception>(async () =>
            await _engine.ConvertToBackgroundServiceAsync("Test.cs", "NonExistentClass"));
    }

    [Test]
    public async Task FindCircularDependencies_EmptyProject_ReturnsEmpty()
    {
        SetSource("// empty file with no types", "Empty.cs");

        var cycles = await _engine.FindCircularDependenciesAsync();

        Assert.That(cycles, Is.Empty, "No types in project — no circular dependencies possible");
    }

    [Test]
    public async Task FindCircularDependencies_TwoMutuallyDependentClasses_FindsDirectCycle()
    {
        // A→B and B→A via field references — classic direct cycle
        SetMultipleFiles(
            ("NodeA.cs", @"
public class NodeA
{
    private NodeB _dependency;
}"),
            ("NodeB.cs", @"
public class NodeB
{
    private NodeA _dependency;
}")
        );

        var cycles = await _engine.FindCircularDependenciesAsync();

        Assert.That(cycles, Is.Not.Empty, "Mutually dependent classes should form a detected cycle");
        var cycle = cycles[0];
        Assert.That(cycle.CycleType, Is.EqualTo("Direct"), "Two-node cycle should be classified as Direct");
        Assert.That(cycle.Cycle.Count, Is.EqualTo(3), "Direct cycle path includes start+mid+start: [A,B,A]");
        // Both types should appear in the cycle
        Assert.That(cycle.Cycle.Any(n => n.Contains("NodeA")), Is.True);
        Assert.That(cycle.Cycle.Any(n => n.Contains("NodeB")), Is.True);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. ApiAutomationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ApiAutomationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ApiAutomationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ApiAutomationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task GenerateHttpClient_WithTypedActionResult_ProducesClientWithTypedReturn()
    {
        SetSource(@"
public class ProductsController
{
    public Task<ActionResult<List<Product>>> GetAll() => Task.FromResult(null!);
    public Task<ActionResult<Product>> GetById(int id) => Task.FromResult(null!);
}");
        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "ProductsController");

        Assert.That(result, Does.Contain("ProductsClient"), "Client class name should strip 'Controller'");
        Assert.That(result, Does.Contain("GetAll"), "Should generate method for GetAll");
        Assert.That(result, Does.Contain("GetById"), "Should generate method for GetById");
        Assert.That(result, Does.Contain("using System.Net.Http.Json;"), "Should include HttpClient using");
        // Task<ActionResult<T>> should be simplified to Task<T>
        Assert.That(result, Does.Contain("Task<List<Product>>").Or.Contain("Task<Product>"),
            "Should strip ActionResult wrapper from return types");
    }

    [Test]
    public async Task GenerateHttpClient_VoidAndActionResultReturns_GeneratesTaskMethods()
    {
        SetSource(@"
public class OrdersController
{
    public void Delete(int id) { }
    public ActionResult Create(string name) => null!;
    public IActionResult Update(int id) => null!;
}");
        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "OrdersController");

        // void, ActionResult, IActionResult → all become Task
        Assert.That(result, Does.Contain("async Task Delete"), "void return → Task");
        Assert.That(result, Does.Contain("async Task Create"), "ActionResult → Task");
        Assert.That(result, Does.Contain("async Task Update"), "IActionResult → Task");
    }

    [Test]
    public async Task GenerateHttpClient_FileNotFound_ReturnsEmpty()
    {
        SetSource("public class C { }", "Test.cs");

        var result = await _engine.GenerateHttpClientForControllerAsync("Missing.cs", "MyController");

        Assert.That(result, Is.Empty, "Should return empty string when file not found");
    }

    [Test]
    public async Task GenerateHttpClient_ControllerNotFound_ReturnsEmpty()
    {
        SetSource(@"public class SomeOtherClass { public void Method() { } }");

        var result = await _engine.GenerateHttpClientForControllerAsync("Test.cs", "MissingController");

        Assert.That(result, Is.Empty, "Should return empty string when controller class not found");
    }
}
