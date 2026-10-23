// Battery #23 -> QualityTools
// Tests all 46 public methods of QualityTools in-memory via TestSolutionBuilder.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
public class BatteryTwentyThreeTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private PerformanceEngine _performanceEngine;
    private SecurityEngine _securityEngine;
    private TestingEngine _testingEngine;
    private ControlFlowEngine _controlFlowEngine;
    private LogicSimplificationEngine _logicOptimizationEngine;
    private AsyncAnalysisEngine _asyncSafetyEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private AsyncBatchEngine _asyncBatchEngine;
    private DiagnosticEngine _diagnosticEngine;
    private AntiPatternEngine _antiPatternEngine;
    private ThreadSafetyEngine _threadSafetyEngine;
    private QualityTools _tools;
    private const string AsyncSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestProj;

public class AsyncWorker
{
    private static object _lock = new object();

    public async Task WorkAsync()
    {
        await Task.Delay(1000);
    }

    public void SyncMethod()
    {
        Thread.Sleep(100);
    }

    public void MethodWithLock()
    {
        lock(_lock) { Console.WriteLine(""locked""); }
    }

    public async void AsyncVoidBad()
    {
        await Task.Delay(1);
    }

    public Task FireAndForget()
    {
        return Task.CompletedTask;
    }

    public async Task CallerAsync()
    {
        FireAndForget();
        await Task.CompletedTask;
    }
}";
    private const string SecuritySource = @"
using System;
using System.Data.SqlClient;

namespace TestProj;

public class DataService
{
    public void RunQuery(string userId)
    {
        var cmd = new SqlCommand(""SELECT * FROM Users WHERE Id = "" + userId);
    }

    public string GetPath()
    {
        return ""C:\\Users\\admin\\file.txt"";
    }
}";
    private const string QualitySource = @"
using System;
using System.Collections.Generic;

namespace TestProj;

public class QualityClass
{
    public string Status;
    public int Count;
    public string Name { get; set; }

    public void Process()
    {
        if (Status == null) { }
        var x = (string)null;
        try { var r = 1/Count; } catch { }
    }

    public async System.Threading.Tasks.Task DoAsync()
    {
        await System.Threading.Tasks.Task.Delay(1);
    }
}";
    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicSimplificationEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncAnalysisEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager, _config);
        _threadSafetyEngine = new ThreadSafetyEngine(_workspaceManager);
        _asyncBatchEngine = new AsyncBatchEngine(_workspaceManager, new AsyncOptimizationEngine(_workspaceManager), new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance), new AntiPatternEngine(_workspaceManager), new MigrationLedger(), NullLogger<AsyncBatchEngine>.Instance);
        _tools = BatteryToolFactory.CreateQualityTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ===================== THROW-GUARD METHODS =====================
    // --- AddGuardClauses ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task AddGuardClauses_ValidMethod_ReturnsUpdatedSource()
    {
        SetSource("public class Guarded { public void Process(string s) { System.Console.WriteLine(s); } }", "Quality.cs");
        var result = await _logicOptimizationEngine.AddGuardClausesAsync("Quality.cs", "Process");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("ArgumentNullException"));
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task AddGuardClauses_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.AddGuardClausesAsync("NonExistent.cs", "Process");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.DocumentNotFound)); // TODO throw-migration: assert the typed exception once LogicSimplificationEngine throws
    }

    // --- AddBenchmarkStub ---
    [Test]
    [Category("TestingEngine")] // sentinel:auto-category
    public async Task AddBenchmarkStub_ValidClassAndMethod_ReturnsUpdatedSource()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.AddBenchmarkStubAsync("Quality.cs", "QualityClass", "Process");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("[Benchmark]"));
    }

    [Test]
    [Category("TestingEngine")] // sentinel:auto-category
    public async Task AddBenchmarkStub_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _testingEngine.AddBenchmarkStubAsync("NonExistent.cs", "C", "M");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.DocumentNotFound)); // TODO throw-migration: assert the typed exception once TestingEngine throws
    }

    // --- AddConfigureAwaitFalse ---
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task AddConfigureAwaitFalse_FileWithAwaits_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("Async.cs");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain(".ConfigureAwait(false)"));
    }

    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task AddConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("NonExistent.cs"));
    }

    // --- RemoveConfigureAwaitFalse ---
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task RemoveConfigureAwaitFalse_FileWithConfigureAwait_ReturnsUpdatedSource()
    {
        const string src = "using System.Threading.Tasks; namespace TestProj; public class W { public async Task DoAsync() { await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false); } }";
        SetSource(src, "W.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("W.cs");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Not.Contain(".ConfigureAwait(false)"));
    }

    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task RemoveConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("NonExistent.cs"));
    }

    // --- ConvertLockToSemaphoreSlim ---
    [Test]
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public async Task ConvertLockToSemaphoreSlim_MethodWithLock_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("Async.cs", "MethodWithLock");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("SemaphoreSlim"));
    }

    [Test]
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public async Task ConvertLockToSemaphoreSlim_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("NonExistent.cs", "MethodWithLock");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Error)); // TODO throw-migration: assert the typed exception once ThreadSafetyEngine throws
    }

    [Test]
    [Category("RefactoringToolHelpers")] // sentinel:auto-category
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public async Task ConvertLockToSemaphoreSlim_NonExistentFile_ReportsNotFoundCode()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("NonExistent.cs", "MethodWithLock");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Error), "non-existent file should return Error outcome");
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error code should be NotFound");
        Assert.That(RefactoringToolHelpers.ErrorCodeFor(result), Is.EqualTo(ToolErrorCode.NotFound), "ErrorCodeFor helper should also return NotFound");
    }

    // --- ConvertToAsyncEnumerable---
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task ConvertToAsyncEnumerable_ValidMethod_ReturnsUpdatedSource()
    {
        const string src = @"using System.Collections.Generic; namespace TestProj;
public class Streamer { public IEnumerable<int> GetData() { yield return 1; yield return 2; } }";
        SetSource(src, "Streamer.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("Streamer.cs", "GetData");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("IAsyncEnumerable"));
    }

    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task ConvertToAsyncEnumerable_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("NonExistent.cs", "GetData");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.DocumentNotFound)); // TODO throw-migration: assert the typed exception once AsyncOptimizationEngine throws
    }

    // --- AddCancellationTokenToMethod---
    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task AddCancellationTokenToMethod_AsyncMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("Async.cs", "WorkAsync");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("CancellationToken"));
    }

    [Test]
    [Category("AsyncOptimizationEngine")] // sentinel:auto-category
    public async Task AddCancellationTokenToMethod_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("NonExistent.cs", "WorkAsync");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.DocumentNotFound)); // TODO throw-migration: assert the typed exception once AsyncOptimizationEngine throws
    }

    // --- MakeMethodThreadSafe---
    [Test]
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public async Task MakeMethodThreadSafe_ValidMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("Async.cs", "SyncMethod");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("lock"));
    }

    [Test]
    [Category("ThreadSafetyEngine")] // sentinel:auto-category
    public async Task MakeMethodThreadSafe_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("NonExistent.cs", "SyncMethod");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound)); // TODO throw-migration: assert the typed exception once ThreadSafetyEngine throws
    }

    // ===================== NON-THROW METHODS =====================
    // --- AnalyzePerformance ---
    [Test]
    [Category("PerformanceEngine")] // sentinel:auto-category
    public async Task AnalyzePerformance_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _performanceEngine.AnalyzePerformanceAsync("Quality.cs");
        Assert.That(result, Is.Empty);
        SetSource("public class P { public string Build(string[] xs) { var s = \"\"; foreach (var x in xs) { s += x; } return s; } }", "Perf.cs");
        var positive = await _performanceEngine.AnalyzePerformanceAsync("Perf.cs");
        Assert.That(positive, Has.Count.EqualTo(1));
        Assert.That(positive, Has.Some.Property("IssueType").EqualTo("StringConcatenationInLoop"));
        Assert.That(positive, Has.None.Property("IssueType").EqualTo("PotentialDoubleEnumeration"));
    }

    // --- AnalyzeSecurity ---
    [Test]
    [Category("SecurityEngine")] // sentinel:auto-category
    public async Task AnalyzeSecurity_FileWithWeakHash_ReturnsList()
    {
        SetSource("using System.Security.Cryptography; public class H { public byte[] Hash(byte[] d) { return MD5.Create().ComputeHash(d); } }", "Security.cs");
        var result = await _securityEngine.AnalyzeSecurityAsync("Security.cs");
        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result[0].IssueType, Is.EqualTo("WeakHashAlgorithm")); Assert.That(result[0].Line, Is.EqualTo(1));
        SetSource("public class C { public int Add(int a, int b) { return a + b; } }", "Clean.cs");
        var clean = await _securityEngine.AnalyzeSecurityAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- GenerateTestSkeleton ---
    [Test]
    [Category("TestingEngine")] // sentinel:auto-category
    public async Task GenerateTestSkeleton_ValidClass_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.GenerateTestSkeletonAsync("Quality.cs", "QualityClass");
        Assert.That(result.Content, Does.Contain("class QualityClassTests"));
    }

    // --- GenerateTestScaffold ---
    [Test]
    [Category("TestingEngine")] // sentinel:auto-category
    public async Task GenerateTestScaffold_ValidClass_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.GenerateTestScaffoldAsync("Quality.cs", "QualityClass");
        Assert.That(result.ClassName, Is.EqualTo("QualityClass")); Assert.That(result.TestClassName, Is.EqualTo("QualityClassTests")); Assert.That(result.Namespace, Is.EqualTo("TestProj.Tests")); Assert.That(result.Code, Does.Contain("class QualityClassTests")); // DEFERRED (accepted 2026-10-22, finding #20): GenerateTestScaffold always emits xUnit/Moq usings, even for an NUnit test project.
        // Left as-is on purpose until scheduled; do not re-raise in reviews.
    }

    // --- AnalyzePathCoverage ---
    [Test]
    [Category("ControlFlowEngine")] // sentinel:auto-category
    public async Task AnalyzePathCoverage_ValidMethod_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _controlFlowEngine.AnalyzePathCoverageAsync("Quality.cs", "Process");
        Assert.That(result.MethodName, Is.EqualTo("Process")); Assert.That(result.BranchesToTest, Is.Not.Empty);
    }

    // --- FindPossibleDeadlocks ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindPossibleDeadlocks_FileWithLock_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _antiPatternEngine.FindPossibleDeadlocksAsync(filePath: "Async.cs");
        Assert.That(result, Is.Empty);
        SetSource("public class D { private object a = new object(); private object b = new object(); public void M() { lock (a) { lock (b) { } } } }", "Dead.cs");
        var positive = await _antiPatternEngine.FindPossibleDeadlocksAsync(filePath: "Dead.cs");
        Assert.That(positive, Has.Count.EqualTo(1)); Assert.That(positive[0], Does.Contain("Nested lock statement found at line 1"));
    }

    // --- AnalyzeSemaphoreUsage ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task AnalyzeSemaphoreUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _antiPatternEngine.AnalyzeSemaphoreUsageAsync("Async.cs");
        Assert.That(result, Is.Empty);
        SetSource("using System.Threading; using System.Threading.Tasks; public class S { private readonly SemaphoreSlim _sem = new SemaphoreSlim(1); public async Task RunAsync() { await _sem.WaitAsync(); } }", "Sem.cs");
        var positive = await _antiPatternEngine.AnalyzeSemaphoreUsageAsync("Sem.cs");
        Assert.That(positive, Has.Count.EqualTo(1)); Assert.That(positive[0], Does.Contain("Semaphore leak in 'RunAsync'"));
    }

    // --- DetectMemoryLeaks ---
    [Test]
    [Category("ResourceSafetyEngine")] // sentinel:auto-category
    public async Task DetectMemoryLeaks_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await new ResourceSafetyEngine(_workspaceManager, _config).DetectMemoryLeaksAsync("Quality.cs");
        Assert.That(result, Is.Empty);
        SetSource("public class Publisher { public event System.EventHandler Changed; } public class Sub { public void Hook(Publisher p) { p.Changed += this.OnChanged; } private void OnChanged(object s, System.EventArgs e) { } }", "Mem.cs");
        var positive = await new ResourceSafetyEngine(_workspaceManager, _config).DetectMemoryLeaksAsync("Mem.cs");
        Assert.That(positive, Has.Count.EqualTo(1)); Assert.That(positive[0], Does.StartWith("Mem.cs:1 ")); Assert.That(positive[0], Does.Contain("Class 'Sub' subscribes to 'p.Changed' but class does not implement IDisposable"));
    }

    // --- FindTaskVoidUsage ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskVoidUsage_FileWithAsyncVoid_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result[0].MethodName, Is.EqualTo("AsyncVoidBad")); Assert.That(result[0].Reason, Does.Contain("Async void"));
    }

    // --- FindTaskYieldUsage ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskYieldUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskYieldUsageAsync("Async.cs");
        Assert.That(result, Is.Empty);
        SetSource("using System.Threading.Tasks; public class Y { public async Task RunAsync() { await Task.Yield(); } }", "Yield.cs");
        var positive = await _asyncSafetyEngine.FindTaskYieldUsageAsync("Yield.cs");
        Assert.That(positive, Has.Count.EqualTo(1)); Assert.That(positive[0].MethodName, Is.EqualTo("RunAsync")); Assert.That(positive[0].Reason, Does.Contain("Task.Yield()"));
    }

    // --- DetectReflectionUsage ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task DetectReflectionUsage_ValidFile_ReturnsList()
    {
        SetSource("public class Plain { public int Add(int a, int b) { return a + b; } }", "Plain.cs");
        var result = await _antiPatternEngine.DetectReflectionUsageAsync(filePath: "Plain.cs");
        Assert.That(result, Is.Empty);
        SetSource("public class R { public object Run(System.Type t) { return t.GetMethod(\"X\"); } }", "Refl.cs");
        result = await _antiPatternEngine.DetectReflectionUsageAsync(filePath: "Refl.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Does.StartWith("[REFLECTION] Potential reflection usage in Refl.cs at line 1 (.GetMethod)"));
    }

    // --- CheckForEmptyCatchBlocks ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task CheckForEmptyCatchBlocks_FileWithEmptyCatch_ReturnsList()
    {
        SetSource("public class E { public void Run() { try { System.Console.WriteLine(); } catch (System.Exception ex) { System.Console.WriteLine(ex); } } }", "Clean.cs");
        var clean = await _antiPatternEngine.CheckForEmptyCatchBlocksAsync(filePath: "Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("public class E { public void Run() { try { System.Console.WriteLine(); } catch (System.Exception) { } } }", "Quality.cs");
        var result = await _antiPatternEngine.CheckForEmptyCatchBlocksAsync(filePath: "Quality.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Does.StartWith("Empty catch block in Quality.cs at line 1."));
    }

    // --- FindTaskDelayUsage ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskDelayUsage_FileWithTaskDelay_ReturnsList()
    {
        SetSource("public class C { public int Add(int a, int b) { return a + b; } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindTaskDelayUsageAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("using System.Threading.Tasks; public class D { public async Task RunAsync() { await Task.Delay(10); } }", "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskDelayUsageAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: 'Task.Delay(...)' found"));
    }

    // --- CheckForRedundantCast ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task CheckForRedundantCast_FileWithRedundantCast_ReturnsList()
    {
        SetSource("public class R { public long Widen(int x) { return (long)x; } }", "Clean.cs");
        var clean = await _antiPatternEngine.CheckForRedundantCastAsync(filePath: "Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("public class R { public int Id(int x) { return (int)x; } }", "Quality.cs");
        var result = await _antiPatternEngine.CheckForRedundantCastAsync(filePath: "Quality.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Does.StartWith("Redundant cast in Quality.cs at line 1. Expression is already of type Int32."));
    }

    // --- FindTaskDelayZeroUsage ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskDelayZeroUsage_ValidFile_ReturnsList()
    {
        SetSource("using System.Threading.Tasks; public class Z { public async Task RunAsync() { await Task.Delay(10); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("using System.Threading.Tasks; public class Z { public async Task RunAsync() { await Task.Delay(0); } }", "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: 'Task.Delay(0)' found"));
    }

    // --- FindTaskWhenAllUsage ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskWhenAllUsage_ValidFile_ReturnsList()
    {
        SetSource("public class C { public int Add(int a, int b) { return a + b; } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("using System.Threading.Tasks; public class W { public async Task RunAsync() { await Task.Delay(1); await Task.Delay(2); } }", "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: Method has 2 sequential awaits with independent pairs."));
    }

    // --- DetectAntiPatterns ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task DetectAntiPatterns_ValidFile_ReturnsList()
    {
        SetSource("public class C { public async void Go() { } }", "Async.cs");
        var result = await _antiPatternEngine.DetectAntiPatternsAsync("Async.cs");
        Assert.That(result, Has.Some.Property("Pattern").EqualTo("AsyncVoidMethod"));
        Assert.That(result, Has.Some.Property("Description").Contains("'async void' method 'Go'"));
        SetSource("using System.Threading.Tasks; public class C { public async Task RunAsync() { await Task.CompletedTask; } }", "Clean.cs");
        var clean = await _antiPatternEngine.DetectAntiPatternsAsync("Clean.cs");
        Assert.That(clean, Has.None.Property("Pattern").EqualTo("AsyncVoidMethod"));
    }

    // --- FindPossibleInfiniteLoops ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindPossibleInfiniteLoops_ValidFile_ReturnsList()
    {
        SetSource("public class L { public void Spin() { while (true) { break; } } }", "Clean.cs");
        var clean = await _antiPatternEngine.FindPossibleInfiniteLoopsAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("public class L { public void Spin() { while (true) { } } }", "Async.cs");
        var result = await _antiPatternEngine.FindPossibleInfiniteLoopsAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Does.Contain("Potential infinite loop (while(true)) in 'Spin'"));
    }

    // --- DetectMismatchedAwait ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task DetectMismatchedAwait_FileWithMixedAsync_ReturnsList()
    {
        SetSource("using System.Threading.Tasks; public class M { public async Task RunAsync() { await Work(); } public Task Work() { return Task.CompletedTask; } }", "Clean.cs");
        var clean = await _antiPatternEngine.DetectMismatchedAwaitAsync(filePath: "Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("using System.Threading.Tasks; public class M { public async Task RunAsync() { Work(); } public Task Work() { return Task.CompletedTask; } }", "Async.cs");
        var result = await _antiPatternEngine.DetectMismatchedAwaitAsync(filePath: "Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("Potential mismatched await in Async.cs at line 1. Call to 'Work' is not awaited."));
    }

    // --- FindHardcodedPaths ---
    [Test]
    [Category("SecurityEngine")] // sentinel:auto-category
    public async Task FindHardcodedPaths_FileWithHardcodedPath_ReturnsList()
    {
        SetSource("public class H { public string P() { return \"plain\"; } }", "Clean.cs");
        var clean = await _securityEngine.FindHardcodedPathsAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
        SetSource("public class H { public string P() { return \"a/b\"; } }", "Security.cs");
        var result = await _securityEngine.FindHardcodedPathsAsync("Security.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].IssueType, Is.EqualTo("HardcodedPath"));
        Assert.That(result[0].Line, Is.EqualTo(1));
    }

    // --- FindMutablePublicProperties ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindMutablePublicProperties_ValidFile_ReturnsList()
    {
        SetSource("public class Mut { public string Name { get; set; } }", "Mut.cs");
        var result = await _antiPatternEngine.FindMutablePublicPropertiesAsync("Mut.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Pattern, Is.EqualTo("MutablePublicApi"));
        Assert.That(result[0].Description, Does.Contain("'Name'"));
        SetSource("public class Mut { public string Name { get; } }", "Clean.cs");
        var clean = await _antiPatternEngine.FindMutablePublicPropertiesAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindNamingViolations ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindNamingViolations_ValidFile_ReturnsList()
    {
        SetSource("public class lowerClass { public int count; }", "Naming.cs");
        var result = await _antiPatternEngine.FindNamingViolationsAsync("Naming.cs");
        Assert.That(result, Has.Some.Property("Description").Contains("Type 'lowerClass' starts with a lowercase letter"));
        Assert.That(result, Has.Some.Property("Description").Contains("Non-private field 'count' starts with a lowercase letter"));
        SetSource("public class GoodClass { private int _value; public int Value { get; } }", "Clean.cs");
        var clean = await _antiPatternEngine.FindNamingViolationsAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindStringMagicValues ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindStringMagicValues_ValidFile_ReturnsList()
    {
        SetSource("public class Magic { public string A() { return \"magic-value\"; } public string B() { return \"magic-value\"; } public string C() { return \"magic-value\"; } }", "Magic.cs");
        var result = await _antiPatternEngine.FindStringMagicValuesAsync("Magic.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Value, Is.EqualTo("magic-value"));
        Assert.That(result[0].OccurrenceCount, Is.EqualTo(3));
        SetSource("public class Magic { public string A() { return \"magic-value\"; } public string B() { return \"magic-value\"; } }", "Clean.cs");
        var clean = await _antiPatternEngine.FindStringMagicValuesAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindMissingCancellationTokens ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindMissingCancellationTokens_ValidFile_ReturnsList()
    {
        SetSource("using System.Threading; using System.Threading.Tasks; public class W { public async Task DoWorkAsync() { await Helper(CancellationToken.None); } public Task Helper(CancellationToken ct) { return Task.CompletedTask; } }", "Async.cs");
        var result = await _antiPatternEngine.FindMissingCancellationTokensAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("DoWorkAsync"));
        SetSource("using System.Threading; using System.Threading.Tasks; public class W { public async Task DoWorkAsync(CancellationToken ct) { await Helper(ct); } public Task Helper(CancellationToken ct) { return Task.CompletedTask; } }", "Clean.cs");
        var clean = await _antiPatternEngine.FindMissingCancellationTokensAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- AnalyzeExceptionHandling ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task AnalyzeExceptionHandling_FileWithCatch_ReturnsList()
    {
        SetSource("public class E { public void Run() { try { Do(); } catch { } } public void Do() { } }", "Exc.cs");
        var result = await _antiPatternEngine.AnalyzeExceptionHandlingAsync("Exc.cs");
        Assert.That(result, Has.Some.Property("Description").Contains("Catching System.Exception"));
        Assert.That(result, Has.Some.Property("Description").Contains("Empty catch block silently swallows"));
        SetSource("public class E { public void Run() { try { Do(); } catch (System.IO.IOException) { throw; } } public void Do() { } }", "Clean.cs");
        var clean = await _antiPatternEngine.AnalyzeExceptionHandlingAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- CheckForSqlInjection ---
    [Test]
    [Category("SecurityEngine")] // sentinel:auto-category
    public async Task CheckForSqlInjection_FileWithSqlInjection_ReturnsList()
    {
        SetSource("public class Db { public void Del(object cmd, string id) { cmd.ExecuteNonQuery($\"DELETE FROM t WHERE id = {id}\"); } }", "Security.cs");
        var result = await _securityEngine.CheckForSqlInjectionAsync("Security.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].IssueType, Is.EqualTo("PossibleSqlInjection"));
        Assert.That(result[0].Line, Is.EqualTo(1));
        Assert.That(result[0].Description, Does.Contain("'ExecuteNonQuery'"));
        SetSource("public class Clean { public void Run(object cmd) { cmd.ExecuteNonQuery(\"DELETE FROM t\"); } }", "Clean.cs");
        var clean = await _securityEngine.CheckForSqlInjectionAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- AnalyzeMethodControlFlow ---
    [Test]
    [Category("ControlFlowEngine")] // sentinel:auto-category
    public async Task AnalyzeMethodControlFlow_ValidMethod_ReturnsResult()
    {
        SetSource("public class F { public int Check(int x) { if (x > 0) { return 1; } return 0; } }", "Flow.cs");
        var result = await _controlFlowEngine.AnalyzeMethodControlFlowAsync("Flow.cs", "Check");
        Assert.That(result.ReturnStatements, Is.EqualTo(new[] { "return 1;", "return 0;" }));
        Assert.That(result.EndPointIsReachable, Is.False);
        Assert.That(result.Error, Is.Null);
        SetSource("public class F { public int Check(int x) { return x; } }", "Clean.cs");
        var missing = await _controlFlowEngine.AnalyzeMethodControlFlowAsync("Clean.cs", "Missing");
        Assert.That(missing.Error, Does.Contain("Method 'Missing' not found"));
    }

    // --- AnalyzeMethodDataFlow ---
    [Test]
    [Category("ControlFlowEngine")] // sentinel:auto-category
    public async Task AnalyzeMethodDataFlow_ValidMethod_ReturnsResult()
    {
        SetSource("public class D { public int Sum(int a) { int b = a + 1; return b; } }", "Calc.cs");
        var result = await _controlFlowEngine.AnalyzeMethodDataFlowAsync("Calc.cs", "Sum");
        Assert.That(result.VariablesDeclared, Is.EqualTo(new[] { "b" }));
        Assert.That(result.ReadInside, Does.Contain("a"));
        Assert.That(result.Error, Is.Null);
        SetSource("public class D { public int Sum(int a) { return a; } }", "Clean.cs");
        var missing = await _controlFlowEngine.AnalyzeMethodDataFlowAsync("Clean.cs", "Missing");
        Assert.That(missing.Error, Does.Contain("Method 'Missing' not found"));
    }

    // --- FindConfigureAwaitMissing ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindConfigureAwaitMissing_FileWithAwaits_ReturnsList()
    {
        SetSource("using System.Threading.Tasks; public class Svc { public async Task RunAsync() { await Task.CompletedTask; } }", "Async.cs");
        var result = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: Await missing .ConfigureAwait(false)"));
        SetSource("using System.Threading.Tasks; public class Svc { public async Task RunAsync() { await Task.CompletedTask.ConfigureAwait(false); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindBlockingCallsInAsync ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindBlockingCallsInAsync_FileWithThreadSleep_ReturnsList()
    {
        SetSource("using System.Threading; using System.Threading.Tasks; public class B { public async Task RunAsync() { Thread.Sleep(1); await Task.CompletedTask; } }", "Async.cs");
        var result = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("Async.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: Thread.Sleep() in async method"));
        SetSource("using System.Threading; public class B { public void Run() { Thread.Sleep(1); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindAsyncInConstructor ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindAsyncInConstructor_ValidFile_ReturnsList()
    {
        SetSource("public class K { public K() { LoadAsync(); } public System.Threading.Tasks.Task LoadAsync() { return System.Threading.Tasks.Task.CompletedTask; } }", "Ctor.cs");
        var result = await _asyncSafetyEngine.FindAsyncInConstructorAsync("Ctor.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("K"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: Constructor invokes async code"));
        SetSource("public class K { private int _x; public K() { _x = 1; } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindAsyncInConstructorAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindTaskRunInAsync ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindTaskRunInAsync_ValidFile_ReturnsList()
    {
        SetSource("using System.Threading.Tasks; public class R { public async Task RunAsync() { await Task.Run(() => { }); } }", "Run.cs");
        var result = await _asyncSafetyEngine.FindTaskRunInAsyncAsync("Run.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: await Task.Run(...) in server code"));
        SetSource("using System.Threading.Tasks; public class R { public async Task RunAsync() { await Task.Delay(1); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindTaskRunInAsyncAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindConcurrentCollectionOpportunities ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindConcurrentCollectionOpportunities_ValidFile_ReturnsList()
    {
        SetSource("using System.Collections.Generic; public class Cache { private readonly object _gate = new object(); private readonly Dictionary<string, int> _map = new Dictionary<string, int>(); public void Add(string k) { lock (_gate) { _map[k] = 1; } } }", "Cache.cs");
        var result = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync("Cache.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("Add"));
        Assert.That(result[0].Reason, Does.Contain("ConcurrentDictionary"));
        SetSource("using System.Collections.Generic; public class Cache { private readonly Dictionary<string, int> _map = new Dictionary<string, int>(); public void Add(string k) { _map[k] = 1; } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindUnsafeLazyInit ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindUnsafeLazyInit_ValidFile_ReturnsList()
    {
        SetSource("public class Holder { private object _instance; public object Get() { if (_instance == null) { _instance = new object(); } return _instance; } }", "Lazy.cs");
        var result = await _asyncSafetyEngine.FindUnsafeLazyInitAsync("Lazy.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Does.EndWith(":1"));
        Assert.That(result[0].Reason, Does.Contain("Unsafe lazy init: '_instance'"));
        SetSource("public class Holder { private volatile object _instance; public object Get() { if (_instance == null) { _instance = new object(); } return _instance; } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindUnsafeLazyInitAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- DetectValueTaskMisuse ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task DetectValueTaskMisuse_ValidFile_ReturnsList()
    {
        SetSource("using System.Threading.Tasks; public class V { public async Task RunAsync() { ValueTask<int> vt = Get(); await vt; await vt; } public ValueTask<int> Get() { return new ValueTask<int>(1); } }", "Vt.cs");
        var result = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("Vt.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("RunAsync"));
        Assert.That(result[0].Reason, Does.Contain("ValueTask variable 'vt' is awaited more than once"));
        SetSource("using System.Threading.Tasks; public class V { public async Task RunAsync() { var vt = Get(); await vt; } public ValueTask<int> Get() { return new ValueTask<int>(1); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindAsyncOverSync ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindAsyncOverSync_FileWithSyncMethods_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        SetSource("using System.Threading.Tasks; public class O { public async Task<int> GetAsync() { return 1; } }", "Over.cs");
        var result = await _asyncSafetyEngine.FindAsyncOverSyncAsync("Over.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("GetAsync"));
        Assert.That(result[0].Reason, Does.StartWith("Line 1: async method contains no await"));
        SetSource("using System.Threading.Tasks; public class O { public async Task RunAsync() { await Task.Delay(1); } }", "Clean.cs");
        var clean = await _asyncSafetyEngine.FindAsyncOverSyncAsync("Clean.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindUnawaitedFireAndForget ---
    [Test]
    [Category("AsyncAnalysisEngine")] // sentinel:auto-category
    public async Task FindUnawaitedFireAndForget_FileWithFireAndForget_ReturnsList()
    {
        SetSource("using System.Threading.Tasks;\nnamespace TestProj;\npublic class F { private async Task DoWorkAsync() { await Task.CompletedTask; } public void Go() { DoWorkAsync(); } }", "Unawaited.cs");
        var result = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("Unawaited.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("MethodName").EqualTo("Go"));
        Assert.That(result, Has.Some.Property("Reason").Contains("Line 3: Task-returning method 'DoWorkAsync' called without await - exceptions will be swallowed silently."));
        SetSource("using System.Threading.Tasks;\nnamespace TestProj;\npublic class F { private async Task DoWorkAsync() { await Task.CompletedTask; } public async Task GoAsync() { await DoWorkAsync(); } }", "Unawaited.cs");
        var clean = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("Unawaited.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindLongParameterList ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindLongParameterList_ValidFile_ReturnsList()
    {
        SetSource("public class P { public void Run(int a, int b, int c, int d) { } }", "LongParams.cs");
        var result = await _antiPatternEngine.FindLongParameterListAsync("LongParams.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("Pattern").EqualTo("LongParameterList"));
        Assert.That(result, Has.Some.Property("Description").EqualTo("'Run' has 4 parameters (a, b, c, d). Consider introducing a Parameter Object."));
        Assert.That(result, Has.Some.Property("Severity").EqualTo("Medium"));
        SetSource("public class P { public void Run(int a, int b, int c) { } }", "LongParams.cs");
        var clean = await _antiPatternEngine.FindLongParameterListAsync("LongParams.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- OptimizeResourceDisposal ---
    [Test]
    [Category("PerformanceEngine")] // sentinel:auto-category
    public async Task OptimizeResourceDisposal_ValidFile_ReturnsList()
    {
        SetSource("public class R { public void Go() { var s = new System.IO.MemoryStream(); } }", "Disposal.cs");
        var result = await _performanceEngine.OptimizeResourceDisposalAsync("Disposal.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("IssueType").EqualTo("MissingDisposal"));
        Assert.That(result, Has.Some.Property("Description").EqualTo("Type 'MemoryStream' implements IDisposable but is not wrapped in a 'using' statement."));
        SetSource("public class R { public void Go() { using (var s = new System.IO.MemoryStream()) { } } }", "Disposal.cs");
        var clean = await _performanceEngine.OptimizeResourceDisposalAsync("Disposal.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- DetectInefficientStringComparisons ---
    [Test]
    [Category("PerformanceEngine")] // sentinel:auto-category
    public async Task DetectInefficientStringComparisons_ValidFile_ReturnsList()
    {
        SetSource("public class S { public bool Eq(string a, string b) { return a.ToLower() == b; } }", "StrCmp.cs");
        var result = await _performanceEngine.DetectInefficientStringComparisonsAsync("StrCmp.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("IssueType").EqualTo("InefficientStringComparison"));
        Assert.That(result, Has.Some.Property("Description").EqualTo("Inefficient string comparison: avoid using .ToLower()/.ToUpper()/.ToLowerInvariant()/.ToUpperInvariant() for comparison. Use string.Equals with StringComparison.OrdinalIgnoreCase instead."));
        SetSource("public class S { public bool Eq(string a, string b) { return a == b; } }", "StrCmp.cs");
        var clean = await _performanceEngine.DetectInefficientStringComparisonsAsync("StrCmp.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindBoxingAllocations ---
    [Test]
    [Category("PerformanceEngine")] // sentinel:auto-category
    public async Task FindBoxingAllocations_ValidFile_ReturnsList()
    {
        SetSource("public class B { public void Take(object o) { } public void Go() { Take(1); } }", "Boxing.cs");
        var result = await _performanceEngine.FindBoxingAllocationsAsync("Boxing.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("IssueType").EqualTo("BoxingAllocation"));
        Assert.That(result, Has.Some.Property("Description").EqualTo("Boxing detected: converting value type 'Int32' to 'object' in parameter 'o'."));
        SetSource("public class B { public void Take(int o) { } public void Go() { Take(1); } }", "Boxing.cs");
        var clean = await _performanceEngine.FindBoxingAllocationsAsync("Boxing.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindPrimitiveObsession ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindPrimitiveObsession_ValidFile_ReturnsList()
    {
        SetSource("public class PO { public void Make(string a, string b, string c) { } }", "Primitive.cs");
        var result = await _antiPatternEngine.FindPrimitiveObsessionAsync("Primitive.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Has.Some.Property("Pattern").EqualTo("PrimitiveObsession"));
        Assert.That(result, Has.Some.Property("Description").EqualTo("'Make' has 3 parameters of type 'string': a, b, c. Consider a dedicated type."));
        Assert.That(result, Has.Some.Property("Severity").EqualTo("Medium"));
        SetSource("public class PO { public void Make(string a, string b, int c) { } }", "Primitive.cs");
        var clean = await _antiPatternEngine.FindPrimitiveObsessionAsync("Primitive.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- FindInconsistentAsyncSuffix ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindInconsistentAsyncSuffix_ValidFile_ReturnsList()
    {
        SetSource("public class IA { public async Task Fetch() { await Task.CompletedTask; } public int GetCountAsync() { return 1; } }", "AsyncSuffix.cs");
        var result = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("AsyncSuffix.cs");
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Has.Some.Property("Description").EqualTo("Method 'Fetch' is async/Task-returning but does not end with 'Async'. Rename to 'FetchAsync'."));
        Assert.That(result, Has.Some.Property("Description").EqualTo("Method 'GetCountAsync' ends with 'Async' but is not async and does not return Task/ValueTask. Remove 'Async' suffix."));
        SetSource("public class IA { public async Task FetchAsync() { await Task.CompletedTask; } public int Count() { return 1; } }", "AsyncSuffix.cs");
        var clean = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("AsyncSuffix.cs");
        Assert.That(clean, Is.Empty);
    }

    // --- GetDiagnosticsSummary ---
    [Test]
    [Category("DiagnosticEngine")] // sentinel:auto-category
    public async Task GetDiagnosticsSummary_ValidFile_ReturnsSummaryResult()
    {
        SetSource("public class E { public int M() { int x = \"s\"; return x; } }", "Diag.cs");
        var result = await _diagnosticEngine.GetFileDiagnosticsAsync("Diag.cs");
        Assert.That(result.Data.Errors, Is.EqualTo(1));
        Assert.That(result.Data.Warnings, Is.EqualTo(0));
        Assert.That(result.Data.Details, Has.Some.Property("Id").EqualTo("CS0029"));
        Assert.That(result.Data.Details, Has.Some.Property("Severity").EqualTo("Error"));
        SetSource("public class E { public int M() { return 1; } }", "Diag.cs");
        var clean = await _diagnosticEngine.GetFileDiagnosticsAsync("Diag.cs");
        Assert.That(clean.Data.Errors, Is.EqualTo(0));
        Assert.That(clean.Data.Details, Is.Empty);
    }
}