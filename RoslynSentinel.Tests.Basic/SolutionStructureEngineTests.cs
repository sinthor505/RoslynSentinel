#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SolutionStructureEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private SolutionStructureEngine _engine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
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
        var result = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "DataSyncWorker");
        Assert.That(result.UpdatedText, Does.Contain("BackgroundService"), "Should inherit from BackgroundService");
        Assert.That(result.UpdatedText, Does.Contain("Microsoft.Extensions.Hosting"), "Should add Hosting using directive");
    }

    [Test]
    public async Task ConvertToBackgroundService_ValidClass_AddsExecuteAsyncOverride()
    {
        SetSource(@"
public class CacheWarmupWorker { public void Initialize() { } }");
        var result = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "CacheWarmupWorker");
        Assert.That(result.UpdatedText, Does.Contain("ExecuteAsync"), "Should inject ExecuteAsync override");
        Assert.That(result.UpdatedText, Does.Contain("CancellationToken"), "ExecuteAsync must accept CancellationToken");
        Assert.That(result.UpdatedText, Does.Contain("stoppingToken"), "Conventional parameter name for BackgroundService");
    }

    [Test]
    public async Task ConvertToBackgroundService_ExistingInterface_IsKeptAfterBackgroundService()
    {
        SetSource(@"
using System;

public class W : IDisposable
{
    public void Dispose() { }
}");
        var result = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "W");
        Assert.That(result.UpdatedText, Does.Match(@"class W\s*:\s*BackgroundService\s*,\s*IDisposable"), "BackgroundService first, existing interface kept");
        var syntaxErrors = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.UpdatedText)
            .GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.That(syntaxErrors, Is.Empty);
    }

    [Test]
    public async Task ConvertToBackgroundService_NonInterfaceBaseClass_IsRefusedNamingTheBase()
    {
        const string source = @"
using System;

public class Foo { }

public class W : Foo, IDisposable
{
    public void Dispose() { }
}";
        SetSource(source);
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "W"));
        Assert.That(ex!.Message, Does.Contain("Class 'W' already derives from 'Foo'"));
        Assert.That(ex.Message, Does.Contain("Convert it manually or remove the base class first"));
        // The refusal happens before any text is produced, so the committed document is unchanged.
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var text = await solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Test.cs").GetTextAsync();
        Assert.That(text.ToString(), Is.EqualTo(source));
    }

    [Test]
    public async Task ConvertToBackgroundService_UnresolvedBaseType_IsRefusedNamingTheBase()
    {
        SetSource(@"
public class W : Missing
{
}");
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "W"));
        Assert.That(ex!.Message, Does.Contain("Class 'W'"));
        Assert.That(ex.Message, Does.Contain("'Missing'"));
    }

    [Test]
    public async Task ConvertToBackgroundService_ClassNotFound_ThrowsException()
    {
        SetSource("public class Foo { }", "Test.cs");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).ConvertToBackgroundServiceAsync("Test.cs", "NonExistentClass"));
    }

    [Test]
    public async Task FindCircularDependencies_EmptyProject_ReturnsEmpty()
    {
        SetSource("// empty file with no types", "Empty.cs");
        var cycles = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).FindCircularDependenciesAsync();
        Assert.That(cycles, Is.Empty, "No types in project - no circular dependencies possible");
    }

    [Test]
    public async Task FindCircularDependencies_TwoMutuallyDependentClasses_FindsDirectCycle()
    {
        // A->B and B->A via field references -> classic direct cycle
        SetMultipleFiles(("NodeA.cs", @"
public class NodeA
{
    private NodeB _dependency;
}"), ("NodeB.cs", @"
public class NodeB
{
    private NodeA _dependency;
}"));
        var cycles = await Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration()).FindCircularDependenciesAsync();
        Assert.That(cycles, Is.Not.Empty, "Mutually dependent classes should form a detected cycle");
        var cycle = cycles[0];
        Assert.That(cycle.CycleType, Is.EqualTo("Direct"), "Two-node cycle should be classified as Direct");
        Assert.That(cycle.Cycle.Count, Is.EqualTo(3), "Direct cycle path includes start+mid+start: [A,B,A]");
        // Both types should appear in the cycle
        Assert.That(cycle.Cycle.Any(n => n.Contains("NodeA")), Is.True);
        Assert.That(cycle.Cycle.Any(n => n.Contains("NodeB")), Is.True);
    }
}// ════════════════════════════════════════════════════════════════════════════════
