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
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly ILogger<SentinelQualityTools> _logger;

    public SentinelQualityTools(
        PerformanceEngine performanceEngine,
        SecurityEngine securityEngine,
        TestingEngine testingEngine,
        ControlFlowEngine controlFlowEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        AnalysisEngine analysisEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        AntiPatternEngine antiPatternEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        ThreadSafetyEngine threadSafetyEngine,
        DiagnosticEngine diagnosticEngine,
        ILogger<SentinelQualityTools> logger)
    {
        _performanceEngine = performanceEngine;
        _securityEngine = securityEngine;
        _testingEngine = testingEngine;
        _controlFlowEngine = controlFlowEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _analysisEngine = analysisEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _antiPatternEngine = antiPatternEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _diagnosticEngine = diagnosticEngine;
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
    [Description("Generates an xUnit+Moq test class scaffold for a given class. Auto-detects constructor parameters to create Mock<T> fields, instantiates the SUT, and adds one test method stub per public method with Arrange/Act/Assert comments.")]
    public async Task<TestScaffoldResult> GenerateTestScaffold(string filePath, string className)
        => await _testingEngine.GenerateTestScaffoldAsync(filePath, className);

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
    [Description("Detects common C# anti-patterns introduced by AI code generation: BlockingTaskWait (.Result/.Wait()/.GetAwaiter().GetResult()), AsyncVoidMethod (non-event-handler async void), StringConcatInLoop (string += in loops), CatchExceptionSwallow (empty catch blocks), DisposedObjectUsage (use after Dispose), MissingCancellationToken (public async methods with 3+ params lacking CancellationToken), MagicNumber (unexplained numeric literals). Optionally scope by filePath, projectName, or patternFilter array.")]
    public async Task<List<AntiPatternFinding>> DetectAntiPatterns(
        string? filePath = null,
        string? projectName = null,
        string[]? patternFilter = null)
        => await _antiPatternEngine.DetectAntiPatternsAsync(filePath, projectName, patternFilter);

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
    [Description("Finds public mutable properties (public setter) on non-DTO public classes. Reports classes that expose state directly rather than through controlled mutation. Classes whose names end with Request/Response/Dto/ViewModel/Model/Options/Settings/Config/Entity/Event/Command/Query are excluded. Scope to a file or project, or scan the whole solution.")]
    public async Task<List<AntiPatternFinding>> FindMutablePublicProperties(
        string? filePath = null, string? projectName = null)
        => await _antiPatternEngine.FindMutablePublicPropertiesAsync(filePath, projectName);

    [McpServerTool]
    [Description("Checks C# naming conventions across a file, project, or solution. Reports: private fields not following '_camelCase', non-private methods not following PascalCase, and parameters not following camelCase. Returns AntiPatternFinding records with Pattern='NamingViolation', Severity='Low'.")]
    public async Task<List<AntiPatternFinding>> FindNamingViolations(
        string? filePath = null, string? projectName = null)
        => await _antiPatternEngine.FindNamingViolationsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Finds string literal magic values: the same string appearing 3+ times across a file, project, or solution. Returns value, occurrence count, suggested constant name (PascalCase), and all file/line locations. Excludes empty strings, nameof() args, attribute arguments, and strings shorter than 3 chars.")]
    public async Task<List<MagicValueFinding>> FindStringMagicValues(string? filePath = null, string? projectName = null, int minOccurrences = 3)
        => await _antiPatternEngine.FindStringMagicValuesAsync(filePath, projectName, minOccurrences);

    [McpServerTool]
    [Description("Finds async methods that lack a CancellationToken parameter but call at least one method that accepts one. These are methods that should be threading cancellation through but aren't. Returns method name, containing type, file/line, and the callee names that accept CancellationToken.")]
    public async Task<List<MissingCancellationTokenFinding>> FindMissingCancellationTokens(string? filePath = null, string? projectName = null)
        => await _antiPatternEngine.FindMissingCancellationTokensAsync(filePath, projectName);

    [McpServerTool]
    [Description("Analyzes exception handling anti-patterns in a file: CatchAll (bare catch or catch Exception), EmptyRethrow (throw ex; loses stack trace), SwallowedException (catch with no rethrow/log/return), ExceptionAsControlFlow (catching FormatException etc. inside loops). Severity: High for CatchAll/EmptyRethrow, Medium for SwallowedException/ExceptionAsControlFlow.")]
    public async Task<List<ExceptionHandlingFinding>> AnalyzeExceptionHandling(string filePath)
        => await _antiPatternEngine.AnalyzeExceptionHandlingAsync(filePath);

    [McpServerTool]
    [Description("""
        Scans a file for potential SQL injection vulnerabilities.
        
        Detects calls to common SQL execution methods (ExecuteNonQuery, ExecuteReader,
        ExecuteScalar, FromSqlRaw, Query, etc.) where the first argument is a dynamic
        string — either an interpolated string with expressions ($"...{x}...") or string
        concatenation involving a non-literal operand.
        
        Returns a list of SecurityIssueReport with IssueType='PossibleSqlInjection',
        file path, line/column, and a description recommending parameterized queries.
        Does NOT check CommandText property assignments.
        """)]
    public async Task<List<SecurityIssueReport>> CheckForSqlInjection(string filePath)
        => await _securityEngine.CheckForSqlInjectionAsync(filePath);

    [McpServerTool]
    [Description("""
        Analyzes control flow for an entire method body using Roslyn's semantic analysis.
        
        Unlike the raw line-based analyze_control_flow tool, this takes a method name — no
        need to count line numbers or worry about accidentally including the method signature
        (which causes a 'statements not within the same statement list' error in line-based tools).
        
        Returns: EndPointIsReachable (false = all code paths return/throw), ReturnStatements,
        ThrowStatements, BreakStatements, ContinueStatements.
        
        If the method has multiple overloads, supply disambiguateLine (any line number that
        falls inside the desired overload's body) to select the correct one.
        """)]
    public async Task<ControlFlowAnalysisResult> AnalyzeMethodControlFlow(
        string filePath,
        string methodName,
        int? disambiguateLine = null)
        => await _controlFlowEngine.AnalyzeMethodControlFlowAsync(filePath, methodName, disambiguateLine);

    [McpServerTool]
    [Description("""
        Analyzes data flow for an entire method body using Roslyn's semantic analysis.
        
        Unlike the raw line-based analyze_data_flow tool, this takes a method name — no
        need to count line numbers or worry about accidentally including the method signature.
        
        Returns: DataFlowsIn (variables read but declared outside), DataFlowsOut (variables
        assigned inside and read outside), VariablesDeclared (locals), AlwaysAssigned (always
        initialized on all code paths), ReadInside, WrittenInside.
        
        If the method has multiple overloads, supply disambiguateLine (any line number that
        falls inside the desired overload's body) to select the correct one.
        """)]
    public async Task<DataFlowAnalysisResult> AnalyzeMethodDataFlow(
        string filePath,
        string methodName,
        int? disambiguateLine = null)
        => await _controlFlowEngine.AnalyzeMethodDataFlowAsync(filePath, methodName, disambiguateLine);

    [McpServerTool]
    [Description("Scans a file for await expressions missing .ConfigureAwait(false). Excludes classes ending in Controller/Hub/PageModel/ViewModel (those are app-level where ConfigureAwait is not needed). Returns method name, line, and reason.")]
    public async Task<List<AsyncSafetyReport>> FindConfigureAwaitMissing(string filePath)
        => await _asyncSafetyEngine.FindConfigureAwaitMissingAsync(filePath);

    [McpServerTool]
    [Description("Detects blocking calls inside async methods: Thread.Sleep, .Result, .Wait(), .GetAwaiter().GetResult(). These block the thread pool thread and can cause deadlocks.")]
    public async Task<List<AsyncSafetyReport>> FindBlockingCallsInAsync(string filePath)
        => await _asyncSafetyEngine.FindBlockingCallsInAsyncAsync(filePath);

    [McpServerTool]
    [Description("Finds constructors that call async methods or contain await expressions. Constructors cannot be async — use a factory method or AsyncHelper instead.")]
    public async Task<List<AsyncSafetyReport>> FindAsyncInConstructor(string filePath)
        => await _asyncSafetyEngine.FindAsyncInConstructorAsync(filePath);

    [McpServerTool]
    [Description("Detects 'await Task.Run(...)' patterns in server-side code. Task.Run wraps work in a new thread pool task which is wasteful in async server code. Prefer direct async methods.")]
    public async Task<List<AsyncSafetyReport>> FindTaskRunInAsync(string filePath)
        => await _asyncSafetyEngine.FindTaskRunInAsyncAsync(filePath);

    [McpServerTool]
    [Description("Finds lock statements that protect List<T> or Dictionary<K,V> fields and suggests replacing them with ConcurrentDictionary<K,V> or ImmutableDictionary for better performance.")]
    public async Task<List<AsyncSafetyReport>> FindConcurrentCollectionOpportunities(string filePath)
        => await _asyncSafetyEngine.FindConcurrentCollectionOpportunitiesAsync(filePath);

    [McpServerTool]
    [Description("Detects unsafe lazy initialization: double-checked locking without 'volatile' keyword (field may be partially initialized due to CPU reordering). Suggests Lazy<T> or volatile.")]
    public async Task<List<AsyncSafetyReport>> FindUnsafeLazyInit(string filePath)
        => await _asyncSafetyEngine.FindUnsafeLazyInitAsync(filePath);

    [McpServerTool]
    [Description("Adds .ConfigureAwait(false) to all await expressions in a file that don't already have it. Use libraryMode=true (default) for library code, false for ASP.NET app code. Idempotent — skips already-configured awaits.")]
    public async Task<string> AddConfigureAwaitFalse(string filePath, bool libraryMode = true)
        => await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(filePath, libraryMode);

    [McpServerTool]
    [Description("Removes all .ConfigureAwait(x) calls from a file, leaving the bare awaited expression. Useful when migrating library code to ASP.NET app code where ConfigureAwait is unnecessary.")]
    public async Task<string> RemoveConfigureAwaitFalse(string filePath)
        => await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(filePath);

    [McpServerTool]
    [Description("Converts lock statements inside a method to async-safe SemaphoreSlim pattern: adds a 'private readonly SemaphoreSlim _semaphore = new(1,1)' field, replaces each lock block with 'await _semaphore.WaitAsync(); try { ... } finally { _semaphore.Release(); }', and makes the method async if needed.")]
    public async Task<string> ConvertLockToSemaphoreSlim(string filePath, string methodName)
        => await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(filePath, methodName);

    [McpServerTool]
    [Description("Converts a method returning Task<List<T>>, Task<IEnumerable<T>>, or List<T> to IAsyncEnumerable<T>. Transforms 'results.Add(item)' patterns to 'yield return item', removes the list variable and return statement, and adds a CancellationToken parameter if missing.")]
    public async Task<string> ConvertToAsyncEnumerable(string filePath, string methodName)
        => await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(filePath, methodName);

    [McpServerTool]
    [Description("Detects invalid ValueTask usage patterns: (A) double await on same variable, (B) ValueTask stored and deferred-awaited with intervening statements, (C) ValueTask passed to Task.WhenAll() without .AsTask(), (D) .Result accessed on ValueTask. Reports method name and line for each violation.")]
    public async Task<List<AsyncSafetyReport>> DetectValueTaskMisuse(string filePath)
        => await _asyncSafetyEngine.DetectValueTaskMisuseAsync(filePath);

    [McpServerTool]
    [Description("Adds 'CancellationToken cancellationToken = default' as the last parameter to a method and propagates it to async callees in the method body that have a CancellationToken overload. Also adds cancellationToken to Task.Delay() calls. Returns the updated source.")]
    public async Task<string> AddCancellationTokenToMethod(string filePath, string methodName)
        => await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync(filePath, methodName);

    [McpServerTool]
    [Description("Adds a private lock object field and wraps a method body in a lock statement. Specify lockFieldName if '_lock' is already used for another type.")]
    public async Task<string> MakeMethodThreadSafe(string filePath, string methodName, string lockFieldName = "_lock")
        => await _threadSafetyEngine.MakeMethodThreadSafeAsync(filePath, methodName, lockFieldName);

    [McpServerTool]
    [Description("Finds async methods with no await expressions, or that only await Task.FromResult/Task.CompletedTask.")]
    public async Task<List<AsyncSafetyReport>> FindAsyncOverSync(string filePath)
        => await _asyncSafetyEngine.FindAsyncOverSyncAsync(filePath);

    [McpServerTool]
    [Description("Finds Task-returning methods called without await (fire-and-forget). Exceptions will be silently swallowed. Identified by 'Async' suffix heuristic.")]
    public async Task<List<AsyncSafetyReport>> FindUnawaitedFireAndForget(string filePath)
        => await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(filePath);

    [McpServerTool]
    [Description("Finds methods/constructors with >= minParameters (default 4) parameters. Excludes DI-only constructors (all params end with Service/Repository/Options/Factory).")]
    public async Task<List<AntiPatternFinding>> FindLongParameterList(string? filePath = null, string? projectName = null, int minParameters = 4)
        => await _antiPatternEngine.FindLongParameterListAsync(filePath, projectName, minParameters);

    [McpServerTool]
    [Description("Finds methods/constructors where the same primitive type (string/int/long/Guid/bool) appears 3+ times as distinct parameters.")]
    public async Task<List<AntiPatternFinding>> FindPrimitiveObsession(string? filePath = null, string? projectName = null)
        => await _antiPatternEngine.FindPrimitiveObsessionAsync(filePath, projectName);

    [McpServerTool]
    [Description("Finds async methods not ending with 'Async', and non-async methods that end with 'Async'. Excludes event handlers.")]
    public async Task<List<AntiPatternFinding>> FindInconsistentAsyncSuffix(string? filePath = null, string? projectName = null)
        => await _antiPatternEngine.FindInconsistentAsyncSuffixAsync(filePath, projectName);

    [McpServerTool]
    [Description("""
        Returns a grouped summary of Roslyn compiler diagnostics (errors and warnings) for a file,
        project, or the entire solution. Groups by diagnostic ID (e.g. CS0103) so you can see which
        issues are most common without scrolling through hundreds of raw messages.
        filePath: analyze a single file (mutually exclusive with projectName).
        projectName: analyze all files in a project.
        Leave both null to analyze the entire solution.
        topN: max number of groups to return, sorted by count descending (default 20).
        Returns TotalIssues, Errors, Warnings, and a TopIssues list with DiagnosticId, Severity,
        MessageTemplate, Count, and Locations.
        """)]
    public async Task<DiagnosticsSummaryResult> GetDiagnosticsSummary(
        string? filePath = null, string? projectName = null, int topN = 20)
    {
        DiagnosticSummary summary;
        if (filePath != null)
            summary = await _diagnosticEngine.GetFileDiagnosticsAsync(filePath);
        else if (projectName != null)
            summary = await _diagnosticEngine.GetProjectDiagnosticsAsync(projectName);
        else
            summary = await _diagnosticEngine.GetSolutionDiagnosticsAsync();

        var relevant = summary.Details
            .Where(d => d.Severity is "Error" or "Warning")
            .ToList();

        var groups = relevant
            .GroupBy(d => d.Id)
            .Select(g =>
            {
                var first = g.First();
                var locations = g.Select(d => $"{d.FilePath}:{d.StartLine}").Distinct().Take(10).ToList();
                return new DiagnosticGroupSummary(
                    DiagnosticId: g.Key,
                    Severity: first.Severity,
                    MessageTemplate: first.Message,
                    Count: g.Count(),
                    Locations: locations
                );
            })
            .OrderByDescending(g => g.Count)
            .Take(topN)
            .ToList();

        return new DiagnosticsSummaryResult(
            TotalIssues: relevant.Count,
            Errors: summary.Errors,
            Warnings: summary.Warnings,
            TopIssues: groups
        );
    }
}
