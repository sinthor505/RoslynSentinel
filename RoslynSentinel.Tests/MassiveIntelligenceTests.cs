using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class MassiveIntelligenceTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AnalysisEngine _analysisEngine;
    private MetricsEngine _metricsEngine;
    private SemanticSearchEngine _searchEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _searchEngine = new SemanticSearchEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task Search_ShouldFindMethodsByReturnType(int id)
    {
        SetSource($"public class C{id} {{ public int M{id}() => {id}; }}", $"C{id}.cs");
        var results = await _searchEngine.FindMethodsByReturnTypeAsync("int");
        Assert.That(results.Any(r => r.MemberName == $"M{id}"), Is.True);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task Search_ShouldFindTypesByAttribute(int id)
    {
        SetSource($"[MyAttr] public class C{id} {{ }}", $"C{id}.cs");
        var results = await _searchEngine.FindTypesByAttributeAsync("MyAttr");
        Assert.That(results.Any(r => r.MemberName == $"C{id}"), Is.True);
    }

    [Test]
    [TestCase("public class A {}", "A")]
    [TestCase("public interface IA {}", "IA")]
    public async Task Inventory_ShouldGetCodeInventory(string src, string expectedType)
    {
        SetSource(src, "Test.cs");
        var report = await _inventoryEngine.GetCodeInventoryAsync("Test.cs");
        if (src.Contains("class")) Assert.That(report.Classes, Contains.Item(expectedType));
        else Assert.That(report.Interfaces, Contains.Item(expectedType));
    }

    [Test]
    public async Task DeadCode_ShouldFindUnusedPrivateFields()
    {
        SetSource("public class C { private int _unused; }", "C.cs");
        var dead = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(dead.Count, Is.EqualTo(1));
        Assert.That(dead[0].SymbolName, Is.EqualTo("_unused"));
    }

    [Test]
    public async Task DeadCode_ShouldIgnoreUsedPrivateFields()
    {
        SetSource("public class C { private int _used; public int Get() => _used; }", "C.cs");
        var dead = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(dead.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Metrics_ShouldComputeSolutionMetrics()
    {
        SetSource("public class C { public void M() { } }", "C.cs");
        var metrics = await _metricsEngine.GetSolutionMetricsAsync();
        Assert.That(metrics.TotalTypes, Is.GreaterThan(0));
        Assert.That(metrics.TotalMethods, Is.GreaterThan(0));
    }

    [Test]
    public async Task Analysis_ShouldDetectLongParameterLists()
    {
        SetSource("public class C { public void M(int a, int b, int c, int d, int e, int f) {} }", "C.cs");
        var issues = await _analysisEngine.DetectLongParameterListsAsync(threshold: 5);
        Assert.That(issues.Count, Is.EqualTo(1));
    }
}
