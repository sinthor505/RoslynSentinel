using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class MassiveIntelligenceTests
{
    private IWorkspaceManager _workspaceManager;
    private MetricsEngine _metricsEngine;
    private DiscoveryEngine _searchEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    [SetUp]
    public void Setup()
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _metricsEngine = Battery.Advanced.TestEnginesAdvanced.MetricsEngine(_workspaceManager);
        _searchEngine = Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager);
        _inventoryEngine = Battery.Basic.TestEngines.InventoryEngine(_workspaceManager);
        _deadCodeEngine = Battery.Advanced.TestEnginesAdvanced.DeadCodeEngine(_workspaceManager);
        _antiPatternEngine = Battery.Advanced.TestEnginesAdvanced.AntiPatternEngine(_workspaceManager, config);
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
    [Category("DiscoveryEngine")] // sentinel:auto-category
    [Category("SearchResult")] // sentinel:auto-category
    public async Task Search_ShouldFindMethodsByReturnType(int id)
    {
        SetSource($"public class C{id} {{ public int M{id}() => {id}; }}", $"C{id}.cs");
        var results = await Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindMethodsByReturnTypeAsync("int");
        Assert.That(results.Any(r => r.MemberName == $"M{id}"), Is.True);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Category("DiscoveryEngine")] // sentinel:auto-category
    [Category("SearchResult")] // sentinel:auto-category
    public async Task Search_ShouldFindTypesByAttribute(int id)
    {
        // FindTypesByAttributeAsync resolves the attribute via the compilation's semantic
        // model (compilation.GetTypeByMetadataName), so the attribute class must actually
        // be declared/resolvable in source -> not just referenced by name.
        SetSource($"public class MyAttrAttribute : System.Attribute {{ }} [MyAttr] public class C{id} {{ }}", $"C{id}.cs");
        var results = await Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("MyAttr");
        Assert.That(results.Any(r => r.MemberName == $"C{id}"), Is.True);
    }

    [Test]
    [TestCase("public class A {}", "A")]
    [TestCase("public interface IA {}", "IA")]
    [Category("InventoryEngine")] // sentinel:auto-category
    public async Task Inventory_ShouldGetCodeInventory(string src, string expectedType)
    {
        SetSource(src, "Test.cs");
        var report = await _inventoryEngine.GetCodeInventoryAsync("Test.cs");
        if (src.Contains("class"))
        {
            Assert.That(report.Classes, Contains.Item(expectedType));
        }
        else
        {
            Assert.That(report.Interfaces, Contains.Item(expectedType));
        }
    }

    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task DeadCode_ShouldFindUnusedPrivateFields()
    {
        SetSource("public class C { private int _unused; }", "C.cs");
        var dead = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(dead.Count, Is.EqualTo(1));
        Assert.That(dead[0].SymbolName, Is.EqualTo("_unused"));
    }

    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task DeadCode_ShouldIgnoreUsedPrivateFields()
    {
        SetSource("public class C { private int _used; public int Get() => _used; }", "C.cs");
        var dead = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("C.cs");
        Assert.That(dead.Count, Is.Zero);
    }

    [Test]
    [Category("MetricsEngine")] // sentinel:auto-category
    [Category("SolutionMetrics")] // sentinel:auto-category
    public async Task Metrics_ShouldComputeSolutionMetrics()
    {
        SetSource("public class C { public void M() { } }", "C.cs");
        var metrics = await _metricsEngine.GetSolutionMetricsAsync();
        Assert.That(metrics.TotalTypes, Is.EqualTo(1), "Fixture has one class.");
        Assert.That(metrics.TotalMethods, Is.EqualTo(1), "Fixture has one method.");
    }

    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task Analysis_ShouldDetectLongParameterLists()
    {
        SetSource("public class C { public void M(int a, int b, int c, int d, int e, int f) {} }", "C.cs");
        var issues = await _antiPatternEngine.DetectLongParameterListsAsync(threshold: 5);
        Assert.That(issues.Count, Is.EqualTo(1));
    }

    public AntiPatternEngine _antiPatternEngine;
}