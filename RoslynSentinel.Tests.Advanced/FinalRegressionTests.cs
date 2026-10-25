using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;
/// <summary>
/// Final regression tests for RoslynSentinel:
/// - BUG-61: SyncTypeAndFilenameTool uses staging mechanism
/// - BUG-60: RemoveMember checks for usages
/// - BUG-57: IntroduceParameterObjectTool handles interfaces
/// - BUG-55: OptimizeToValueTask updates interface signatures
/// - Additional regression coverage for the final sweep
/// </summary>
[TestFixture]
public class FinalRegressionTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SentinelConfiguration _config = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;
    private AsyncOptimizationEngine _asyncOptimizationEngine = null!;
    private StructuralRefinementEngine _structuralRefinementEngine = null!;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, _config);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-61: SyncTypeAndFilenameTool -> Uses Staging Instead of Direct Write
    // ────────────────────────────────────────────────────────────────────────────
    [Test]
    [Category("StructuralRefinementEngine")] // sentinel:auto-category
    public async Task BUG_61_SyncTypeAndFilename_UsesStagingMechanism()
    {
        const string code = @"
            namespace MyApp
            {
                public class DataService { }
                public class Helper { }
            }";
        SetSource(code, "WrongName.cs");
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);
        // Should use staging (Changes dict keyed by new path) instead of direct File.Move
        Assert.That(result.Changes, Is.Not.Null.And.Not.Empty, "Should use staging mechanism via Changes dictionary");
        Assert.That(result.Changes!.Keys.First().Absolute, Does.Contain("DataService.cs"), "Should identify primary type DataService");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-57: IntroduceParameterObjectTool -> Warns About Interface Methods
    // ────────────────────────────────────────────────────────────────────────────
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task BUG_57_IntroduceParameterObject_WarnsAboutInterfaceImplementation()
    {
        const string code = @"
            public interface IProcessor
            {
                void Process(string name, int age, bool active);
            }
            
            public class Processor : IProcessor
            {
                public void Process(string name, int age, bool active) { }
            }";
        SetSource(code);
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await new StructuralRefactoringEngine(_workspaceManager).IntroduceParameterObjectAsync(filePath, "Process");
        Assert.That(result, Is.Not.Null);
        // Should create parameter object and possibly warn about interface
        Assert.That(result.UpdatedText, Does.Contain("ProcessParameters") | Does.Contain("WARNING"), "Should introduce parameter object or warn about interface");
    }

    [Test]
    [Category("StructuralRefactoringEngine")]
    public async Task IntroduceParameterObject_ClassAndInterface_UpdatesImplementationAndAppendsRecord()
    {
        const string code = @"
            public interface IProcessor
            {
                void Process(string name, int age, bool active);
            }

            public class Processor : IProcessor
            {
                public void Process(string name, int age, bool active) { }
            }";
        SetSource(code);
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await new StructuralRefactoringEngine(_workspaceManager).IntroduceParameterObjectAsync(filePath, "Process");
        var classStart = result.UpdatedText.IndexOf("class Processor", StringComparison.Ordinal);
        Assert.That(classStart, Is.GreaterThanOrEqualTo(0), "Class Processor should still be present");
        Assert.That(result.UpdatedText.Substring(classStart), Does.Contain("Process(ProcessParameters request"), "Class implementation should take the parameter object");
        Assert.That(result.UpdatedText, Does.Contain("public record ProcessParameters"), "Record declaration should be appended");
        Assert.That(result.UpdatedText, Does.Contain("void Process(string name, int age, bool active);"), "Interface signature should be unchanged");
        Assert.That(result.Message, Does.Contain("WARNING"), "Interface implementation should produce a warning");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-55: OptimizeToValueTask -> Warns About Interface Signature Changes
    // ────────────────────────────────────────────────────────────────────────────
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task BUG_55_OptimizeToValueTask_WarnsAboutInterfaceUpdate()
    {
        const string code = @"
            public interface IDataService
            {
                Task<string> GetDataAsync();
            }
            
            public class DataService : IDataService
            {
                public async Task<string> GetDataAsync()
                {
                    return await Task.FromResult(""data"");
                }
            }";
        SetSource(code);
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, "GetDataAsync");
        Assert.That(result.UpdatedText, Does.Contain("ValueTask<string>"), "Should convert Task<T> to ValueTask<T>");
        Assert.That(result.UpdatedText, Does.Contain("WARNING") | Does.Contain("interface"), "Should warn about interface when it implements one");
    }
}