#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

/// <summary>
/// Battery #8 -> Tests for five engines at 5-6 mention coverage:
///   A. InventoryEngine              (2 tests) -> GetCodeInventory
///   B. ModernizationUpgradeEngine   (3 tests) -> UseSpan stub, UpgradePatternMatching, UseThrowExpressions stub
///   C. DependencyEngine             (3 tests) -> GetProjectDependencies, unknown project throws, FindUnusedRefs
///   D. ModernLoggingEngine          (3 tests) -> ConvertToSourceGeneratedLogging (real, no calls, unknown class)
///   E. IDEStyleEngine               (3 tests) -> SimplifyMemberAccess, UseObjectInitializers, UseNullPropagation stub
///
/// Total: 14 tests.
/// </summary>
[TestFixture]
[Category("InventoryEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class InventoryEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private InventoryEngine _engine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.InventoryEngine(_workspaceManager);
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
        Assert.That(report.filePath.Absolute, Is.EqualTo("Test.cs"));
        Assert.That(report.Namespaces, Contains.Item("ExpressRecipe.Services"));
        Assert.That(report.Classes, Contains.Item("ProductService"));
        Assert.That(report.Interfaces, Contains.Item("IProductService"));
        Assert.That(report.Methods, Contains.Item("Create"));
        Assert.That(report.Methods, Contains.Item("GetAsync"));
        Assert.That(report.Properties, Contains.Item("Name"));
        Assert.That(report.Properties, Contains.Item("Count"));
    }

    [Test]
    public async Task GetCodeInventory_UnknownFile_ThrowsFileNotFound()
    {
        SetSource("public class Foo { }");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.GetCodeInventoryAsync("NonExistent.cs"));
    }
}
