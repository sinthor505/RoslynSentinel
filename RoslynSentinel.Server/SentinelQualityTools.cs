using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

/// <summary>
/// Return type for source-transform tools that compute an updated file but do NOT write to disk.
/// Always pass <see cref="UpdatedSource"/> to <c>apply_proposed_changes</c> to persist the change.
/// </summary>
/// <param name="UpdatedSource">Full updated source content for the file.</param>
/// <param name="WroteToFile">Always <c>false</c> — these tools never write to disk.</param>
/// <param name="WorkspaceUpdated">Always <c>false</c> — these tools never update the in-memory workspace.</param>
/// <param name="FilePath">Echo of the input file path for routing to <c>apply_proposed_changes</c>.</param>
public record SourceTransformResult(
    string UpdatedSource,
    bool WroteToFile,
    bool WorkspaceUpdated,
    string FilePath
);

/// <summary>
/// Return type for <c>apply_cancellation_token_to_file</c>.
/// Reports which methods were modified and whether the file was written to disk.
/// </summary>
/// <param name="ModifiedMethods">Names of methods that received the new CancellationToken parameter.</param>
/// <param name="SkippedMethods">
/// Names of async/Task-returning methods that were skipped for a specific reason
/// (already had CT, abstract, event handler, or not in the requested <c>methodNames</c> filter).
/// Sync methods are silently excluded and do not appear here.
/// </param>
/// <param name="TotalModified">Count of modified methods.</param>
/// <param name="WroteToFile"><c>true</c> if the updated source was written to disk successfully.</param>
/// <param name="WorkspaceInSync">
/// <c>true</c> if the in-memory workspace was successfully updated after writing.
/// If <c>false</c>, call <c>load_solution</c> before running further semantic analysis.
/// </param>
/// <param name="WorkspaceVersion">Monotonically increasing workspace version counter; increments on every successful workspace refresh.</param>
public record ApplyCancellationTokenToFileResult(
    List<string> ModifiedMethods,
    List<string> SkippedMethods,
    int TotalModified,
    bool WroteToFile,
    bool WorkspaceInSync = false,
    int WorkspaceVersion = 0
);

/// <summary>
/// Return type for <c>add_cancellation_token_to_method</c> when called with a known <c>autoStage</c> setting.
/// Exactly one of <see cref="ChangeId"/> or <see cref="Source"/> is non-null.
/// </summary>
/// <param name="ChangeId">
/// Set when <c>autoStage=true</c> (default). Pass to <c>apply_staged_changes</c> to write to disk,
/// or <c>get_staged_changes</c> to preview.
/// </param>
/// <param name="Source">
/// Set when <c>autoStage=false</c>. Contains the full updated source; pass to <c>apply_proposed_changes</c> to save.
/// </param>
public record CancellationTokenResult(
    string? ChangeId,
    SourceTransformResult? Source
);

/// <summary>
/// Return type for <c>get_async_migration_progress</c>.
/// Aggregated async-migration statistics for the solution or a single project.
/// </summary>
/// <param name="TotalAsyncMethods">All async or Task/ValueTask-returning methods found.</param>
/// <param name="WithCancellationToken">Subset that already have a CancellationToken parameter.</param>
/// <param name="WithoutCancellationToken">Subset that are still missing a CancellationToken parameter.</param>
/// <param name="CancellationTokenPct">Percentage of async methods that carry a CancellationToken (0–100).</param>
/// <param name="BridgeWrappers">Methods decorated with [Obsolete] where the message contains "Asyncify-bridge".</param>
/// <param name="PendingObsoleteCallers">Call sites of those bridge wrappers that still need to be migrated.</param>
/// <param name="AsyncVoidEventHandlers">Count of <c>async void</c> methods (informational — signatures are fixed).</param>
public record AsyncMigrationProgressReport(
    int TotalAsyncMethods,
    int WithCancellationToken,
    int WithoutCancellationToken,
    double CancellationTokenPct,
    int BridgeWrappers,
    int PendingObsoleteCallers,
    int AsyncVoidEventHandlers
);

/// <summary>
/// A single method flagged by a scout tool via <c>flag_migration_candidate</c>.
/// </summary>
/// <param name="FilePath">Absolute path of the source file containing the method.</param>
/// <param name="MethodName">The method's identifier (case-sensitive).</param>
/// <param name="ClassName">The class that declares the method.</param>
/// <param name="Pattern">
/// The refactoring pattern the method is earmarked for:
/// <c>"AsyncBridgeCandidate"</c>, <c>"HandlerExtract"</c>, <c>"HandlerToAsync"</c>, <c>"AsyncCallerUplift"</c>, etc.
/// </param>
/// <param name="Score">Eligibility score from the scout tool (0 = unscored).</param>
/// <param name="Reason">Human-readable rationale for the flag, or <c>null</c> if none was supplied.</param>
/// <param name="FlaggedDate">ISO date string (yyyy-MM-dd) when the method was flagged, or <c>null</c>.</param>
/// <param name="Line">1-based source line of the method declaration.</param>
public record MigrationCandidateFinding(
    string FilePath,
    string MethodName,
    string ClassName,
    string Pattern,
    int Score,
    string? Reason,
    string? FlaggedDate,
    int Line,
    string ProjectName = ""
)
{
    /// <summary>
    /// Human-readable one-liner combining the most useful fields.
    /// Example: <c>"AsyncBridgeCandidate (score=70): Calls search/isExistedInDB — updateSettlement"</c>.
    /// </summary>
    public string Summary =>
        $"{Pattern} (score={Score})" +
        (string.IsNullOrWhiteSpace(Reason) ? "" : $": {Reason}") +
        $" — {MethodName}";
}

/// <summary>
/// Slim return type for <c>flag_migration_candidate</c>.
/// The attribute has already been written to disk before this result is returned.
/// </summary>
/// <param name="FilePath">Absolute path of the file that was modified.</param>
/// <param name="MethodName">Name of the method that received the <c>[MigrationCandidate]</c> attribute.</param>
/// <param name="Pattern">Migration pattern string that was applied (e.g. <c>"AsyncBridgeCandidate"</c>).</param>
/// <param name="Line">1-based source line of the method declaration after rewriting.</param>
/// <param name="WasAlreadyFlagged">
/// <c>true</c> if the method already carried a <c>[MigrationCandidate]</c> attribute with this exact
/// pattern — the attribute was replaced (idempotent update); <c>false</c> for a fresh flag.
/// </param>
/// <param name="PreviousPattern">
/// The pattern string from a pre-existing <c>[MigrationCandidate]</c> attribute, or <c>null</c>
/// if the method was not previously flagged.
/// </param>
/// <param name="AttributeClassInjected">
/// <c>true</c> if a new <c>MigrationCandidateAttribute.cs</c> file was generated alongside the
/// modification; <c>false</c> if the attribute class was already present in the solution.
/// </param>
/// <param name="Summary">Human-readable summary of the flag action for log display.</param>
public record FlagMigrationCandidateResult(
    string FilePath,
    string MethodName,
    string Pattern,
    int Line,
    bool WasAlreadyFlagged,
    string? PreviousPattern,
    bool AttributeClassInjected,
    string Summary
);

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
    private readonly AsyncBatchEngine _asyncBatchEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly PathDrivenTestEngine _pathDrivenTestEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelQualityTools> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

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
        AsyncBatchEngine asyncBatchEngine,
        PersistentWorkspaceManager workspaceManager,
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
        _asyncBatchEngine = asyncBatchEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Returns execution paths to cover and test methods that exercise a production method. Finds covering tests by name convention (test method name contains production method name) and by direct call-site presence. Returns BranchesToTest, CoveringTests (test file, method, line), and HasAnyCoverage flag.")]
    public async Task<ToolResult<object>> GetTestCoverageMap(string filePath, string methodName)
    {
        try
        {
            var result = await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTestCoverageMap failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetTestCoverageMap failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Returns [MigrationCandidate]-attributed methods added by flag_migration_candidate. Syntax-level — no compilation needed. pattern: call describe_advanced_tool_options("scan_migration_candidates") for valid values. summarize=true → guaranteed ≤2KB dashboard (byClass capped at 10, TopCandidates capped at 5 regardless of topN; ByClassTruncated=true when truncated). summarize=false + limit/offset → full paged candidate records. minScore filters in both modes; TotalRecords reflects post-filter count. A method flagged for two patterns appears twice. When results exceed the inline threshold, LargeResultInfo is populated instead of Data — call get_scan_result(changeId) to read in pages.
        """)]
    public async Task<ToolResult<object>> ScanMigrationCandidates(
        string? filePath = null,
        string? projectName = null,
        string? pattern = null,
        bool summarize = false,
        int? topN = null,
        int? minScore = null,
        int limit = 50,
        int offset = 0)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        // ── summarize=true path: always inline, never touches threshold ───
        if (summarize)
        {
            List<MigrationCandidateFinding> summaryFindings;
            try
            {
                summaryFindings = await _asyncOptimizationEngine
                    .FindMigrationCandidatesAsync(filePath, projectName, pattern);
            }
            catch (ArgumentException ex)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(MigrationErrorCode.InvalidArgument, ex.Message)
                };
            }
            catch (Exception ex)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(MigrationErrorCode.Exception,
                                  "An unexpected error occurred.", ex.Message)
                };
            }

            // B7: apply minScore before aggregation — TotalCandidates reflects post-filter count
            var aggregateFindings = minScore.HasValue
                ? summaryFindings.Where(f => f.Score >= minScore.Value).ToList()
                : summaryFindings;

            var buckets = new Dictionary<string, int>
            {
                ["<0"] = 0,
                ["0-25"] = 0,
                ["26-50"] = 0,
                ["51-75"] = 0,
                ["76plus"] = 0,
            };
            foreach (var f in aggregateFindings)
            {
                var key = f.Score < 0 ? "<0"
                        : f.Score <= 25 ? "0-25"
                        : f.Score <= 50 ? "26-50"
                        : f.Score <= 75 ? "51-75"
                        : "76plus";
                buckets[key]++;
            }

            var byPattern = aggregateFindings
                .GroupBy(f => f.Pattern)
                .ToDictionary(g => g.Key, g => g.Count());

            // B1: use slim type (no FilePath) and cap at 10 to keep summary unconditionally inline-safe.
            const int MaxByClass = 10;
            var allByClass = aggregateFindings
                .GroupBy(f => (f.ClassName, f.ProjectName))
                .Select(g => new ClassCandidateSummarySlim(
                    ClassName: g.Key.ClassName,
                    ProjectName: g.Key.ProjectName,
                    Count: g.Count()))
                .OrderByDescending(c => c.Count)
                .ToList();
            bool byClassTruncated = allByClass.Count > MaxByClass;
            var byClass = byClassTruncated ? allByClass.Take(MaxByClass).ToList() : allByClass;

            // B1: TopCandidates — slim type, capped at 5, only when topN or minScore is set.
            const int MaxTopCandidates = 5;
            List<TopCandidateSummaryEntry>? topCandidates = null;
            if (topN.HasValue || minScore.HasValue)
            {
                var effectiveTopN = Math.Min(topN ?? MaxTopCandidates, MaxTopCandidates);
                topCandidates = aggregateFindings
                    .OrderByDescending(f => f.Score)
                    .Take(effectiveTopN)
                    .Select(f =>
                    {
                        var s = f.Summary;
                        return new TopCandidateSummaryEntry(
                            MethodName: f.MethodName,
                            ClassName: f.ClassName,
                            Pattern: f.Pattern,
                            Score: f.Score,
                            Summary: s[..Math.Min(120, s.Length)]);
                    })
                    .ToList();
            }

            var summary = new MigrationScanSummary(
                TotalCandidates: aggregateFindings.Count,
                ByPattern: byPattern,
                ByClass: byClass,
                ByScoreBucket: buckets,
                TopCandidates: topCandidates,
                ByClassTruncated: byClassTruncated);

            // B1 Fix 4: 8 KB overflow safety net — should be unreachable with slim types + caps.
            const int SummaryThresholdBytes = 8 * 1024;
            var summaryJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(summary);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Summary JSON size: {SizeBytes} bytes", summaryJson.Length);
            }

            if (summaryJson.Length > SummaryThresholdBytes)
            {
                if (summaryJson.Length > 8 * 1024)
                {
                    _logger.LogWarning("Summary JSON size {SizeBytes} bytes exceeds expected limits. " +
                                       "This may indicate an issue with the summarization logic or unusually large data. " +
                                       "Consider reviewing the summary generation and applying stricter caps if necessary.",
                                       summaryJson.Length);
                }

                var operationId = Guid.NewGuid().ToString("N");
                var solutionRoot = _workspaceManager.GetSolutionRoot();
                if (!string.IsNullOrEmpty(solutionRoot))
                {
                    var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "operations");
                    Directory.CreateDirectory(dir);
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                    var fp = System.IO.Path.Combine(dir, $"scan_{ts}_{operationId}.json");
                    await File.WriteAllTextAsync(fp,
                        System.Text.Json.JsonSerializer.Serialize(summary, _jsonOptions),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    return new ToolResult<object>
                    {
                        Success = true,
                        LargeResult = new LargeResultInfo(
                            WrittenToFile: true,
                            FilePath: fp,
                            OperationId: operationId,
                            SizeBytes: summaryJson.Length,
                            TotalRecords: aggregateFindings.Count,
                            Message: $"Summary exceeded {SummaryThresholdBytes} bytes ({summaryJson.Length} bytes). " +
                                           $"Use get_scan_result(changeId: \"{operationId}\") to page through results.")
                    };
                }
            }

            return new ToolResult<object>
            {
                Success = true,
                Data = summary
            };
        }

        // ── candidates path: threshold logic follows ──────────────────────
        List<MigrationCandidateFinding> allFindings;
        try
        {
            allFindings = await _asyncOptimizationEngine
                .FindMigrationCandidatesAsync(filePath, projectName, pattern);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.InvalidArgument, ex.Message)
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }

        // ── paginate ──────────────────────────────────────────────────────
        // B7b: apply minScore before pagination — TotalRecords reflects post-filter count
        if (minScore.HasValue)
            allFindings = allFindings.Where(f => f.Score >= minScore.Value).ToList();

        int totalCount = allFindings.Count;
        var page = allFindings.Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < totalCount;

        // ── size threshold: 30 KB (stay below VS Code's inline interception limit) ──
        const int ThresholdBytes = 30 * 1024;
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(page);

        if (jsonBytes.Length > ThresholdBytes)
        {
            var operationId = Guid.NewGuid().ToString("N");
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            string scanFilePath;
            bool written = false;

            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "operations");
                Directory.CreateDirectory(dir);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                scanFilePath = System.IO.Path.Combine(dir, $"scan_{timestamp}_{operationId}.json");
                await File.WriteAllTextAsync(
                    scanFilePath,
                    JsonSerializer.Serialize(page, _jsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                written = true;
            }
            else
            {
                scanFilePath = string.Empty;
            }

            return new ToolResult<object>
            {
                Success = written,
                TotalRecords = totalCount,
                HasMore = hasMore,
                LargeResult = new LargeResultInfo(
                    WrittenToFile: written,
                    FilePath: scanFilePath,
                    OperationId: operationId,
                    SizeBytes: jsonBytes.Length,
                    TotalRecords: totalCount,
                    Message: $"Result written to file ({jsonBytes.Length} bytes, {totalCount} records). " +
                                   $"Use get_scan_result(changeId: \"{operationId}\") to page through results. " +
                                   "Pass limit and offset to control page size (default limit: 50).")
            };
        }

        // ── inline result ─────────────────────────────────────────────────
        return new ToolResult<object>
        {
            Success = true,
            Data = page,
            TotalRecords = totalCount,
            HasMore = hasMore,
        };
    }

    [McpServerTool]
    [Description("Calculates cyclomatic complexity of a method: 1 + one per if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns complexity score and contributing conditionals. Guide: 1–4 = Low, 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High.")]
    public async Task<ToolResult<object>> GetMethodComplexity(string filePath, string methodName)
    {
        try
        {
            var result = await _testingEngine.CalculateComplexityAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodComplexity failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetMethodComplexity failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── get_scan_result ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Pages through a large scan result written to disk when scan_migration_candidates payload exceeded the inline size threshold. Supply either changeId (resolves to .roslynsentinel/operations/scan_*_{changeId}.json) or filePath (must match the scan_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.
        """)]
    public async Task<ToolResult<object>> GetScanResult(
        string? changeId = null,
        string? filePath = null,
        int limit = 50,
        int offset = 0)
    {
        string? resolvedPath = null;
        var solutionRoot = _workspaceManager.GetSolutionRoot();

        if (!string.IsNullOrEmpty(changeId) && !string.IsNullOrEmpty(solutionRoot))
        {
            var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            if (Directory.Exists(dir))
            {
                resolvedPath = Directory
                    .EnumerateFiles(dir, $"scan_*_{changeId}.json")
                    .FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            // Validate: path must be inside the operations directory and match the scan_*.json pattern.
            var fileName = System.IO.Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var opsDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "operations"));
                var candidate = System.IO.Path.GetFullPath(filePath);
                if (candidate.StartsWith(opsDir, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("scan_", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
            }
        }

        if (resolvedPath == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.InvalidArgument,
                              "Scan file not found. Supply a valid changeId or filePath pointing to a scan_*.json file in the operations directory.")
            };
        }

        List<MigrationCandidateFinding> all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath);
            all = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(
                      json,
                      _jsonOptions)
                  ?? new List<MigrationCandidateFinding>();
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "Failed to read scan file.", ex.Message)
            };
        }

        var page = all.Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < all.Count;

        return new ToolResult<object>
        {
            Success = true,
            Data = page,
            TotalRecords = all.Count,
            HasMore = hasMore,
        };
    }

    // ── Phase 8: get_async_migration_progress ─────────────────────────────────

    [McpServerTool]
    [Description("""
        Returns async migration progress statistics for the solution or a single project. Reports: total async Task/ValueTask methods, how many have a CancellationToken parameter (and how many still need one), percentage coverage, Asyncify-bridge wrapper count ([Obsolete("Asyncify-bridge:...")]), bridge call sites pending migration (CS0618), and async void event handlers (informational — their signatures cannot be extended). projectName=null → entire solution.
        """)]
    public async Task<ToolResult<AsyncMigrationProgressReport>> GetAsyncMigrationProgress(
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<AsyncMigrationProgressReport>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        try
        {
            var report = await _antiPatternEngine
                .GetAsyncMigrationProgressAsync(projectName, cancellationToken);
            return new ToolResult<AsyncMigrationProgressReport>
            {
                Success = true,
                Data = report
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<AsyncMigrationProgressReport>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }
    }

    // ── Phase 4 / Phase 7 — async_migrate internal helpers ─────────────────────

    [Description("""
        Batch-first CancellationToken propagation across multiple files. Supersedes
        propagate_cancellation_token_in_method, propagate_cancellation_token_in_file, and
        propagate_cancellation_token_batch. Checks the circuit breaker before executing;
        records batch outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames? }. null MethodNames = all eligible methods.
        input.DryRun   — when true, computes without writing files.
        input.MaxItems — max files to process (default 100).

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-file details.
        Severity="halt" means the circuit breaker opened — call get_breaker_status then reset_breaker.
        """)]
    private async Task<BatchResultSummary> PropagateCancellationToken(
        BatchTargetInput input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var batchInput = new PropagateCtBatchInput
        {
            Targets = input.Targets.Select(t => new PropagateCtFileTarget
            {
                FilePath = t.FilePath,
                MethodNames = t.MethodNames,
            }).ToList(),
            DryRun = input.DryRun,
            MaxFiles = input.MaxItems,
            FlagFailures = true,
        };

        PropagateCtBatchResult result;
        try
        {
            result = await _asyncBatchEngine.PropagateCancellationTokenBatchAsync(batchInput, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PropagateCancellationToken batch unexpected exception");
            throw new InvalidOperationException(
                $"PropagateCancellationToken failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        int succeeded = result.Applied.Count;
        int failed = result.Failed.Count;
        int skipped = result.RemainingFiles;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items = new List<OperationItemRecord>();
        foreach (var a in result.Applied)
        {
            items.Add(new OperationItemRecord
            {
                FilePath = a.FilePath,
                Outcome = a.TotalForwarded > 0 ? "succeeded" : "skipped",
                Reason = a.TotalForwarded == 0 ? "no eligible call sites" : null,
            });
        }
        foreach (var f in result.Failed)
        {
            items.Add(new OperationItemRecord { FilePath = f.FilePath, Outcome = "failed", Reason = f.Reason });
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "propagate_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();
        var failures = result.Failed
            .Take(15)
            .Select(f => new FailureDetail { FilePath = f.FilePath, Reason = f.Reason, Outcome = "failed" })
            .ToList();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            RolledBack = 0,
            Attempted = succeeded + failed + skipped,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first Asyncify-bridge conversion. Supersedes the single-method
        convert_to_async_bridge_single. Applies the bridge transform to each named method
        across the provided targets. Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames } — MethodNames must be specified.
        input.DryRun   — when true, validates without writing files.
        input.MaxItems — max (file×method) items to process (default 100).
        propagateCancellationTokens — when true (default), propagates CT in the new async overload.

        Each method is applied sequentially and written to disk immediately so that later
        methods in the same file see the updated source. Errors on one method do not abort others.
        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-method details.
        """)]
    private async Task<BatchResultSummary> ConvertToAsyncBridge(
        BatchTargetInput input,
        bool propagateCancellationTokens = true,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed = 0;
        int processed = 0;
        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();

        foreach (var target in input.Targets)
        {
            if (target.MethodNames == null || target.MethodNames.Length == 0)
            {
                var fd = new FailureDetail
                {
                    FilePath = target.FilePath,
                    Reason = "MethodNames must be specified for convert_to_async_bridge",
                    Outcome = "failed",
                };
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = fd.Reason });
                if (failures.Count < 15) { failures.Add(fd); }
                failed++;
                continue;
            }

            foreach (var methodName in target.MethodNames)
            {
                if (processed >= input.MaxItems)
                {
                    break;
                }

                processed++;

                if (input.DryRun)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        MethodName = methodName,
                        Outcome = "skipped",
                        Reason = "dry_run",
                    });
                    succeeded++;
                    continue;
                }

                try
                {
                    var updatedSource = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                        target.FilePath, methodName);

                    if (propagateCancellationTokens)
                    {
                        var asyncMethod = methodName + "Async";
                        var (propagated, _) = await _asyncOptimizationEngine
                            .PropagateCancellationTokenInMethodAsync(target.FilePath, asyncMethod, cancellationToken);
                        if (!string.IsNullOrEmpty(propagated))
                        {
                            updatedSource = propagated;
                        }
                    }

                    var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<string, string> { { target.FilePath, updatedSource } });

                    string? beforeSource782 = null;
                    applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource782);

                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        MethodName = methodName,
                        Outcome = "succeeded",
                        BeforeSource = beforeSource782,
                    });
                    succeeded++;
                }
                catch (Exception ex)
                {
                    var reason = ex.Message;
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        MethodName = methodName,
                        Outcome = "failed",
                        Reason = reason,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = target.FilePath,
                            MethodName = methodName,
                            Reason = reason,
                            Outcome = "failed",
                        });
                    }
                    failed++;
                    _logger.LogWarning(
                        "ConvertToAsyncBridge batch: {Method} in {File} failed: {Reason}",
                        methodName, target.FilePath, reason);
                }
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "convert_to_async_bridge", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = 0,
            RolledBack = 0,
            Attempted = succeeded + failed,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first addition of CancellationToken parameters to async methods across multiple
        files. Supersedes add_cancellation_token_to_method and apply_cancellation_token_to_file.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets  — list of { FilePath, MethodNames? }. null MethodNames = all eligible async
                         methods in the file. MethodNames filter = only those specific methods.
        input.DryRun   — when true, computes without writing files.
        input.MaxItems — max files to process (default 100).

        Files are processed independently (no cross-file dependency). Each file's changes are
        collected then applied atomically. Returns BatchResultSummary.
        Use get_operation_detail(changeId) for per-file details.
        """)]
    private async Task<BatchResultSummary> AddCancellationToken(
        BatchTargetInput input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();
        int processed = 0;

        foreach (var target in input.Targets)
        {
            if (processed >= input.MaxItems)
            {
                skipped++;
                continue;
            }

            processed++;

            try
            {
                var (updatedSource, modified, skippedMethods) =
                    await _asyncOptimizationEngine.ApplyCancellationTokenToFileAsync(
                        target.FilePath, target.MethodNames, cancellationToken);

                if (updatedSource.StartsWith("// Error:"))
                {
                    var reason = updatedSource;
                    items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = reason });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = "failed" });
                    }
                    failed++;
                }
                else if (modified.Count == 0)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        Outcome = "skipped",
                        Reason = "no eligible async methods",
                    });
                    skipped++;
                }
                else
                {
                    if (!input.DryRun)
                    {
                        allChanges[target.FilePath] = updatedSource;
                    }

                    foreach (var m in modified)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = target.FilePath,
                            MethodName = m,
                            Outcome = input.DryRun ? "skipped" : "succeeded",
                            Reason = input.DryRun ? "dry_run" : null,
                        });
                    }

                    succeeded++;
                }
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = "failed", Reason = reason });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = "failed" });
                }
                failed++;
                _logger.LogWarning("AddCancellationToken batch: {File} failed: {Reason}", target.FilePath, reason);
            }
        }

        if (allChanges.Count > 0)
        {
            var applyResult941 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
            // Backfill BeforeSource on succeeded records now that PreImages are available.
            if (applyResult941.PreImages != null)
            {
                foreach (var item in items)
                {
                    if (item.Outcome == "succeeded" && item.BeforeSource == null)
                    {
                        applyResult941.PreImages.TryGetValue(item.FilePath, out var pre);
                        item.BeforeSource = pre;
                    }
                }
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "add_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            RolledBack = 0,
            Attempted = succeeded + failed + skipped,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first caller uplift — uplifts callers of Asyncify-bridge sync wrappers to use
        their async overloads directly. Supersedes run_uplift_batch and run_uplift_batch_multi.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Targets                    — list of { BridgedMethodName, ProjectName? }.
        input.DryRun                     — when true, reports without writing files.
        input.MaxCallersPerMethod        — max callers per bridged method (default 10).
        input.PropagateCancellationTokens — when true (default), propagates CT in new async overloads.

        Returns BatchResultSummary where Succeeded = total callers uplifted, Failed = total callers
        skipped (flagged NeedsManualReview). Use get_operation_detail(changeId) for details.
        """)]
    private async Task<BatchResultSummary> RunUplift(
        RunUpliftInput input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var multiInput = new UpliftBatchMultiInput
        {
            Targets = input.Targets.Select(t => new UpliftBatchMultiTarget
            {
                BridgedMethodName = t.BridgedMethodName,
                ProjectName = t.ProjectName,
            }).ToList(),
            MaxCallersPerMethod = input.MaxCallersPerMethod,
            DryRun = input.DryRun,
            PropagateCancellationTokens = input.PropagateCancellationTokens,
        };

        UpliftBatchMultiResult result;
        try
        {
            result = await _asyncBatchEngine.RunUpliftBatchMultiAsync(multiInput, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunUplift batch unexpected exception");
            throw new InvalidOperationException(
                $"RunUplift failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        int succeeded = result.TotalUplifted;
        int failed = result.TotalSkipped;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items = new List<OperationItemRecord>();
        foreach (var pm in result.PerMethod)
        {
            foreach (var u in pm.Result.Uplifted)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = u.FilePath,
                    MethodName = u.CallerMethod,
                    Outcome = "succeeded",
                });
            }
            foreach (var s in pm.Result.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.CallerMethod,
                    Outcome = "failed",
                    Reason = s.Reason,
                });
            }
            if (pm.Error != null)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = pm.BridgedMethodName,
                    Outcome = "failed",
                    Reason = pm.Error,
                });
            }
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "run_uplift", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();
        var failures = result.PerMethod
            .SelectMany(pm => pm.Result.Skipped.Select(s => new FailureDetail
            {
                FilePath = s.FilePath,
                MethodName = s.CallerMethod,
                Reason = s.Reason,
                Outcome = "failed",
            }))
            .Take(15)
            .ToList();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = 0,
            RolledBack = 0,
            Attempted = succeeded + failed,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    [Description("""
        Batch-first migration-candidate flagging. Supersedes flag_migration_candidate (single),
        flag_migration_candidates_batch, and flag_migration_candidates_in_project.
        Checks the circuit breaker; records outcome; writes a forensic blob.

        input.Scope="targets" (default): flags an explicit list of methods.
          input.Targets — list of { FilePath, MethodName, Pattern, Score?, Reason? }.
        input.Scope="project": autonomous scan; scores and flags qualifying methods.
          input.ProjectName — restrict to one project; null = entire solution.
          input.Pattern     — migration pattern to apply (default "AsyncBridgeCandidate").
          input.MinScore    — minimum score to flag (default 50).
          input.DryRun      — when true, reports what would be flagged without writing files.
          input.ForceRescan — when true, ignores existing [MigrationCandidate] attributes.

        Returns BatchResultSummary where Succeeded = methods flagged, Skipped = below-minScore
        or already-flagged, Failed = errors. Use get_operation_detail(changeId) for details.
        """)]
    private async Task<BatchResultSummary> FlagMigrationCandidates(
        FlagCandidatesInput input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();
        var changeId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (input.Scope == "project")
            {
                var engineResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, input.Pattern, input.MinScore, input.DryRun, input.ForceRescan);

                if (!input.DryRun && engineResult.Changes.Count > 0)
                {
                    var applyResult1126 = await _workspaceManager.ApplyProposedChangesAsync(engineResult.Changes);
                    _ = applyResult1126; // PreImages available below via closure; stored for backfill after loop.

                    foreach (var f in engineResult.Flagged)
                    {
                        string? beforeSource1135 = null;
                        applyResult1126.PreImages?.TryGetValue(f.FilePath, out beforeSource1135);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = "succeeded",
                            Reason = null,
                            BeforeSource = beforeSource1135,
                        });
                        succeeded++;
                    }
                }
                else
                {
                    foreach (var f in engineResult.Flagged)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = input.DryRun ? "skipped" : "succeeded",
                            Reason = input.DryRun ? "dry_run" : null,
                        });
                        succeeded++;
                    }
                }
                foreach (var s in engineResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome = "skipped",
                        Reason = $"score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in engineResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome = "skipped",
                        Reason = "already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                // scope="targets" — explicit list
                var targets = input.Targets ?? new List<FlagCandidateTarget>();
                var tuples = targets.Select(t =>
                    (FilePath: t.FilePath, MethodName: t.MethodName,
                     Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                var (results, errors) = await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    var tgt = targets[i];
                    if (r.Line == -1)
                    {
                        continue;
                    }

                    foreach (var kv in r.Changes)
                    {
                        allChanges[kv.Key] = kv.Value;
                    }

                    items.Add(new OperationItemRecord
                    {
                        FilePath = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome = "succeeded",
                    });
                    succeeded++;
                }

                foreach (var (idx, err) in errors)
                {
                    var tgt = targets[idx];
                    items.Add(new OperationItemRecord
                    {
                        FilePath = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome = "failed",
                        Reason = err,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = tgt.FilePath,
                            MethodName = tgt.MethodName,
                            Reason = err,
                            Outcome = "failed",
                        });
                    }
                    failed++;
                }

                if (allChanges.Count > 0 && !input.DryRun)
                {
                    var applyResult1223 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                    // Backfill BeforeSource on succeeded records now that PreImages are available.
                    if (applyResult1223.PreImages != null)
                    {
                        foreach (var item in items)
                        {
                            if (item.Outcome == "succeeded" && item.BeforeSource == null)
                            {
                                applyResult1223.PreImages.TryGetValue(item.FilePath, out var pre);
                                item.BeforeSource = pre;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlagMigrationCandidates batch unexpected exception");
            throw new InvalidOperationException(
                $"FlagMigrationCandidates failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName = await OperationBlobWriter.WriteAsync(
            "flag_migration_candidates", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            RolledBack = 0,
            Attempted = succeeded + failed + skipped,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    // ── Phase 6 — asyncify macro-workflow ─────────────────────────────────────

    [Description("""
        Runs the full Asyncify migration sequence on a project (or explicit method list) in
        a single call. The server owns and executes the fixed sequence — the agent only supplies
        parameters.

        Fixed internal sequence:
          1. Flag   — discovers qualifying sync methods and flags them
                      [MigrationCandidate("AsyncBridgeCandidate")].
                      Skipped when MethodTargets is provided (pre-selected methods).
          2. Bridge — converts flagged methods to the Asyncify-bridge pattern (sync wrapper +
                      async overload). Each async overload optionally gets CT propagated.
          3. Uplift — uplifts callers of each bridge wrapper to use the async overload directly.
          4. Propagate CT — propagates CancellationToken in all files touched by bridge + uplift.
                      Skipped when PropagateCancellationTokens=false.

        Checks the circuit breaker before starting; records total outcome across all phases;
        writes one forensic blob. Returns BatchResultSummary.
        Succeeded = bridges + uplifts. Skipped = below-minScore / remaining candidates.
        Failed = bridge + uplift failures.

        input.ProjectName              — project to process; null = entire solution.
        input.MethodTargets            — explicit (FilePath, MethodName) list; skips flag phase.
        input.Exclusions               — method names to skip in every phase.
        input.DryRun                   — reports without writing files.
        input.PropagateCancellationTokens — run CT propagation after bridge+uplift (default true).
        input.MaxMethods               — max methods in bridge phase (default 50).
        input.MaxCallersPerMethod      — max callers per bridged method in uplift (default 10).
        input.MinScore                 — minimum score to flag in discovery phase (default 50).
        input.ScoreThreshold           — max score eligible for bridge conversion (default 60).

        Use get_operation_detail(changeId) to inspect per-phase, per-method results.
        """)]
    private async Task<BatchResultSummary> Asyncify(
        AsyncifyInput input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();
        int succeeded = 0, failed = 0, skipped = 0;
        var changeId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            // ── Phase 1: Flag ─────────────────────────────────────────────────
            if (input.MethodTargets == null || input.MethodTargets.Count == 0)
            {
                // Autonomous discovery: scan + flag candidates in the target project/solution.
                var flagResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, "AsyncBridgeCandidate", input.MinScore,
                    input.DryRun, forceRescan: false);

                if (!input.DryRun && flagResult.Changes.Count > 0)
                {
                    var applyResult1317 = await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);

                    foreach (var f in flagResult.Flagged)
                    {
                        if (input.Exclusions?.Contains(f.MethodName) == true)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = f.FilePath,
                                MethodName = f.MethodName,
                                Outcome = "skipped",
                                Reason = "excluded",
                            });
                            skipped++;
                            continue;
                        }

                        string? beforeSource1339 = null;
                        applyResult1317.PreImages?.TryGetValue(f.FilePath, out beforeSource1339);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = "succeeded",
                            Reason = "phase:flag",
                            BeforeSource = beforeSource1339,
                        });
                        succeeded++;
                    }
                }
                else
                {
                    foreach (var f in flagResult.Flagged)
                    {
                        if (input.Exclusions?.Contains(f.MethodName) == true)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = f.FilePath,
                                MethodName = f.MethodName,
                                Outcome = "skipped",
                                Reason = "excluded",
                            });
                            skipped++;
                            continue;
                        }

                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = "skipped",
                            Reason = "dry_run:flag",
                        });
                        skipped++;
                    }
                }
                foreach (var s in flagResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome = "skipped",
                        Reason = $"phase:flag — score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in flagResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome = "skipped",
                        Reason = "phase:flag — already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                // Explicit targets: flag just the named methods.
                var tuples = input.MethodTargets
                    .Where(t => input.Exclusions?.Contains(t.MethodName) != true)
                    .Select(t => (FilePath: t.FilePath, MethodName: t.MethodName,
                                  Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                if (tuples.Count > 0)
                {
                    var (flagResults, flagErrors) =
                        await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                    var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < flagResults.Count; i++)
                    {
                        var r = flagResults[i];
                        if (r.Line == -1) { continue; }

                        foreach (var kv in r.Changes) { allChanges[kv.Key] = kv.Value; }
                        items.Add(new OperationItemRecord
                        {
                            FilePath = tuples[i].FilePath,
                            MethodName = tuples[i].MethodName,
                            Outcome = "succeeded",
                            Reason = "phase:flag",
                        });
                        succeeded++;
                    }
                    foreach (var (idx, err) in flagErrors)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = tuples[idx].FilePath,
                            MethodName = tuples[idx].MethodName,
                            Outcome = "failed",
                            Reason = $"phase:flag — {err}",
                        });
                        if (failures.Count < 15)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = tuples[idx].FilePath,
                                MethodName = tuples[idx].MethodName,
                                Reason = err,
                                Outcome = "failed",
                            });
                        }
                        failed++;
                    }
                    if (!input.DryRun && allChanges.Count > 0)
                    {
                        var applyResult1421 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                        // Backfill BeforeSource on succeeded records now that PreImages are available.
                        if (applyResult1421.PreImages != null)
                        {
                            foreach (var item in items)
                            {
                                if (item.Outcome == "succeeded" && item.BeforeSource == null)
                                {
                                    applyResult1421.PreImages.TryGetValue(item.FilePath, out var pre);
                                    item.BeforeSource = pre;
                                }
                            }
                        }
                    }
                }
            }

            // ── Phase 2: Bridge ───────────────────────────────────────────────
            var bridgeResult = await _asyncBatchEngine.RunBridgeBatchAsync(
                input.ProjectName,
                input.MaxMethods,
                input.ScoreThreshold,
                input.DryRun,
                input.PropagateCancellationTokens,
                cancellationToken);

            foreach (var a in bridgeResult.Applied)
            {
                if (input.Exclusions?.Contains(a.MethodName) == true) { continue; }
                items.Add(new OperationItemRecord
                {
                    FilePath = a.FilePath,
                    MethodName = a.MethodName,
                    Outcome = "succeeded",
                    Reason = "phase:bridge",
                });
                succeeded++;
            }
            foreach (var s in bridgeResult.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome = "failed",
                    Reason = $"phase:bridge — {s.Reason}",
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = s.FilePath,
                        MethodName = s.MethodName,
                        Reason = s.Reason,
                        Outcome = "failed",
                    });
                }
                failed++;
            }
            if (bridgeResult.RemainingCandidates > 0)
            {
                skipped += bridgeResult.RemainingCandidates;
            }

            // ── Phase 3: Uplift ───────────────────────────────────────────────
            if (bridgeResult.Applied.Count > 0)
            {
                var upliftTargets = bridgeResult.Applied
                    .Where(a => input.Exclusions?.Contains(a.MethodName) != true)
                    .Select(a => new UpliftBatchMultiTarget
                    {
                        BridgedMethodName = a.MethodName,
                        ProjectName = input.ProjectName,
                    })
                    .ToList();

                if (upliftTargets.Count > 0)
                {
                    var upliftResult = await _asyncBatchEngine.RunUpliftBatchMultiAsync(
                        new UpliftBatchMultiInput
                        {
                            Targets = upliftTargets,
                            MaxCallersPerMethod = input.MaxCallersPerMethod,
                            DryRun = input.DryRun,
                            PropagateCancellationTokens = input.PropagateCancellationTokens,
                        },
                        cancellationToken);

                    foreach (var pm in upliftResult.PerMethod)
                    {
                        foreach (var u in pm.Result.Uplifted)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = u.FilePath,
                                MethodName = u.CallerMethod,
                                Outcome = "succeeded",
                                Reason = "phase:uplift",
                            });
                            succeeded++;
                        }
                        foreach (var s in pm.Result.Skipped)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = s.FilePath,
                                MethodName = s.CallerMethod,
                                Outcome = "failed",
                                Reason = $"phase:uplift — {s.Reason}",
                            });
                            if (failures.Count < 15)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath = s.FilePath,
                                    MethodName = s.CallerMethod,
                                    Reason = s.Reason,
                                    Outcome = "failed",
                                });
                            }
                            failed++;
                        }
                    }
                }
            }

            // ── Phase 4: Propagate CT ─────────────────────────────────────────
            if (input.PropagateCancellationTokens && !input.DryRun)
            {
                var bridgedFiles = bridgeResult.Applied
                    .Select(a => a.FilePath)
                    .Distinct()
                    .ToList();

                if (bridgedFiles.Count > 0)
                {
                    var ctResult = await _asyncBatchEngine.PropagateCancellationTokenBatchAsync(
                        new PropagateCtBatchInput
                        {
                            Targets = bridgedFiles.Select(fp => new PropagateCtFileTarget
                            {
                                FilePath = fp,
                                MethodNames = null
                            }).ToList(),
                            DryRun = false,
                            MaxFiles = bridgedFiles.Count,
                            FlagFailures = false,
                        },
                        cancellationToken);

                    foreach (var a in ctResult.Applied)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = a.FilePath,
                            Outcome = "succeeded",
                            Reason = $"phase:propagate_ct — {a.TotalForwarded} call sites forwarded",
                        });
                    }
                    foreach (var f in ctResult.Failed)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            Outcome = "failed",
                            Reason = $"phase:propagate_ct — {f.Reason}",
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Asyncify unexpected exception");
            throw new InvalidOperationException(
                $"Asyncify failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName2 = await OperationBlobWriter.WriteAsync(
            "asyncify", changeId, items, _workspaceManager.GetSolutionRoot());
        var status2 = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName2,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            RolledBack = 0,
            Attempted = succeeded + failed + skipped,
            Failures = failures,
            Severity = status2.Severity,
            Directive = status2.Directive,
            BreakerOpen = status2.Open,
        };
    }

    // ── Phase 7 — async_migrate dispatcher ────────────────────────────────────

    [McpServerTool]
    [Description("""
        Unified dispatcher for six async-migration operations. All operations check the circuit breaker first and return BatchResultSummary. operation: call describe_advanced_tool_options("async_migrate") for valid values and required input fields per operation. Use get_operation_detail(changeId) for per-item details. Severity="halt" → circuit breaker opened; call get_breaker_status then reset_breaker. ErrorCode="SolutionNotLoaded" → call load_solution first. ErrorCode="InvalidArgument" → unknown operation name.
        """)]
    public async Task<ToolResult<BatchResultSummary>> AsyncMigrate(
        string operation,
        AsyncMigrateInput input,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        BatchResultSummary result;
        try
        {
            result = operation switch
            {
                "propagate_cancellation_token" => await PropagateCancellationToken(
                    new BatchTargetInput
                    {
                        Targets = input.Targets ?? [],
                        DryRun = input.DryRun,
                        MaxItems = input.MaxItems,
                    },
                    cancellationToken),

                "convert_to_async_bridge" => await ConvertToAsyncBridge(
                    new BatchTargetInput
                    {
                        Targets = input.Targets ?? [],
                        DryRun = input.DryRun,
                        MaxItems = input.MaxItems,
                    },
                    input.PropagateCancellationTokens,
                    cancellationToken),

                "add_cancellation_token" => await AddCancellationToken(
                    new BatchTargetInput
                    {
                        Targets = input.Targets ?? [],
                        DryRun = input.DryRun,
                        MaxItems = input.MaxItems,
                    },
                    cancellationToken),

                "run_uplift" => await RunUplift(
                    new RunUpliftInput
                    {
                        Targets = input.UpliftTargets ?? [],
                        DryRun = input.DryRun,
                        MaxCallersPerMethod = input.MaxCallersPerMethod,
                        PropagateCancellationTokens = input.PropagateCancellationTokens,
                    },
                    cancellationToken),

                "flag_migration_candidates" => await FlagMigrationCandidates(
                    new FlagCandidatesInput
                    {
                        Scope = input.FlagScope,
                        Targets = input.FlagTargets,
                        ProjectName = input.ProjectName,
                        Pattern = input.Pattern,
                        MinScore = input.MinScore,
                        DryRun = input.DryRun,
                        ForceRescan = input.ForceRescan,
                    },
                    cancellationToken),

                "asyncify" => await Asyncify(
                    new AsyncifyInput
                    {
                        ProjectName = input.ProjectName,
                        MethodTargets = input.MethodTargets,
                        Exclusions = input.Exclusions,
                        DryRun = input.DryRun,
                        PropagateCancellationTokens = input.PropagateCancellationTokens,
                        MaxMethods = input.MaxMethods,
                        MaxCallersPerMethod = input.MaxCallersPerMethod,
                        MinScore = input.MinScore,
                        ScoreThreshold = input.ScoreThreshold,
                    },
                    cancellationToken),

                _ => throw new ArgumentException(
                    $"Unknown operation '{operation}'. Valid: propagate_cancellation_token, " +
                    "convert_to_async_bridge, add_cancellation_token, run_uplift, " +
                    "flag_migration_candidates, asyncify.",
                    nameof(operation))
            };
        }
        catch (ArgumentException ex)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.InvalidArgument, ex.Message)
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }

        return new ToolResult<BatchResultSummary>
        {
            Success = true,
            Data = result
        };
    }

    // ── describe_advanced_tool_options ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Returns reference documentation for a named tool's valid input values — operation names, transform/kind/detector catalogues, and parameter defaults. Only covers tools whose valid values cannot be inferred from the schema alone. Covered tools: async_migrate, scan, scan_migration_candidates, apply_file_codemod, apply_method_codemod, apply_class_codemod, generate, convert_switch_to_pattern_safe, analyze_switch_for_pattern_conversion, analyze_foreach_for_linq_conversion. Returns ErrorCode="NoFurtherDocumentation" if the tool is not in the covered set — this does not mean the tool is invalid, only that its schema is self-describing.
        """)]
    public ToolOptionsResult DescribeAdvancedToolOptions(string toolName)
    {
        return toolName switch
        {
            "async_migrate" => AsyncMigrateOptions(),
            "scan" => ScanOptions(),
            "scan_migration_candidates" => ScanMigrationCandidatesOptions(),
            "apply_file_codemod" => ApplyFileCodemodOptions(),
            "apply_method_codemod" => ApplyMethodCodemodOptions(),
            "apply_class_codemod" => ApplyClassCodemodOptions(),
            "generate" => GenerateOptions(),
            "convert_switch_to_pattern_safe" => ConvertSwitchOptions(),
            "analyze_switch_for_pattern_conversion" => AnalyzeSwitchOptions(),
            "analyze_foreach_for_linq_conversion" => AnalyzeForeachOptions(),
            _ => new ToolOptionsResult
            {
                Description = $"'{toolName}' is not in the describe_advanced_tool_options covered set. " +
                               "This does not mean the tool is invalid — its parameter schema fully " +
                               "describes its inputs. Covered tools: async_migrate, scan, " +
                               "scan_migration_candidates, apply_file_codemod, apply_method_codemod, " +
                               "apply_class_codemod, generate, convert_switch_to_pattern_safe, " +
                               "analyze_switch_for_pattern_conversion, analyze_foreach_for_linq_conversion.",
                Error = new ResultError(
                    "NoFurtherDocumentation",
                    $"'{toolName}' has no registered options table. See Description for the covered tool list.")
            }
        };
    }

    private static ToolOptionsResult AsyncMigrateOptions() => new()
    {
        Description = """
            async_migrate — operation values and required input fields:

              "propagate_cancellation_token"
                  input.Targets         — list of { FilePath, MethodNames? }
                  input.DryRun          — optional, default false
                  input.MaxItems        — optional, default 100

              "convert_to_async_bridge"
                  input.Targets         — list of { FilePath, MethodNames } (MethodNames required)
                  input.DryRun          — optional, default false
                  input.PropagateCancellationTokens — optional, default true

              "add_cancellation_token"
                  input.Targets         — list of { FilePath, MethodNames? }
                  input.DryRun          — optional, default false
                  input.MaxItems        — optional, default 100

              "run_uplift"
                  input.UpliftTargets   — list of { BridgedMethodName, ProjectName? }
                  input.DryRun          — optional, default false
                  input.MaxCallersPerMethod       — optional, default 10
                  input.PropagateCancellationTokens — optional, default true

              "flag_migration_candidates"
                  input.FlagScope       — "targets" (default) or "project"
                  input.FlagTargets     — list of { FilePath, MethodName, Pattern, Score?, Reason? } (scope=targets)
                  input.ProjectName     — project name (scope=project); null = entire solution
                  input.Pattern         — optional, default "AsyncBridgeCandidate"
                  input.MinScore        — optional, default 50
                  input.DryRun          — optional, default false
                  input.ForceRescan     — optional, default false

              "asyncify"
                  input.ProjectName     — project; null = entire solution
                  input.MethodTargets   — explicit method targets (skips discovery phase)
                  input.Exclusions      — method names to skip
                  input.DryRun          — optional, default false
                  input.PropagateCancellationTokens — optional, default true
                  input.MaxMethods      — optional, default 50
                  input.MaxCallersPerMethod       — optional, default 10
                  input.MinScore        — optional, default 50
                  input.ScoreThreshold  — optional, default 60
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["propagate_cancellation_token"] = new { Targets = "list of {FilePath, MethodNames?}", DryRun = false, MaxItems = 100 },
            ["convert_to_async_bridge"] = new { Targets = "list of {FilePath, MethodNames}", DryRun = false, PropagateCancellationTokens = true },
            ["add_cancellation_token"] = new { Targets = "list of {FilePath, MethodNames?}", DryRun = false, MaxItems = 100 },
            ["run_uplift"] = new { UpliftTargets = "list of {BridgedMethodName, ProjectName?}", DryRun = false, MaxCallersPerMethod = 10, PropagateCancellationTokens = true },
            ["flag_migration_candidates"] = new { FlagScope = "targets|project", DryRun = false, MinScore = 50, ForceRescan = false },
            ["asyncify"] = new { DryRun = false, MaxMethods = 50, MaxCallersPerMethod = 10, MinScore = 50, ScoreThreshold = 60 },
        }
    };

    private static ToolOptionsResult ScanMigrationCandidatesOptions() => new()
    {
        Description = """
            scan_migration_candidates — valid pattern values:
              AsyncBridgeCandidate   — method is a sync wrapper suitable for async-bridge conversion
              HandlerExtract         — method body can be extracted into a separate handler class
              HandlerToAsync         — handler method that should be made async
              AsyncCallerUplift      — sync caller of an already-bridged async method

            MigrationScanSummary fields (when summarize=true):
              ByPattern        — candidate count keyed by pattern name.
              ByClass          — List<ClassCandidateSummarySlim> sorted descending by Count, capped at 10.
                                 Each entry has ClassName, ProjectName (.csproj name), Count.
                                 ByClassTruncated=true when more than 10 classes were found.
              ByScoreBucket    — counts in "<0", "0-25", "26-50", "51-75", "76plus" buckets.
                                 The '<0' bucket is valid but rare; it applies to methods flagged
                                 manually with a negative score (auto-flagging discards sub-zero
                                 results before writing the attribute).
              TopCandidates    — List<TopCandidateSummaryEntry> populated when topN or minScore is
                                 set; null otherwise. Each entry has MethodName, ClassName, Pattern,
                                 Score, Summary (truncated to 120 chars). Capped at 5 entries.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["patterns"] = new[] { "AsyncBridgeCandidate", "HandlerExtract", "HandlerToAsync", "AsyncCallerUplift" },
            ["scoreBuckets"] = new[] { "<0", "0-25", "26-50", "51-75", "76plus" },
        }
    };

    private static ToolOptionsResult ApplyFileCodemodOptions() => new()
    {
        Description = """
            apply_file_codemod — valid transform values:
              add_braces                        Adds braces to all brace-less control statements.
              cleanup_implicit_spans            Removes redundant implicit Span<T>→Span<byte> casts.
              convert_to_null_coalescing        Replaces null-conditional chains with ?? operators.
              convert_to_pattern                Converts is/as type-check+cast pairs to pattern matching.
              convert_to_switch                 Converts if-else chains to switch expressions.
              fix_mismatched_namespaces         Corrects namespace declarations to match folder structure.
              fix_thread_sleep                  Replaces Thread.Sleep with await Task.Delay in async methods.
              format_document_preview           Returns a FormatPreviewResult diff without writing.
              format_document_safe              Formats the document. preview=false writes to disk; preview=true returns content only.
              generate_xml_documentation_stubs  Generates XML doc stubs for all undocumented public methods.
              optimize_task_wait                Converts blocking Task.Wait/Result to async/await.
              preview_add_missing_usings        Returns AddUsingsPreview listing missing usings (read-only).
              add_configure_await_false         Adds .ConfigureAwait(false) to all awaits. libraryMode=true (default).
                                                Returns SourceTransformResult.
              remove_configure_await_false      Removes all .ConfigureAwait(x) calls. Returns SourceTransformResult.
              simplify_boolean_expressions      Simplifies redundant boolean expressions (x == true → x).
              simplify_member_access            Removes unnecessary this./base. qualifiers.
              simplify_verbosity                Removes redundant type names and default parameter values.
              sort_and_deduplicate_usings       Sorts and deduplicates using directives. preview=false writes to disk.
                                                Returns UsingsCleanupResult.
              upgrade_pattern_matching          Upgrades is/as casts to C# pattern-matching syntax.
              upgrade_thread_safety             Fixes dangerous double-checked locking patterns.
              upgrade_to_file_scoped_namespace  Converts block-scoped namespace to file-scoped.
              upgrade_to_modern_guards          Converts null-check guards to ArgumentNullException.ThrowIfNull.
              use_field_backed_properties       Converts auto-properties with backing fields to field-backed (C# 13).
              use_index_from_end                Converts array[array.Length - N] to array[^N].
              use_time_provider                 Replaces DateTime.Now/UtcNow with ITimeProvider calls.

            Additional parameters:
              libraryMode: for add_configure_await_false — true (default) adds .ConfigureAwait(false) to all awaits.
              preview: for format_document_safe and sort_and_deduplicate_usings — false (default) writes to disk.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_braces", "cleanup_implicit_spans", "convert_to_null_coalescing", "convert_to_pattern",
                "convert_to_switch", "fix_mismatched_namespaces", "fix_thread_sleep", "format_document_preview",
                "format_document_safe", "generate_xml_documentation_stubs", "optimize_task_wait",
                "preview_add_missing_usings", "add_configure_await_false", "remove_configure_await_false",
                "simplify_boolean_expressions", "simplify_member_access", "simplify_verbosity",
                "sort_and_deduplicate_usings", "upgrade_pattern_matching", "upgrade_thread_safety",
                "upgrade_to_file_scoped_namespace", "upgrade_to_modern_guards", "use_field_backed_properties",
                "use_index_from_end", "use_time_provider"
            }
        }
    };

    private static ToolOptionsResult ApplyMethodCodemodOptions() => new()
    {
        Description = """
            apply_method_codemod — valid transform values:
              add_guard_clauses              Adds ArgumentNullException.ThrowIfNull guards for reference params.
                                             Returns SourceTransformResult.
              convert_expression_body        Converts between block body and expression body.
                                             direction: "ToExpression" or "ToBlock".
                                             contextSnippet/lineBefore/lineAfter to disambiguate.
              convert_lock_to_semaphore_slim Converts lock statements to async SemaphoreSlim pattern.
                                             Returns SourceTransformResult.
              convert_method_to_indexer      Converts a single-parameter get/set method pair to an indexer.
              convert_out_params_to_value_tuple  Converts out-parameter methods to ValueTuple returns.
                                             Returns OutParamConversionResult.
              convert_static_to_extension    Converts a static method to an extension method.
              convert_switch_to_expression   Converts a switch statement to a switch expression.
              convert_to_async_enumerable    Converts a Task<List<T>>-returning method to IAsyncEnumerable<T>.
                                             Returns SourceTransformResult.
              extension_to_static            Converts an extension method back to a static method.
              generate_async_overload        Generates an async overload of a synchronous method via Task.Run.
              make_method_static             Removes implicit instance state and makes the method static.
              make_method_thread_safe        Adds a lock field and wraps the method body in a lock statement.
                                             lockFieldName: name for the lock object (default "_lock").
                                             Returns SourceTransformResult.
              optimize_independent_awaits    Batches sequential independent awaits into Task.WhenAll.
              optimize_to_value_task         Converts Task/Task<T> return type to ValueTask/ValueTask<T>.
              reduce_block_depth             Inverts conditions and uses early returns to reduce nesting depth.
              update_xml_docs_from_signature Regenerates XML <param> and <returns> tags from the method signature.
              use_exception_expressions      Replaces throw new ArgumentNullException(nameof(x)) with
                                             ArgumentNullException.ThrowIfNull(x), etc.

            Additional parameters:
              direction: required for convert_expression_body — "ToExpression" or "ToBlock".
              contextSnippet/lineBefore/lineAfter: for convert_expression_body disambiguation.
              lockFieldName: for make_method_thread_safe — name for the lock field (default "_lock").
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_guard_clauses", "convert_expression_body", "convert_lock_to_semaphore_slim",
                "convert_method_to_indexer", "convert_out_params_to_value_tuple", "convert_static_to_extension",
                "convert_switch_to_expression", "convert_to_async_enumerable", "extension_to_static",
                "generate_async_overload", "make_method_static", "make_method_thread_safe",
                "optimize_independent_awaits", "optimize_to_value_task", "reduce_block_depth",
                "update_xml_docs_from_signature", "use_exception_expressions"
            }
        }
    };

    private static ToolOptionsResult ApplyClassCodemodOptions() => new()
    {
        Description = """
            apply_class_codemod — valid transform values:
              add_validation_to_poco          Adds [Required] and [StringLength(100)] to all string properties.
              class_to_record                 Converts a class to a record type.
              convert_abstract_to_interface   Converts an abstract class to an interface.
              convert_property_safe           Converts a property between auto-property and full property.
                                              propertyName: the property to convert.
                                              direction: "ToFullProperty" or "ToAutoProperty".
                                              contextSnippet/lineBefore/lineAfter to disambiguate.
              convert_property_to_methods     Converts a property to a getter/setter method pair.
                                              propertyName: pass the property name via className or propertyName.
              convert_to_background_service   Adds BackgroundService base class and generates ExecuteAsync override.
              convert_to_source_generated_logging  Converts ILogger calls to source-generated logging.
              document_poco_fields            Adds [Description] XML comments to all fields in a POCO class.
              make_class_immutable            Converts mutable properties to init-only and adds a With method.
              record_to_class                 Converts a record type to a class.
              replace_constructor_with_factory  Replaces a constructor with a static factory method.
              sort_members                    Sorts members by convention (fields, ctors, props, methods).
              upgrade_to_primary_constructor  Converts a simple assignment-only constructor to a C# 12 primary constructor.

            Additional parameters:
              propertyName: for convert_property_safe and convert_property_to_methods.
              direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty".
              contextSnippet/lineBefore/lineAfter: for convert_property_safe disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_validation_to_poco", "class_to_record", "convert_abstract_to_interface",
                "convert_property_safe", "convert_property_to_methods", "convert_to_background_service",
                "convert_to_source_generated_logging", "document_poco_fields", "make_class_immutable",
                "record_to_class", "replace_constructor_with_factory", "sort_members",
                "upgrade_to_primary_constructor"
            }
        }
    };

    private static ToolOptionsResult GenerateOptions() => new()
    {
        Description = """
            generate — valid kind values:
              add_benchmark_stub           Adds a BenchmarkDotNet stub class for a method.
                                           Requires filePath, className, methodName.
                                           Returns SourceTransformResult.
              generate_constructor         Generates a constructor from private/readonly fields.
                                           Returns updated file content as a string.
              generate_decorator_class     Generates a Decorator pattern class for an interface.
                                           Pass the interface name as className (filePath not required).
                                           decoratorPrefix: prefix for the decorator class (default "Logging").
                                           projectName: optional project scope.
                                           Returns DecoratorResult.
              generate_equality_overrides  Generates Equals and GetHashCode overrides.
                                           Returns updated file content as a string.
              generate_fluent_builder      Generates a fluent builder class with With{Property}() methods.
                                           Returns FluentBuilderResult.
              generate_path_driven_tests   Generates test stubs for each execution path in a method.
                                           Requires filePath, methodName.
                                           framework: "NUnit" (default), "xunit", or "mstest".
                                           disambiguateLine: line number to resolve overloaded methods.
                                           Returns PathDrivenTestReport.
              generate_repository_interface  Extracts an interface from a class with DI and Moq snippets.
                                           Returns RepositoryInterfaceResult.
              generate_test_scaffold       Generates an xUnit+Moq test scaffold with mock fields and test stubs.
                                           Returns TestScaffoldResult.
              generate_test_skeleton       Generates a test class skeleton with one test stub per public method.
                                           Returns TestSkeletonReport.
              generate_to_string_safe      Generates a ToString() override with correctly escaped interpolated strings.
                                           members: optional comma-separated list of property/field names.
                                           Returns MsAugmentResult.

            Additional parameters:
              filePath: required for all kinds except generate_decorator_class.
              className: target class name; for generate_decorator_class pass the interface name.
              methodName: required for add_benchmark_stub and generate_path_driven_tests.
              members: for generate_to_string_safe — optional comma-separated member list.
              decoratorPrefix: for generate_decorator_class (default "Logging").
              projectName: for generate_decorator_class — optional project scope.
              framework: for generate_path_driven_tests — "NUnit" (default), "xunit", or "mstest".
              disambiguateLine: for generate_path_driven_tests — disambiguates overloaded methods.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["kinds"] = new[] {
                "add_benchmark_stub", "generate_constructor", "generate_decorator_class",
                "generate_equality_overrides", "generate_fluent_builder", "generate_path_driven_tests",
                "generate_repository_interface", "generate_test_scaffold", "generate_test_skeleton",
                "generate_to_string_safe"
            }
        }
    };

    private static ToolOptionsResult ConvertSwitchOptions() => new()
    {
        Description = """
            convert_switch_to_pattern_safe — supported switch forms and rejection rules:

            SUPPORTED forms:
              1. All cases assign to the SAME variable:
                   case "g": factor = 1.0; break;
                   → factor = unit switch { "g" => 1.0, ... };
              2. All cases are return statements:
                   case "g": return 1.0;
                   → return unit switch { "g" => 1.0, ... };
              3. All cases are throw statements (or mixed with return).

            REJECTED (returned as error, not silently dropped):
              • Cases assigning to MULTIPLE different variables per case
              • Cases assigning to different variables across cases
              • Cases with complex multi-statement bodies

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line, e.g. "switch (unit)".
              lineBefore/lineAfter — disambiguate when the snippet matches multiple locations.

            Run analyze_switch_for_pattern_conversion first if you are unsure whether conversion is safe.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["supportedForms"] = new[] { "single-variable assignment", "return statements", "throw statements" },
            ["rejectedForms"] = new[] { "multiple-variable assignment per case", "different variables across cases", "complex multi-statement bodies" }
        }
    };

    private static ToolOptionsResult AnalyzeSwitchOptions() => new()
    {
        Description = """
            analyze_switch_for_pattern_conversion — pre-flight analysis output fields:

            Returns SwitchConversionAnalysis with:
              IsSafeToConvert     — true when the standard tool or convert_switch_to_pattern_safe will produce correct output.
              CaseCount           — total number of cases analysed.
              Cases[]             — per-case detail: CaseLabel, AssignmentCount, VariablesAssigned[], IsSafe, BlockingReason.
              BlockingReason      — human-readable reason why IsSafeToConvert is false (null when safe).
              Recommendation      — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_to_pattern_matching' tool silently drops
            variable assignments in switch cases that assign to more than one variable, producing
            broken code without any warning. This tool detects that condition before conversion.

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line.
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CaseCount", "Cases", "BlockingReason", "Recommendation" }
        }
    };

    private static ToolOptionsResult AnalyzeForeachOptions() => new()
    {
        Description = """
            analyze_foreach_for_linq_conversion — pre-flight analysis output fields:

            Returns ForeachLinqAnalysis with:
              IsSafeToConvert          — true when convert_foreach_linq will produce correct output.
              CollectionVariableName   — the collection variable being built by the foreach.
              StatementsBeforeForeach  — list of statements that modify the collection BEFORE the loop
                                         (these would be discarded by the standard tool if present).
              BlockingReason           — human-readable reason when IsSafeToConvert is false.
              Recommendation           — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_foreach_linq' tool silently destroys data.
            When a collection is modified before the foreach (e.g., results.Add("header")), the
            standard tool re-initialises the variable with 'new List<T>()', discarding those
            pre-loop additions WITHOUT any warning.

            ALWAYS call this before convert_foreach_linq. Only proceed if IsSafeToConvert=true.

            Parameters:
              filePath        — absolute path to the .cs file.
              contextSnippet  — short snippet of the foreach statement (e.g., "foreach (var item in").
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CollectionVariableName", "StatementsBeforeForeach", "BlockingReason", "Recommendation" }
        }
    };

    private static ToolOptionsResult ScanOptions()
    {
        // Single source of truth: derived from SentinelScanTools.s_descriptors.
        // Adding, removing, or reclassifying a detector in s_descriptors automatically
        // propagates here — no manual sync required.
        var byDomain = SentinelScanTools.s_descriptors
            .GroupBy(d => d.Domain)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => d.Id).ToArray());

        int total = SentinelScanTools.s_descriptors.Length;

        var sb = new StringBuilder();
        sb.AppendLine($"scan — valid detector IDs grouped by domain ({total} total):");
        sb.AppendLine();
        foreach (var (domain, ids) in byDomain)
        {
            sb.AppendLine($"{domain} ({ids.Length}):");
            // Wrap ids at ~80 chars, indented two spaces
            const int Indent = 2;
            const int WrapAt = 80;
            var line = new StringBuilder(new string(' ', Indent));
            foreach (var id in ids)
            {
                string candidate = line.Length == Indent ? id : ", " + id;
                if (line.Length + candidate.Length > WrapAt && line.Length > Indent)
                {
                    sb.AppendLine(line.ToString());
                    line.Clear().Append(new string(' ', Indent)).Append(id);
                }
                else
                {
                    line.Append(candidate);
                }
            }
            if (line.Length > Indent)
            {
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine();
        }
        sb.AppendLine("scope values: \"file\" | \"project\" | \"solution\"");
        sb.AppendLine("scopeName: filePath for scope=file; projectName for scope=project; omit for solution.");
        sb.AppendLine("  For duplicate_blocks_in_hierarchy, scopeName is the root type name.");
        sb.AppendLine("File-scope-only detectors require scope=\"file\". unused_references requires scope=\"project\".");
        sb.Append("Call describe_scan_detectors for per-detector scope hints and descriptions.");

        return new ToolOptionsResult
        {
            Description = sb.ToString(),
            StructuredOptions = new Dictionary<string, object>(
                byDomain.Select(kvp =>
                    new KeyValuePair<string, object>(kvp.Key, kvp.Value)))
        };
    }
}
// v2 — ScanOptions() now derived from SentinelScanTools.s_descriptors (single source of truth)