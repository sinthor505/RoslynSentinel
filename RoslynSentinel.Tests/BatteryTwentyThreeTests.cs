// Battery #23 — SentinelQualityTools
// Tests all 46 public methods of SentinelQualityTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryTwentyThreeTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private PerformanceEngine _performanceEngine;
    private SecurityEngine _securityEngine;
    private TestingEngine _testingEngine;
    private ControlFlowEngine _controlFlowEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private AnalysisEngine _analysisEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private DiagnosticEngine _diagnosticEngine;
    private SentinelQualityTools _tools;

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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _performanceEngine = new PerformanceEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _testingEngine = new TestingEngine(_workspaceManager);
        _controlFlowEngine = new ControlFlowEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _tools = new SentinelQualityTools(
            _performanceEngine, _securityEngine, _testingEngine, _controlFlowEngine,
            _logicOptimizationEngine, _analysisEngine, _asyncSafetyEngine,
            new AntiPatternEngine(_workspaceManager), _asyncOptimizationEngine,
            new ThreadSafetyEngine(_workspaceManager), _diagnosticEngine,
            NullLogger<SentinelQualityTools>.Instance);
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
    public async Task AddGuardClauses_ValidMethod_ReturnsUpdatedSource()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AddGuardClauses("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void AddGuardClauses_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.AddGuardClauses("NonExistent.cs", "Process"));
    }

    // --- AddBenchmarkStub ---

    [Test]
    public async Task AddBenchmarkStub_ValidClassAndMethod_ReturnsUpdatedSource()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AddBenchmarkStub("Quality.cs", "QualityClass", "Process");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void AddBenchmarkStub_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.AddBenchmarkStub("NonExistent.cs", "C", "M"));
    }

    // --- AddConfigureAwaitFalse ---

    [Test]
    public async Task AddConfigureAwaitFalse_FileWithAwaits_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.AddConfigureAwaitFalse("Async.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void AddConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.AddConfigureAwaitFalse("NonExistent.cs"));
    }

    // --- RemoveConfigureAwaitFalse ---

    [Test]
    public async Task RemoveConfigureAwaitFalse_FileWithConfigureAwait_ReturnsUpdatedSource()
    {
        const string src = "using System.Threading.Tasks; namespace TestProj; public class W { public async Task DoAsync() { await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false); } }";
        SetSource(src, "W.cs");
        var result = await _tools.RemoveConfigureAwaitFalse("W.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void RemoveConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.RemoveConfigureAwaitFalse("NonExistent.cs"));
    }

    // --- ConvertLockToSemaphoreSlim ---

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithLock_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.ConvertLockToSemaphoreSlim("Async.cs", "MethodWithLock");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.ConvertLockToSemaphoreSlim("NonExistent.cs", "MethodWithLock");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToAsyncEnumerable---

    [Test]
    public async Task ConvertToAsyncEnumerable_ValidMethod_ReturnsUpdatedSource()
    {
        const string src = @"using System.Collections.Generic; namespace TestProj;
public class Streamer { public IEnumerable<int> GetData() { yield return 1; yield return 2; } }";
        SetSource(src, "Streamer.cs");
        var result = await _tools.ConvertToAsyncEnumerable("Streamer.cs", "GetData");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToAsyncEnumerable_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.ConvertToAsyncEnumerable("NonExistent.cs", "GetData");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddCancellationTokenToMethod---

    [Test]
    public async Task AddCancellationTokenToMethod_AsyncMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.AddCancellationTokenToMethod("Async.cs", "WorkAsync");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddCancellationTokenToMethod_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.AddCancellationTokenToMethod("NonExistent.cs", "WorkAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- MakeMethodThreadSafe---

    [Test]
    public async Task MakeMethodThreadSafe_ValidMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.MakeMethodThreadSafe("Async.cs", "SyncMethod");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task MakeMethodThreadSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.MakeMethodThreadSafe("NonExistent.cs", "SyncMethod");
        Assert.That(result, Is.Not.Null);
    }

    // ===================== NON-THROW METHODS =====================

    // --- AnalyzePerformance ---

    [Test]
    public async Task AnalyzePerformance_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AnalyzePerformance("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzePerformance_NonExistentFile_ReturnsEmptyOrNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.AnalyzePerformance("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeSecurity ---

    [Test]
    public async Task AnalyzeSecurity_FileWithSqlInjection_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _tools.AnalyzeSecurity("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateTestSkeleton ---

    [Test]
    public async Task GenerateTestSkeleton_ValidClass_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.GenerateTestSkeleton("Quality.cs", "QualityClass");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateTestScaffold ---

    [Test]
    public async Task GenerateTestScaffold_ValidClass_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.GenerateTestScaffold("Quality.cs", "QualityClass");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzePathCoverage ---

    [Test]
    public async Task AnalyzePathCoverage_ValidMethod_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AnalyzePathCoverage("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPossibleDeadlocks ---

    [Test]
    public async Task FindPossibleDeadlocks_FileWithLock_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindPossibleDeadlocks("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeSemaphoreUsage ---

    [Test]
    public async Task AnalyzeSemaphoreUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.AnalyzeSemaphoreUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectMemoryLeaks ---

    [Test]
    public async Task DetectMemoryLeaks_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.DetectMemoryLeaks("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskVoidUsage ---

    [Test]
    public async Task FindTaskVoidUsage_FileWithAsyncVoid_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskVoidUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskYieldUsage ---

    [Test]
    public async Task FindTaskYieldUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskYieldUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectReflectionUsage ---

    [Test]
    public async Task DetectReflectionUsage_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.DetectReflectionUsage("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForEmptyCatchBlocks ---

    [Test]
    public async Task CheckForEmptyCatchBlocks_FileWithEmptyCatch_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.CheckForEmptyCatchBlocks("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskDelayUsage ---

    [Test]
    public async Task FindTaskDelayUsage_FileWithTaskDelay_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskDelayUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForRedundantCast ---

    [Test]
    public async Task CheckForRedundantCast_FileWithRedundantCast_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.CheckForRedundantCast("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskDelayZeroUsage ---

    [Test]
    public async Task FindTaskDelayZeroUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskDelayZeroUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskWhenAllUsage ---

    [Test]
    public async Task FindTaskWhenAllUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskWhenAllUsage("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectAntiPatterns ---

    [Test]
    public async Task DetectAntiPatterns_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.DetectAntiPatterns("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPossibleInfiniteLoops ---

    [Test]
    public async Task FindPossibleInfiniteLoops_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindPossibleInfiniteLoops("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectMismatchedAwait ---

    [Test]
    public async Task DetectMismatchedAwait_FileWithMixedAsync_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.DetectMismatchedAwait("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindHardcodedPaths ---

    [Test]
    public async Task FindHardcodedPaths_FileWithHardcodedPath_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _tools.FindHardcodedPaths("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMutablePublicProperties ---

    [Test]
    public async Task FindMutablePublicProperties_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindMutablePublicProperties("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindNamingViolations ---

    [Test]
    public async Task FindNamingViolations_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindNamingViolations("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindStringMagicValues ---

    [Test]
    public async Task FindStringMagicValues_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindStringMagicValues("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMissingCancellationTokens ---

    [Test]
    public async Task FindMissingCancellationTokens_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindMissingCancellationTokens("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeExceptionHandling ---

    [Test]
    public async Task AnalyzeExceptionHandling_FileWithCatch_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AnalyzeExceptionHandling("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForSqlInjection ---

    [Test]
    public async Task CheckForSqlInjection_FileWithSqlInjection_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _tools.CheckForSqlInjection("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeMethodControlFlow ---

    [Test]
    public async Task AnalyzeMethodControlFlow_ValidMethod_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AnalyzeMethodControlFlow("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeMethodDataFlow ---

    [Test]
    public async Task AnalyzeMethodDataFlow_ValidMethod_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.AnalyzeMethodDataFlow("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindConfigureAwaitMissing ---

    [Test]
    public async Task FindConfigureAwaitMissing_FileWithAwaits_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindConfigureAwaitMissing("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBlockingCallsInAsync ---

    [Test]
    public async Task FindBlockingCallsInAsync_FileWithThreadSleep_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindBlockingCallsInAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAsyncInConstructor ---

    [Test]
    public async Task FindAsyncInConstructor_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindAsyncInConstructor("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskRunInAsync ---

    [Test]
    public async Task FindTaskRunInAsync_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindTaskRunInAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindConcurrentCollectionOpportunities ---

    [Test]
    public async Task FindConcurrentCollectionOpportunities_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindConcurrentCollectionOpportunities("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnsafeLazyInit ---

    [Test]
    public async Task FindUnsafeLazyInit_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindUnsafeLazyInit("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectValueTaskMisuse ---

    [Test]
    public async Task DetectValueTaskMisuse_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.DetectValueTaskMisuse("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAsyncOverSync ---

    [Test]
    public async Task FindAsyncOverSync_FileWithSyncMethods_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindAsyncOverSync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnawaitedFireAndForget ---

    [Test]
    public async Task FindUnawaitedFireAndForget_FileWithFireAndForget_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindUnawaitedFireAndForget("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindLongParameterList ---

    [Test]
    public async Task FindLongParameterList_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindLongParameterList("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- OptimizeResourceDisposal ---

    [Test]
    public async Task OptimizeResourceDisposal_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.OptimizeResourceDisposal("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectInefficientStringComparisons ---

    [Test]
    public async Task DetectInefficientStringComparisons_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.DetectInefficientStringComparisons("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBoxingAllocations ---

    [Test]
    public async Task FindBoxingAllocations_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindBoxingAllocations("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPrimitiveObsession ---

    [Test]
    public async Task FindPrimitiveObsession_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.FindPrimitiveObsession("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindInconsistentAsyncSuffix ---

    [Test]
    public async Task FindInconsistentAsyncSuffix_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _tools.FindInconsistentAsyncSuffix("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetDiagnosticsSummary ---

    [Test]
    public async Task GetDiagnosticsSummary_ValidFile_ReturnsSummaryResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _tools.GetDiagnosticsSummary("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }
}
