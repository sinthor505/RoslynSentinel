using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelQualityTools
{
    private readonly PerformanceEngine _performanceEngine;
    private readonly SecurityEngine _securityEngine;
    private readonly TestingEngine _testingEngine;
    private readonly ControlFlowEngine _controlFlowEngine;
    private readonly LogicOptimizationEngine _logicOptimizationEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly ILogger<SentinelQualityTools> _logger;

    public SentinelQualityTools(
        PerformanceEngine performanceEngine,
        SecurityEngine securityEngine,
        TestingEngine testingEngine,
        ControlFlowEngine controlFlowEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        AnalysisEngine analysisEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        ILogger<SentinelQualityTools> logger)
    {
        _performanceEngine = performanceEngine;
        _securityEngine = securityEngine;
        _testingEngine = testingEngine;
        _controlFlowEngine = controlFlowEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _analysisEngine = analysisEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Analyzes a file for common performance issues.")]
    public async Task<List<PerformanceIssueReport>> AnalyzePerformance(string filePath) 
        => await _performanceEngine.AnalyzePerformanceAsync(filePath);

    [McpServerTool]
    [Description("Analyzes a file for potential security vulnerabilities.")]
    public async Task<List<SecurityIssueReport>> AnalyzeSecurity(string filePath) 
        => await _securityEngine.AnalyzeSecurityAsync(filePath);

    [McpServerTool]
    [Description("Generates a unit test skeleton for a class.")]
    public async Task<TestSkeletonReport> GenerateTestSkeleton(string filePath, string className) 
        => await _testingEngine.GenerateTestSkeletonAsync(filePath, className);

    [McpServerTool]
    [Description("Analyzes execution paths for test coverage.")]
    public async Task<PathCoverageReport> AnalyzePathCoverage(string filePath, string methodName) 
        => await _controlFlowEngine.AnalyzePathCoverageAsync(filePath, methodName);

    [McpServerTool]
    [Description("Adds ArgumentNullException.ThrowIfNull guard clauses for all reference parameters in a method.")]
    public async Task<string> AddGuardClauses(string filePath, string methodName)
    {
        return await _logicOptimizationEngine.AddGuardClausesAsync(filePath, methodName);
    }

    [McpServerTool]
    [Description("Scans for IDisposable objects that are not properly disposed. Optionally filtered by project.")]
    public async Task<List<string>> OptimizeResourceDisposal(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.OptimizeResourceDisposalAsync(filePath, projectName);

    [McpServerTool]
    [Description("Scans for common string comparison pitfalls. Optionally filtered by project.")]
    public async Task<List<string>> DetectInefficientStringComparisons(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.DetectInefficientStringComparisonsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Finds potential boxing allocations. Optionally filtered by project.")]
    public async Task<List<string>> FindBoxingAllocations(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.FindBoxingAllocationsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Adds a BenchmarkDotNet stub class for performance testing a specific method.")]
    public async Task<string> AddBenchmarkStub(string filePath, string className, string methodName) 
        => await _testingEngine.AddBenchmarkStubAsync(filePath, className, methodName);

    [McpServerTool]
    [Description("Analyzes a solution or project for deadlocks. Optional scope.")]
    public async Task<List<string>> FindPossibleDeadlocks(string? projectName = null, string? filePath = null) 
        => await _analysisEngine.FindPossibleDeadlocksAsync(projectName, filePath);

    [McpServerTool]
    [Description("Analyzes SemaphoreSlim usage to find potentially missing Release() calls.")]
    public async Task<List<string>> AnalyzeSemaphoreUsage(string filePath) 
        => await _analysisEngine.AnalyzeSemaphoreUsageAsync(filePath);

    [McpServerTool]
    [Description("Scans a file for potential memory leaks (e.g. unhooked events).")]
    public async Task<List<string>> DetectMemoryLeaks(string filePath) 
        => await _analysisEngine.DetectMemoryLeaksAsync(filePath);

    [McpServerTool]
    [Description("Detects dangerous 'async void' usage that can crash the application.")]
    public async Task<List<AsyncSafetyReport>> FindTaskVoidUsage(string filePath) 
        => await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync(filePath);

    [McpServerTool]
    [Description("Detects Task.Yield() calls.")]
    public async Task<List<AsyncSafetyReport>> FindTaskYieldUsage(string filePath) 
        => await _asyncSafetyEngine.FindTaskYieldUsageAsync(filePath);

    [McpServerTool]
    [Description("Scans for System.Reflection usage. Optionally filtered by project.")]
    public async Task<List<string>> DetectReflectionUsage(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.DetectReflectionUsageAsync(filePath, projectName);

    [McpServerTool]
    [Description("Scans for empty catch blocks. Optionally filtered by project.")]
    public async Task<List<string>> CheckForEmptyCatchBlocks(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath, projectName);

    [McpServerTool]
    [Description("Detects Task.Delay() usage.")]
    public async Task<List<AsyncSafetyReport>> FindTaskDelayUsage(string filePath) 
        => await _asyncSafetyEngine.FindTaskDelayUsageAsync(filePath);

    [McpServerTool]
    [Description("Detects redundant type casts. Optionally filtered by project.")]
    public async Task<List<string>> CheckForRedundantCast(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.CheckForRedundantCastAsync(filePath, projectName);

    [McpServerTool]
    [Description("Detects redundant Task.Delay(0) calls.")]
    public async Task<List<AsyncSafetyReport>> FindTaskDelayZeroUsage(string filePath) 
        => await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync(filePath);

    [McpServerTool]
    [Description("Detects sequential await calls that could be parallelized.")]
    public async Task<List<AsyncSafetyReport>> FindTaskWhenAllUsage(string filePath) 
        => await _asyncSafetyEngine.FindTaskWhenAllUsageAsync(filePath);

    [McpServerTool]
    [Description("Analyzes a file for potential infinite loops.")]
    public async Task<List<string>> FindPossibleInfiniteLoops(string filePath) 
        => await _analysisEngine.FindPossibleInfiniteLoopsAsync(filePath);

    [McpServerTool]
    [Description("Detects unawaited Task calls. Optionally filtered by project.")]
    public async Task<List<string>> DetectMismatchedAwait(string? filePath = null, string? projectName = null) 
        => await _analysisEngine.DetectMismatchedAwaitAsync(filePath, projectName);

    [McpServerTool]
    [Description("Scans a file for hardcoded file system paths.")]
    public async Task<List<SecurityIssueReport>> FindHardcodedPaths(string filePath) 
        => await _securityEngine.FindHardcodedPathsAsync(filePath);

    [McpServerTool]
    [Description("Scans a file for potential SQL injection vulnerabilities.")]
    public async Task<List<SecurityIssueReport>> CheckForSqlInjection(string filePath) 
        => await _securityEngine.CheckForSqlInjectionAsync(filePath);
}
