using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

public class QualityTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private LogicOptimizationEngine _logicEngine;
    private PerformanceEngine _perfEngine;
    private AnalysisEngine _analysisEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _logicEngine = new LogicOptimizationEngine(_workspaceManager);
        _perfEngine = new PerformanceEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    [Test]
    public async Task AddGuardClauses_Should_Inject_NullChecks()
    {
        var source = "public class S { public void M(string input) { var x = input.Length; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "S.cs"));
        var result = await _logicEngine.AddGuardClausesAsync("S.cs", "M");
        Assert.That(result, Contains.Substring("ArgumentNullException.ThrowIfNull(input);"));
    }

    [Test]
    public async Task DetectInefficientStringComparisons_Should_Flag_ToLower_Equals()
    {
        var source = "public class C { bool IsMatch(string s) => s.ToLowerInvariant() == \"test\"; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.DetectInefficientStringComparisonsAsync("C.cs");
        Assert.That(issues.Count, Is.GreaterThan(0));
        Assert.That(issues[0], Contains.Substring("Inefficient string comparison"));
    }

    [Test]
    public async Task CheckForEmptyCatchBlocks_Should_Flag_Empty_Blocks()
    {
        var source = "public class C { public void M() { try { } catch { } } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.CheckForEmptyCatchBlocksAsync("C.cs");
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0], Contains.Substring("Empty catch block"));
    }

    [Test]
    public async Task FindTaskVoidUsage_Should_Flag_Async_Void()
    {
        var source = "public class C { public async void M() { } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync("C.cs");
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0].Reason, Contains.Substring("Async void methods"));
    }

    [Test]
    public async Task FindPossibleDeadlocks_Should_Flag_Nested_Locks()
    {
        var source = "public class C { object a = new(); object b = new(); public void M() { lock(a) { lock(b) { } } } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.FindPossibleDeadlocksAsync(filePath: "C.cs");
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0], Contains.Substring("Nested lock statement"));
    }

    [Test]
    public async Task CheckForRedundantCast_Should_Flag_Same_Type_Casts()
    {
        var source = "public class C { public void M() { string s = \"test\"; var x = (string)s; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.CheckForRedundantCastAsync("C.cs");
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0], Does.Contain("Redundant cast in C.cs"));
    }
}
