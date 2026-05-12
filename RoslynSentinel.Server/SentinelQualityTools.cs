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
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly PathDrivenTestEngine _pathDrivenTestEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
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
        CodeStyleAnalysisEngine codeStyleAnalysisEngine,
        PathDrivenTestEngine pathDrivenTestEngine,
        StackOverflowEngine stackOverflowEngine,
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
        _codeStyleAnalysisEngine = codeStyleAnalysisEngine;
        _pathDrivenTestEngine = pathDrivenTestEngine;
        _stackOverflowEngine = stackOverflowEngine;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Analyzes a file for common performance issues.")]
    public async Task<List<PerformanceIssueReport>> AnalyzePerformance(string filePath)
    {
        try
        {
            return await _performanceEngine.AnalyzePerformanceAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalyzePerformance unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"AnalyzePerformance for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

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
    [Description("Extends path coverage analysis with a cross-reference to test methods that exercise the given production method. Finds covering tests by name convention (test method name contains the production method name) and by direct call-site presence in the test body. Returns BranchesToTest (execution paths to cover) and CoveringTests (test file, test method name, line) with HasAnyCoverage flag.")]
    public async Task<TestCoverageMap> GetTestCoverageMap(string filePath, string methodName)
        => await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);

    [McpServerTool]
    [Description("Adds ArgumentNullException.ThrowIfNull guard clauses for all reference parameters in a method.")]
    public async Task<string> AddGuardClauses(string filePath, string methodName)
    {
        var result = await _logicOptimizationEngine.AddGuardClausesAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"AddGuardClauses failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Scans for IDisposable objects that are not properly disposed. Optionally filtered by project.")]
    public async Task<List<string>> OptimizeResourceDisposal(string? filePath = null, string? projectName = null)
    {
        try
        {
            return await _analysisEngine.OptimizeResourceDisposalAsync(filePath, projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizeResourceDisposal unexpected exception for '{FilePath}' / '{ProjectName}'", filePath, projectName);
            throw new InvalidOperationException($"OptimizeResourceDisposal failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

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
    {
        var result = await _testingEngine.AddBenchmarkStubAsync(filePath, className, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"AddBenchmarkStub failed for '{className}.{methodName}' in '{filePath}': " +
                "file not found in workspace, class not found, or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Analyzes a solution or project for deadlocks. Optional scope.")]
    public async Task<List<string>> FindPossibleDeadlocks(string? projectName = null, string? filePath = null)
    {
        try
        {
            return await _analysisEngine.FindPossibleDeadlocksAsync(projectName, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindPossibleDeadlocks unexpected exception for '{ProjectName}' / '{FilePath}'", projectName, filePath);
            throw new InvalidOperationException($"FindPossibleDeadlocks failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

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
    [Description("Scans for hardcoded file system paths. Pass filePath to scope to a single file, projectName to scope to a project, or leave both null to scan the whole solution.")]
    public async Task<List<SecurityIssueReport>> FindHardcodedPaths(string? filePath = null, string? projectName = null)
        => await _securityEngine.FindHardcodedPathsAsync(filePath, projectName);

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
        Scans for potential SQL injection vulnerabilities.
        Pass filePath to scope to a single file, projectName to scope to a project,
        or leave both null to scan the whole solution.

        Detects calls to common SQL execution methods (ExecuteNonQuery, ExecuteReader,
        ExecuteScalar, FromSqlRaw, Query, etc.) where the first argument is a dynamic
        string — either an interpolated string with expressions ($"...{x}...") or string
        concatenation involving a non-literal operand.

        Returns a list of SecurityIssueReport with IssueType='PossibleSqlInjection',
        file path, line/column, and a description recommending parameterized queries.
        Does NOT check CommandText property assignments.
        """)]
    public async Task<List<SecurityIssueReport>> CheckForSqlInjection(string? filePath = null, string? projectName = null)
    {
        try
        {
            return await _securityEngine.CheckForSqlInjectionAsync(filePath, projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckForSqlInjection unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"CheckForSqlInjection for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

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
    {
        var result = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(filePath, libraryMode);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"AddConfigureAwaitFalse failed for '{filePath}': " +
                "file not found in workspace. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Removes all .ConfigureAwait(x) calls from a file, leaving the bare awaited expression. Useful when migrating library code to ASP.NET app code where ConfigureAwait is unnecessary.")]
    public async Task<string> RemoveConfigureAwaitFalse(string filePath)
    {
        var result = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(filePath);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"RemoveConfigureAwaitFalse failed for '{filePath}': " +
                "file not found in workspace. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Converts lock statements inside a method to async-safe SemaphoreSlim pattern: adds a 'private readonly SemaphoreSlim _semaphore = new(1,1)' field, replaces each lock block with 'await _semaphore.WaitAsync(); try { ... } finally { _semaphore.Release(); }', and makes the method async if needed.")]
    public async Task<string> ConvertLockToSemaphoreSlim(string filePath, string methodName)
    {
        var result = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"ConvertLockToSemaphoreSlim failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace, method not found, or no lock statements found in method. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Converts a method returning Task<List<T>>, Task<IEnumerable<T>>, or List<T> to IAsyncEnumerable<T>. Transforms 'results.Add(item)' patterns to 'yield return item', removes the list variable and return statement, and adds a CancellationToken parameter if missing.")]
    public async Task<string> ConvertToAsyncEnumerable(string filePath, string methodName)
    {
        var result = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"ConvertToAsyncEnumerable failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace, method not found, or method does not return Task<List<T>> or similar. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Detects invalid ValueTask usage patterns: (A) double await on same variable, (B) ValueTask stored and deferred-awaited with intervening statements, (C) ValueTask passed to Task.WhenAll() without .AsTask(), (D) .Result accessed on ValueTask. Reports method name and line for each violation.")]
    public async Task<List<AsyncSafetyReport>> DetectValueTaskMisuse(string filePath)
        => await _asyncSafetyEngine.DetectValueTaskMisuseAsync(filePath);

    [McpServerTool]
    [Description("Adds 'CancellationToken cancellationToken = default' as the last parameter to a method and propagates it to async callees in the method body that have a CancellationToken overload. Also adds cancellationToken to Task.Delay() calls. Returns the updated source.")]
    public async Task<string> AddCancellationTokenToMethod(string filePath, string methodName)
    {
        var result = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"AddCancellationTokenToMethod failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Adds a private lock object field and wraps a method body in a lock statement. Specify lockFieldName if '_lock' is already used for another type.")]
    public async Task<string> MakeMethodThreadSafe(string filePath, string methodName, string lockFieldName = "_lock")
    {
        var result = await _threadSafetyEngine.MakeMethodThreadSafeAsync(filePath, methodName, lockFieldName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"MakeMethodThreadSafe failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Finds async methods with no await expressions, or that only await Task.FromResult/Task.CompletedTask.")]
    public async Task<List<AsyncSafetyReport>> FindAsyncOverSync(string filePath)
        => await _asyncSafetyEngine.FindAsyncOverSyncAsync(filePath);

    [McpServerTool]
    [Description("Finds Task-returning methods called without await (fire-and-forget). Exceptions will be silently swallowed. Pass filePath to scope to a single file, projectName to scope to a project, or leave both null to scan the whole solution.")]
    public async Task<List<AsyncSafetyReport>> FindUnawaitedFireAndForget(string? filePath = null, string? projectName = null)
        => await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(filePath, projectName);

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
    [Description("Detects unsafe or incorrect System.Text.Json usage patterns in a file: (1) JsonDocument.Parse() not wrapped in a 'using' — leaks pooled memory back to the ArrayPool, (2) JsonElement.GetProperty() instead of TryGetProperty() — throws KeyNotFoundException on missing keys at runtime. Returns SecurityIssueReport with IssueType, file path, line/column, and remediation description.")]
    public async Task<List<SecurityIssueReport>> DetectJsonAntiPatterns(string filePath)
        => await _securityEngine.DetectJsonAntiPatternsAsync(filePath);

    [McpServerTool]
    [Description("Detects statements within a specific method that are unreachable due to a preceding return, throw, break, or continue on all code paths. Returns string descriptions of each unreachable statement and the reason it cannot execute. Use before adding code at the end of a method to confirm the insertion point is actually reached.")]
    public async Task<List<string>> DetectUnreachableCode(string filePath, string methodName)
        => await _analysisEngine.DetectUnreachableCodeAsync(filePath, methodName);

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

    // ── Performance: new detectors ─────────────────────────────────────────────

    [McpServerTool]
    [Description("Detects LINQ N+1 query patterns: LINQ terminal calls (Where, FirstOrDefault, Any, Count, etc.) inside foreach/for/while loops where the loop variable appears in the LINQ chain — each iteration triggers a separate query. Reports file, line, and the loop variable involved.")]
    public async Task<List<PerformanceIssueReport>> FindLinqN1Patterns(string? filePath = null, string? projectName = null)
        => await _performanceEngine.FindLinqN1PatternsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Detects string interpolation ($\"...\") and string.Format() calls inside loop bodies. Each iteration allocates a new string — use StringBuilder for accumulation or move the format outside the loop.")]
    public async Task<List<PerformanceIssueReport>> FindStringFormatInLoops(string? filePath = null)
        => await _performanceEngine.FindStringFormatInLoopsAsync(filePath);

    [McpServerTool]
    [Description("Detects IEnumerable<T> or IQueryable<T> locals/parameters that are iterated more than once without a materializing call (ToList/ToArray). Multiple enumerations can execute a database query or generator multiple times. Reports variable name and enumeration line numbers.")]
    public async Task<List<PerformanceIssueReport>> FindMultipleEnumeration(string? filePath = null)
        => await _performanceEngine.FindMultipleEnumerationAsync(filePath);

    [McpServerTool]
    [Description("Detects LINQ chains of the form .Where(pred).First() / .Where(pred).Any() / .Where(pred).Count() where the intermediate Where creates an unnecessary IEnumerable allocation. Suggests collapsing to .First(pred) / .Any(pred) / .Count(pred) for a single-pass, allocation-free alternative.")]
    public async Task<List<PerformanceIssueReport>> FindLinqRedundantWhere(string? filePath = null)
        => await _performanceEngine.FindLinqRedundantWhereAsync(filePath);

    [McpServerTool]
    [Description("Detects explicit casts of Nullable<T> values to object or dynamic, which box the nullable and can produce surprising null-equality behavior ((object)(int?)null == null is true, but two boxed int? values with the same value are not reference-equal). Uses the semantic model for accuracy.")]
    public async Task<List<PerformanceIssueReport>> FindImplicitNullableBoxing(string? filePath = null)
        => await _performanceEngine.FindImplicitNullableBoxingAsync(filePath);

    // ── Analysis: new detectors ────────────────────────────────────────────────

    [McpServerTool]
    [Description("Detects classes that implement IDisposable AND declare a finalizer (~C()) without a disposed-flag guard (if (_disposed) return;). The GC may call the finalizer after Dispose() has already run, causing double-free of unmanaged resources.")]
    public async Task<List<string>> FindFinalizerOnDisposable(string? projectName = null)
        => await _analysisEngine.FindFinalizerOnDisposableAsync(projectName);

    [McpServerTool]
    [Description("Detects static fields that hold unbounded collections (Dictionary, List, HashSet, etc.) that are populated with .Add()/.TryAdd() but never .Clear()ed and have no Count-based size cap. A common memory exhaustion DoS vector when populated from user-controlled data.")]
    public async Task<List<string>> FindUnboundedStaticCollections(string? projectName = null)
        => await _analysisEngine.FindUnboundedStaticCollectionsAsync(projectName);

    [McpServerTool]
    [Description("Detects recursive methods that lack a depth parameter or an early-exit base-case guard (non-recursive if-return before the recursive call). Unbounded recursion on large inputs causes StackOverflowException and crashes the process with no recoverable exception.")]
    public async Task<List<string>> FindUnboundedRecursion(string? projectName = null)
        => await _analysisEngine.FindUnboundedRecursionAsync(projectName);

    [McpServerTool]
    [Description("Validates overload chain correctness across the solution. For each group of same-named methods where shorter overloads delegate to fuller ones, detects: (1) ChainMissingParameter — a parameter from the calling overload is absent from the forwarding call, silently dropping the caller's value; (2) ChainArgumentOrder — source parameters appear in inverted order in the forwarding call, binding to the wrong target parameter; (3) OverloadCycle — two overloads delegate to each other, causing guaranteed StackOverflowException. Requires a loaded solution.")]
    public async Task<List<string>> FindMisboundOverloadChains(string? projectName = null)
        => await _analysisEngine.FindMisboundOverloadChainsAsync(projectName);

    // ── Thread safety: new detectors ───────────────────────────────────────────

    [McpServerTool]
    [Description("Detects the unsafe lazy initialization pattern (if (_field == null) { _field = new X(); }) outside a lock and without a volatile field or Lazy<T>. Without volatile, the CPU or JIT may reorder the store and expose a partially-constructed object on another thread.")]
    public async Task<List<string>> FindUnsafeLazyInitThread(string? projectName = null, string? filePath = null)
        => await _threadSafetyEngine.FindUnsafeLazyInitAsync(projectName, filePath);

    [McpServerTool]
    [Description("Detects while/do-while loops containing Interlocked.CompareExchange that have no back-off (no Thread.Sleep, Thread.SpinWait, SpinWait.SpinOnce, or Task.Delay). A spinning CAS loop without back-off can peg a CPU core at 100% under contention (live-lock).")]
    public async Task<List<string>> FindCasLoopWithoutBackoff(string? projectName = null, string? filePath = null)
        => await _threadSafetyEngine.FindCasLoopWithoutBackoffAsync(projectName, filePath);

    [McpServerTool]
    [Description("Detects the double-checked locking (DCL) pattern where the lazily-initialized field is not declared volatile. Without volatile, a CPU or JIT may reorder the store and expose a partially-constructed object to the outer null-check on another thread. Use volatile or Lazy<T>.")]
    public async Task<List<string>> FindDoubleCheckedLocking(string? projectName = null, string? filePath = null)
        => await _threadSafetyEngine.FindDoubleCheckedLockingAsync(projectName, filePath);

    [McpServerTool]
    [Description("Detects the check-then-act race on Dictionary/ConcurrentDictionary: ContainsKey() or TryGetValue() followed by Add()/TryAdd() on the same variable outside of any lock. Another thread may insert the same key between the check and add. Use GetOrAdd() or TryAdd() for atomic insertion.")]
    public async Task<List<string>> FindCheckThenActOnDictionary(string? projectName = null, string? filePath = null)
        => await _threadSafetyEngine.FindCheckThenActOnDictionaryAsync(projectName, filePath);

    // ── Security: new detectors ────────────────────────────────────────────────

    [McpServerTool]
    [Description("Detects Regex patterns in string literals that contain nested quantifiers ((a+)+, (a*b*)+, etc.) — a common ReDoS vulnerability. On non-matching input these patterns cause catastrophic backtracking, making the CPU spin indefinitely. Reports the pattern and the dangerous construct.")]
    public async Task<List<SecurityIssueReport>> FindReDoSPatterns(string filePath)
        => await _securityEngine.FindReDoSPatternsAsync(filePath);

    [McpServerTool]
    [Description("Detects new Regex() or Regex.IsMatch/Match/Replace calls where the pattern argument is not a compile-time string literal. A non-literal pattern may originate from user input, enabling Regex injection (attacker-controlled matching logic) and ReDoS attacks.")]
    public async Task<List<SecurityIssueReport>> FindUnvalidatedRegexSource(string filePath)
        => await _securityEngine.FindUnvalidatedRegexSourceAsync(filePath);

    [McpServerTool]
    [Description("Detects new Regex() construction inside loop bodies (for/foreach/while/do). Constructing a Regex per iteration recompiles the pattern on every pass — a pure waste for literal patterns, and a ReDoS amplification vector for variable patterns. Hoist to a static readonly field.")]
    public async Task<List<SecurityIssueReport>> FindRegexNewInLoop(string filePath)
        => await _securityEngine.FindRegexNewInLoopAsync(filePath);

    // ── Async safety: new detectors ────────────────────────────────────────────

    [McpServerTool]
    [Description("Detects consecutive await expressions in async methods where neither result is used by the other await — missed Task.WhenAll() parallelism. Example: var a = await F(); var b = await G(); where b doesn't depend on a. Combining with Task.WhenAll cuts total latency to max(F,G).")]
    public async Task<List<AsyncSafetyReport>> FindSequentialIndependentAwaits(string? filePath = null)
        => await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync(filePath);

    [McpServerTool]
    [Description("Detects async void methods whose entire body is not wrapped in a try/catch. Unhandled exceptions inside async void crash the process via the unhandled-exception handler — there is no way for a caller to catch them. Wrap the body in try { } catch (Exception ex) { }.")]
    public async Task<List<AsyncSafetyReport>> FindAsyncVoidWithoutTryCatch(string? filePath = null)
        => await _asyncSafetyEngine.FindAsyncVoidWithoutTryCatchAsync(filePath);

    [McpServerTool]
    [Description("Detects DisposeAsync() calls that are not awaited. In a synchronous Dispose() method or any context without await, the ValueTask returned by DisposeAsync() is discarded — async cleanup (file flushes, connection teardown) finishes after the method returns, leaving resources dangling.")]
    public async Task<List<AsyncSafetyReport>> FindUnawakedDisposeAsync(string? filePath = null)
        => await _asyncSafetyEngine.FindUnawakedDisposeAsyncAsync(filePath);

    [McpServerTool]
    [Description("Detects Task or ValueTask-returning method calls assigned to fields or properties without await, where the field is never subsequently awaited or .Wait()ed anywhere in the class. The task silently fails — any exception it throws is never observed and may eventually crash the process via UnobservedTaskException.")]
    public async Task<List<AsyncSafetyReport>> FindUnobservedTaskInField(string? filePath = null)
        => await _asyncSafetyEngine.FindUnobservedTaskInFieldAsync(filePath);

    [McpServerTool]
    [Description("""
        EPC31: Finds async methods that have a CancellationToken parameter but call awaitable
        methods ('*Async') without forwarding the token. Detects missed cancellation propagation
        that leaves callers unable to cancel downstream work.
        filePath: scan a single file. Leave null to scan the entire solution.
        Returns method name, file, line, the unforwarded callee name, and the CT parameter name.
        """)]
    public async Task<List<AsyncSafetyReport>> FindCancellationTokenNotForwarded(string? filePath = null)
        => await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync(filePath);

    // ── Code style: new detectors ──────────────────────────────────────────────

    [McpServerTool]
    [Description("Detects public properties that expose mutable collection types (List<T>, Dictionary<K,V>, HashSet<T>, etc.) with a public non-init setter, allowing callers to completely replace the collection. Use IReadOnlyList<T>/IReadOnlyDictionary<K,V>, or change the setter to private/init.")]
    public async Task<List<string>> FindMutablePublicCollectionProperties(string? projectName = null)
        => await _codeStyleAnalysisEngine.FindMutablePublicCollectionPropertiesAsync(projectName);

    [McpServerTool]
    [Description("Finds switch statements on enum types that don't handle all enum members and have no default case. Returns the enum type name, the list of missing member names, the containing method name, file path, and line. Essential after adding a new enum member — tells you every switch that needs updating. Scope to a file or project, or scan the entire solution.")]
    public async Task<List<EnumSwitchGap>> FindNonExhaustiveEnumSwitches(
        string? filePath = null,
        string? projectName = null)
        => await _controlFlowEngine.FindNonExhaustiveEnumSwitchesAsync(filePath, projectName);

    [McpServerTool]
    [Description("Calculates the cyclomatic complexity of a method: 1 + one for each if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns the complexity score and the list of conditionals that contribute to it. Complexity guide: 1–4 = Low (easy to understand and test), 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High (split required). Use before modifying a method to gauge how risky the change is.")]
    public async Task<TestComplexityReport> GetMethodComplexity(string filePath, string methodName)
        => await _testingEngine.CalculateComplexityAsync(filePath, methodName);

    [McpServerTool]
    [Description("""
        Finds methods with 2 or more 'out' parameters — a common sign that the method
        is doing the work of returning multiple values through side-channels instead of
        a proper return type.

        Returns the current return type, the out-parameter names and types, and a suggested
        ValueTuple signature. Use ConvertOutParamsToValueTuple to apply the conversion
        automatically once you've reviewed the findings.

        Scope to a file or project, or scan the entire solution.
        """)]
    public async Task<List<OutParamMethodFinding>> FindMultipleOutParameterMethods(
        string? filePath = null,
        string? projectName = null)
        => await _antiPatternEngine.FindMultipleOutParameterMethodsAsync(filePath, projectName);

    [McpServerTool]
    [Description("""
        Flags methods that reassign a parameter inside the method body in a way that
        is invisible to the caller. Two warning patterns detected:

        ValueTypeParameterReassigned: A value-type parameter (int, bool, struct, enum)
        is reassigned but not returned — the caller's copy is unaffected. Use 'ref' or
        return the value if caller visibility was the intent.

        ReferenceTypeParameterReplaced: A reference-type parameter is replaced with a
        new instance (new ...) but not returned. The caller's reference still points to
        the original object.

        Parameters with 'ref', 'out', or 'in' modifiers are excluded. No auto-fix —
        these require developer judgement about intent.
        """)]
    public async Task<List<AntiPatternFinding>> FindValueTypeMutationIntent(
        string? filePath = null,
        string? projectName = null)
        => await _antiPatternEngine.FindValueTypeMutationIntentAsync(filePath, projectName);

    [McpServerTool]
    [Description("""
        Generates path-driven xUnit/NUnit/MSTest test stubs for a method by walking
        its AST for decision points (if/else, switch, foreach) and emitting one test
        case per distinct execution path. Always includes a happy-path test first.

        For each path the report includes:
        - TestMethodName: suggested test method name following the MethodName_Condition_Outcome pattern
        - ScenarioDescription: human-readable description of the path
        - InputConstraints: inferred parameter values to trigger this path
        - ArrangeCode / ActCode / AssertCode: Arrange/Act/Assert scaffold with TODO markers
        - Note: caveats about heuristic inference

        Generated code is a starting point — TODO markers show where you must supply
        real values or mock setups that cannot be inferred statically.

        framework: "NUnit" (default), "xunit", or "mstest"
        disambiguateLine: any line number inside the desired overload when the method is overloaded
        """)]
    public async Task<PathDrivenTestReport> GeneratePathDrivenTests(
        string filePath,
        string methodName,
        string framework = "NUnit",
        int? disambiguateLine = null)
        => await _pathDrivenTestEngine.GeneratePathDrivenTestsAsync(filePath, methodName, framework, disambiguateLine);

    [McpServerTool]
    [Description("""
        Statically analyzes a C# file for potential StackOverflowException causes.

        Detects six distinct categories:
        - DirectRecursion       (Definite)   — method calls itself with no conditional guard
        - OverrideCallsSelf     (Definite)   — override calls own name instead of base.X()
        - PropertySelfRead      (Definite)   — property getter reads itself (missing backing field)
        - PropertySelfWrite     (Definite)   — property setter assigns to itself
        - InheritanceCycle      (Definite/Suspicious) — override → base → virtual dispatch → same override
        - ConditionalRecursion  (Suspicious) — guarded self-call whose base case may be unreachable
        - ArgumentNotDecreasing (Suspicious) — recursive call passes parameter unchanged or growing
        - MutualRecursion       (Suspicious) — A calls B calls ... calls A (up to 5-hop cycles)
        - DeepCallChain         (Informational, opt-in) — >40 static frames deep

        Inheritance cycle detection covers both in-file class hierarchies (syntactic) and
        cross-file base classes (semantic). The most dangerous case — override calls base which
        calls an abstract/virtual that this class overrides, creating a hidden dispatch loop —
        is detected with full CyclePath tracing.

        includeInformational: set true to include DeepCallChain findings (default: false)
        """)]
    public async Task<StackOverflowReport> AnalyzeStackOverflowRisks(
        string filePath,
        bool includeInformational = false)
        => await _stackOverflowEngine.AnalyzeStackOverflowRisksAsync(filePath, includeInformational);
}
