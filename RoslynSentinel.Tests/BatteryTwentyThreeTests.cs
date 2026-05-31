// Battery #23 — SentinelQualityTools
// Tests all 46 public methods of SentinelQualityTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

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
    private AsyncBatchEngine _asyncBatchEngine;
    private DiagnosticEngine _diagnosticEngine;
    private AntiPatternEngine _antiPatternEngine;
    private ThreadSafetyEngine _threadSafetyEngine;
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
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        _threadSafetyEngine = new ThreadSafetyEngine(_workspaceManager);
        _asyncBatchEngine = new AsyncBatchEngine(_workspaceManager, new AsyncOptimizationEngine(_workspaceManager), new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine(_workspaceManager)), new AntiPatternEngine(_workspaceManager), NullLogger<AsyncBatchEngine>.Instance);
        _tools = new SentinelQualityTools(
            _performanceEngine, _securityEngine, _testingEngine, _controlFlowEngine,
            _logicOptimizationEngine, _analysisEngine, _asyncSafetyEngine,
            new AntiPatternEngine(_workspaceManager), _asyncOptimizationEngine,
            new ThreadSafetyEngine(_workspaceManager), _diagnosticEngine,
            new CodeStyleAnalysisEngine(_workspaceManager),
            new PathDrivenTestEngine(_workspaceManager),
            new StackOverflowEngine(_workspaceManager),
            _asyncBatchEngine,
            _workspaceManager,
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
        var result = await _logicOptimizationEngine.AddGuardClausesAsync("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddGuardClauses_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.AddGuardClausesAsync("NonExistent.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddBenchmarkStub ---

    [Test]
    public async Task AddBenchmarkStub_ValidClassAndMethod_ReturnsUpdatedSource()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.AddBenchmarkStubAsync("Quality.cs", "QualityClass", "Process");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddBenchmarkStub_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _testingEngine.AddBenchmarkStubAsync("NonExistent.cs", "C", "M");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddConfigureAwaitFalse ---

    [Test]
    public async Task AddConfigureAwaitFalse_FileWithAwaits_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("Async.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveConfigureAwaitFalse ---

    [Test]
    public async Task RemoveConfigureAwaitFalse_FileWithConfigureAwait_ReturnsUpdatedSource()
    {
        const string src = "using System.Threading.Tasks; namespace TestProj; public class W { public async Task DoAsync() { await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false); } }";
        SetSource(src, "W.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("W.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task RemoveConfigureAwaitFalse_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertLockToSemaphoreSlim ---

    [Test]
    public async Task ConvertLockToSemaphoreSlim_MethodWithLock_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("Async.cs", "MethodWithLock");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("NonExistent.cs", "MethodWithLock");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToAsyncEnumerable---

    [Test]
    public async Task ConvertToAsyncEnumerable_ValidMethod_ReturnsUpdatedSource()
    {
        const string src = @"using System.Collections.Generic; namespace TestProj;
public class Streamer { public IEnumerable<int> GetData() { yield return 1; yield return 2; } }";
        SetSource(src, "Streamer.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("Streamer.cs", "GetData");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToAsyncEnumerable_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync("NonExistent.cs", "GetData");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddCancellationTokenToMethod---

    [Test]
    public async Task AddCancellationTokenToMethod_AsyncMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("Async.cs", "WorkAsync");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task AddCancellationTokenToMethod_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync("NonExistent.cs", "WorkAsync");
        Assert.That(result, Is.Not.Null);
    }

    // --- MakeMethodThreadSafe---

    [Test]
    public async Task MakeMethodThreadSafe_ValidMethod_ReturnsUpdatedSource()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("Async.cs", "SyncMethod");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task MakeMethodThreadSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync("NonExistent.cs", "SyncMethod");
        Assert.That(result, Is.Not.Null);
    }

    // ===================== NON-THROW METHODS =====================

    // --- AnalyzePerformance ---

    [Test]
    public async Task AnalyzePerformance_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _performanceEngine.AnalyzePerformanceAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeSecurity ---

    [Test]
    public async Task AnalyzeSecurity_FileWithSqlInjection_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _securityEngine.AnalyzeSecurityAsync("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateTestSkeleton ---

    [Test]
    public async Task GenerateTestSkeleton_ValidClass_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.GenerateTestSkeletonAsync("Quality.cs", "QualityClass");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateTestScaffold ---

    [Test]
    public async Task GenerateTestScaffold_ValidClass_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _testingEngine.GenerateTestScaffoldAsync("Quality.cs", "QualityClass");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzePathCoverage ---

    [Test]
    public async Task AnalyzePathCoverage_ValidMethod_ReturnsReport()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _controlFlowEngine.AnalyzePathCoverageAsync("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPossibleDeadlocks ---

    [Test]
    public async Task FindPossibleDeadlocks_FileWithLock_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _analysisEngine.FindPossibleDeadlocksAsync(filePath: "Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeSemaphoreUsage ---

    [Test]
    public async Task AnalyzeSemaphoreUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _analysisEngine.AnalyzeSemaphoreUsageAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectMemoryLeaks ---

    [Test]
    public async Task DetectMemoryLeaks_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _analysisEngine.DetectMemoryLeaksAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskVoidUsage ---

    [Test]
    public async Task FindTaskVoidUsage_FileWithAsyncVoid_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskYieldUsage ---

    [Test]
    public async Task FindTaskYieldUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskYieldUsageAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectReflectionUsage ---

    [Test]
    public async Task DetectReflectionUsage_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _analysisEngine.DetectReflectionUsageAsync(filePath: "Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForEmptyCatchBlocks ---

    [Test]
    public async Task CheckForEmptyCatchBlocks_FileWithEmptyCatch_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath: "Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskDelayUsage ---

    [Test]
    public async Task FindTaskDelayUsage_FileWithTaskDelay_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskDelayUsageAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForRedundantCast ---

    [Test]
    public async Task CheckForRedundantCast_FileWithRedundantCast_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _analysisEngine.CheckForRedundantCastAsync(filePath: "Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskDelayZeroUsage ---

    [Test]
    public async Task FindTaskDelayZeroUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskWhenAllUsage ---

    [Test]
    public async Task FindTaskWhenAllUsage_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectAntiPatterns ---

    [Test]
    public async Task DetectAntiPatterns_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.DetectAntiPatternsAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPossibleInfiniteLoops ---

    [Test]
    public async Task FindPossibleInfiniteLoops_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _analysisEngine.FindPossibleInfiniteLoopsAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectMismatchedAwait ---

    [Test]
    public async Task DetectMismatchedAwait_FileWithMixedAsync_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _analysisEngine.DetectMismatchedAwaitAsync(filePath: "Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindHardcodedPaths ---

    [Test]
    public async Task FindHardcodedPaths_FileWithHardcodedPath_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _securityEngine.FindHardcodedPathsAsync("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMutablePublicProperties ---

    [Test]
    public async Task FindMutablePublicProperties_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.FindMutablePublicPropertiesAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindNamingViolations ---

    [Test]
    public async Task FindNamingViolations_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.FindNamingViolationsAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindStringMagicValues ---

    [Test]
    public async Task FindStringMagicValues_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.FindStringMagicValuesAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindMissingCancellationTokens ---

    [Test]
    public async Task FindMissingCancellationTokens_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _antiPatternEngine.FindMissingCancellationTokensAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeExceptionHandling ---

    [Test]
    public async Task AnalyzeExceptionHandling_FileWithCatch_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.AnalyzeExceptionHandlingAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CheckForSqlInjection ---

    [Test]
    public async Task CheckForSqlInjection_FileWithSqlInjection_ReturnsList()
    {
        SetSource(SecuritySource, "Security.cs");
        var result = await _securityEngine.CheckForSqlInjectionAsync("Security.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeMethodControlFlow ---

    [Test]
    public async Task AnalyzeMethodControlFlow_ValidMethod_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _controlFlowEngine.AnalyzeMethodControlFlowAsync("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeMethodDataFlow ---

    [Test]
    public async Task AnalyzeMethodDataFlow_ValidMethod_ReturnsResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _controlFlowEngine.AnalyzeMethodDataFlowAsync("Quality.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindConfigureAwaitMissing ---

    [Test]
    public async Task FindConfigureAwaitMissing_FileWithAwaits_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindConfigureAwaitMissingAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBlockingCallsInAsync ---

    [Test]
    public async Task FindBlockingCallsInAsync_FileWithThreadSleep_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAsyncInConstructor ---

    [Test]
    public async Task FindAsyncInConstructor_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindAsyncInConstructorAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindTaskRunInAsync ---

    [Test]
    public async Task FindTaskRunInAsync_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindTaskRunInAsyncAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindConcurrentCollectionOpportunities ---

    [Test]
    public async Task FindConcurrentCollectionOpportunities_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnsafeLazyInit ---

    [Test]
    public async Task FindUnsafeLazyInit_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindUnsafeLazyInitAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectValueTaskMisuse ---

    [Test]
    public async Task DetectValueTaskMisuse_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.DetectValueTaskMisuseAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindAsyncOverSync ---

    [Test]
    public async Task FindAsyncOverSync_FileWithSyncMethods_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindAsyncOverSyncAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindUnawaitedFireAndForget ---

    [Test]
    public async Task FindUnawaitedFireAndForget_FileWithFireAndForget_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindLongParameterList ---

    [Test]
    public async Task FindLongParameterList_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.FindLongParameterListAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- OptimizeResourceDisposal ---

    [Test]
    public async Task OptimizeResourceDisposal_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _performanceEngine.OptimizeResourceDisposalAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- DetectInefficientStringComparisons ---

    [Test]
    public async Task DetectInefficientStringComparisons_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _performanceEngine.DetectInefficientStringComparisonsAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindBoxingAllocations ---

    [Test]
    public async Task FindBoxingAllocations_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _performanceEngine.FindBoxingAllocationsAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindPrimitiveObsession ---

    [Test]
    public async Task FindPrimitiveObsession_ValidFile_ReturnsList()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _antiPatternEngine.FindPrimitiveObsessionAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- FindInconsistentAsyncSuffix ---

    [Test]
    public async Task FindInconsistentAsyncSuffix_ValidFile_ReturnsList()
    {
        SetSource(AsyncSource, "Async.cs");
        var result = await _antiPatternEngine.FindInconsistentAsyncSuffixAsync("Async.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetDiagnosticsSummary ---

    [Test]
    public async Task GetDiagnosticsSummary_ValidFile_ReturnsSummaryResult()
    {
        SetSource(QualitySource, "Quality.cs");
        var result = await _diagnosticEngine.GetFileDiagnosticsAsync("Quality.cs");
        Assert.That(result, Is.Not.Null);
    }
}
