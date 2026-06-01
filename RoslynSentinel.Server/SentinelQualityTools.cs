using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    string  FilePath,
    string  MethodName,
    string  ClassName,
    string  Pattern,
    int     Score,
    string? Reason,
    string? FlaggedDate,
    int     Line,
    string  ProjectName = ""
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
    string  FilePath,
    string  MethodName,
    string  Pattern,
    int     Line,
    bool    WasAlreadyFlagged,
    string? PreviousPattern,
    bool    AttributeClassInjected,
    string  Summary
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
    [Description("Extends path coverage analysis with a cross-reference to test methods that exercise the given production method. Finds covering tests by name convention (test method name contains the production method name) and by direct call-site presence in the test body. Returns BranchesToTest (execution paths to cover) and CoveringTests (test file, test method name, line) with HasAnyCoverage flag.")]
    public async Task<TestCoverageMap> GetTestCoverageMap(string filePath, string methodName)
        => await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);

    [McpServerTool]
    [Description("""
        Returns [MigrationCandidate]-attributed methods added by flag_migration_candidate.
        Uses syntax-level analysis — no compilation needed.

        filePath:    restrict to one file (full or partial path suffix).
        projectName: restrict to one project (case-insensitive).
        pattern:     restrict to one pattern — call describe_tool_options("scan_migration_candidates")
                     for valid pattern values.
        summarize:   when true, return counts only (always inline-safe). Add topN/minScore to include
                     top actionable targets alongside counts.
        topN:        when summarize=true, include this many top-scored candidates in TopCandidates.
        minScore:    when summarize=true, filter TopCandidates to score >= minScore.
        limit/offset: page the full candidate list (summarize=false only).

        Returns MigrationResult<List<MigrationCandidateFinding>> or MigrationResult<MigrationScanSummary>.
        A method flagged for two patterns appears twice. Each finding includes a Summary field.
        When payload exceeds 256 KB the result is written to disk; use get_scan_result to page through it.
        """)]
    public async Task<object> ScanMigrationCandidates(
        string? filePath    = null,
        string? projectName = null,
        string? pattern     = null,
        bool    summarize   = false,
        int     limit       = 50,
        int     offset      = 0,
        int?    topN        = null,
        int?    minScore    = null)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new MigrationResult<object>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        List<MigrationCandidateFinding> allFindings;
        try
        {
            allFindings = await _asyncOptimizationEngine
                .FindMigrationCandidatesAsync(filePath, projectName, pattern);
        }
        catch (ArgumentException ex)
        {
            return new MigrationResult<object>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.InvalidArgument, ex.Message)
            };
        }
        catch (Exception ex)
        {
            return new MigrationResult<object>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }

        // ── summarize=true path ───────────────────────────────────────────
        if (summarize)
        {
            var buckets = new Dictionary<string, int>
            {
                ["<0"]     = 0,
                ["0-25"]   = 0,
                ["26-50"]  = 0,
                ["51-75"]  = 0,
                ["76plus"] = 0,
            };
            foreach (var f in allFindings)
            {
                var key = f.Score < 0  ? "<0"
                        : f.Score <= 25 ? "0-25"
                        : f.Score <= 50 ? "26-50"
                        : f.Score <= 75 ? "51-75"
                        : "76plus";
                buckets[key]++;
            }

            var byPattern = allFindings
                .GroupBy(f => f.Pattern)
                .ToDictionary(g => g.Key, g => g.Count());

            var byClass = allFindings
                .GroupBy(f => (f.ClassName, f.FilePath))
                .Select(g => new ClassCandidateSummary
                {
                    ClassName   = g.Key.ClassName,
                    ProjectName = g.First().ProjectName,
                    FilePath    = g.Key.FilePath,
                    Count       = g.Count()
                })
                .OrderByDescending(c => c.Count)
                .ToList();

            // ── topN / minScore ───────────────────────────────────────────
            List<MigrationCandidateFinding>? topCandidates = null;
            if (topN.HasValue || minScore.HasValue)
            {
                var filtered = allFindings.AsEnumerable();
                if (minScore.HasValue)
                    filtered = filtered.Where(f => f.Score >= minScore.Value);
                filtered = filtered.OrderByDescending(f => f.Score);
                if (topN.HasValue)
                    filtered = filtered.Take(topN.Value);
                topCandidates = filtered.ToList();
            }

            return new MigrationResult<MigrationScanSummary>
            {
                Success = true,
                Data    = new MigrationScanSummary(
                    TotalCandidates: allFindings.Count,
                    ByPattern:       byPattern,
                    ByClass:         byClass,
                    ByScoreBucket:   buckets,
                    TopCandidates:   topCandidates)
            };
        }

        // ── paginate ──────────────────────────────────────────────────────
        int totalCount = allFindings.Count;
        var page = allFindings.Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < totalCount;

        // ── size threshold: 256 KB ────────────────────────────────────────
        const int ThresholdBytes = 256 * 1024;
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
                    JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                written = true;
            }
            else
            {
                scanFilePath = string.Empty;
            }

            return new MigrationResult<List<MigrationCandidateFinding>>
            {
                Success     = written,
                TotalRecords = totalCount,
                HasMore      = hasMore,
                LargeResult  = new LargeResultInfo(
                    WrittenToFile: written,
                    FilePath:      scanFilePath,
                    OperationId:   operationId,
                    SizeBytes:     jsonBytes.Length,
                    TotalRecords:  totalCount)
            };
        }

        // ── inline result ─────────────────────────────────────────────────
        return new MigrationResult<List<MigrationCandidateFinding>>
        {
            Success      = true,
            Data         = page,
            TotalRecords = totalCount,
            HasMore      = hasMore,
        };
    }


    [McpServerTool]
    [Description("Calculates the cyclomatic complexity of a method: 1 + one for each if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns the complexity score and the list of conditionals that contribute to it. Complexity guide: 1–4 = Low (easy to understand and test), 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High (split required). Use before modifying a method to gauge how risky the change is.")]
    public async Task<TestComplexityReport> GetMethodComplexity(string filePath, string methodName)
        => await _testingEngine.CalculateComplexityAsync(filePath, methodName);

    // ── get_scan_result ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Pages through a large scan result that was previously written to disk by
        scan_migration_candidates when the payload exceeded the inline size threshold.

        changeId: the OperationId returned in LargeResult.OperationId — resolves to
                  .roslynsentinel/operations/scan_*_{changeId}.json.
        filePath: full path to the scan file (alternative to changeId).
                  Must match the scan_*.json pattern inside the operations directory.
        limit:    max findings per page (default 50).
        offset:   zero-based page offset (default 0).

        Returns MigrationResult<List<MigrationCandidateFinding>> with TotalRecords and HasMore.
        """)]
    public async Task<MigrationResult<List<MigrationCandidateFinding>>> GetScanResult(
        string? changeId  = null,
        string? filePath  = null,
        int     limit     = 50,
        int     offset    = 0)
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
            return new MigrationResult<List<MigrationCandidateFinding>>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.InvalidArgument,
                              "Scan file not found. Supply a valid changeId or filePath pointing to a scan_*.json file in the operations directory.")
            };
        }

        List<MigrationCandidateFinding> all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath);
            all = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(
                      json,
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? new List<MigrationCandidateFinding>();
        }
        catch (Exception ex)
        {
            return new MigrationResult<List<MigrationCandidateFinding>>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.Exception,
                              "Failed to read scan file.", ex.Message)
            };
        }

        var page   = all.Skip(offset).Take(limit).ToList();
        bool hasMore = (offset + limit) < all.Count;

        return new MigrationResult<List<MigrationCandidateFinding>>
        {
            Success      = true,
            Data         = page,
            TotalRecords = all.Count,
            HasMore      = hasMore,
        };
    }

    // ── Phase 8: get_async_migration_progress ─────────────────────────────────

    [McpServerTool]
    [Description("""
        Returns async migration progress statistics for the solution or a single project.
        Reports: total async Task/ValueTask methods, how many already have a CancellationToken
        parameter (and how many still need one), percentage coverage, number of Asyncify-bridge
        wrapper methods ([Obsolete("Asyncify-bridge:...")]), number of call sites that still
        invoke those bridge wrappers (CS0618 sites pending migration), and count of async void
        event handlers (informational — their signatures cannot be extended).

        projectName: restrict statistics to a single project; null = entire solution.
        """)]
    public async Task<MigrationResult<AsyncMigrationProgressReport>> GetAsyncMigrationProgress(
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new MigrationResult<AsyncMigrationProgressReport>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        try
        {
            var report = await _antiPatternEngine
                .GetAsyncMigrationProgressAsync(projectName, cancellationToken);
            return new MigrationResult<AsyncMigrationProgressReport>
            {
                Success = true,
                Data    = report
            };
        }
        catch (Exception ex)
        {
            return new MigrationResult<AsyncMigrationProgressReport>
            {
                Success = false,
                Error   = new ResultError(MigrationErrorCode.Exception,
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
        BatchTargetInput  input,
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
                FilePath    = t.FilePath,
                MethodNames = t.MethodNames,
            }).ToList(),
            DryRun       = input.DryRun,
            MaxFiles     = input.MaxItems,
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
        int failed    = result.Failed.Count;
        int skipped   = result.RemainingFiles;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items    = new List<OperationItemRecord>();
        foreach (var a in result.Applied)
        {
            items.Add(new OperationItemRecord
            {
                FilePath = a.FilePath,
                Outcome  = a.TotalForwarded > 0 ? "succeeded" : "skipped",
                Reason   = a.TotalForwarded == 0 ? "no eligible call sites" : null,
            });
        }
        foreach (var f in result.Failed)
        {
            items.Add(new OperationItemRecord { FilePath = f.FilePath, Outcome = "failed", Reason = f.Reason });
        }

        var blobName  = await OperationBlobWriter.WriteAsync(
            "propagate_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status    = _workspaceManager.GetBreakerStatus();
        var failures  = result.Failed
            .Take(15)
            .Select(f => new FailureDetail { FilePath = f.FilePath, Reason = f.Reason, Outcome = "failed" })
            .ToList();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
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
        BatchTargetInput  input,
        bool              propagateCancellationTokens = true,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed    = 0;
        int processed = 0;
        var items     = new List<OperationItemRecord>();
        var failures  = new List<FailureDetail>();

        foreach (var target in input.Targets)
        {
            if (target.MethodNames == null || target.MethodNames.Length == 0)
            {
                var fd = new FailureDetail
                {
                    FilePath = target.FilePath,
                    Reason   = "MethodNames must be specified for convert_to_async_bridge",
                    Outcome  = "failed",
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
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "skipped",
                        Reason     = "dry_run",
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

                    await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<string, string> { { target.FilePath, updatedSource } });

                    items.Add(new OperationItemRecord
                    {
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "succeeded",
                    });
                    succeeded++;
                }
                catch (Exception ex)
                {
                    var reason = ex.Message;
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = target.FilePath,
                        MethodName = methodName,
                        Outcome    = "failed",
                        Reason     = reason,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath   = target.FilePath,
                            MethodName = methodName,
                            Reason     = reason,
                            Outcome    = "failed",
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
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = 0,
            RolledBack  = 0,
            Attempted   = succeeded + failed,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
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
        BatchTargetInput  input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int succeeded  = 0;
        int failed     = 0;
        int skipped    = 0;
        var items      = new List<OperationItemRecord>();
        var failures   = new List<FailureDetail>();
        int processed  = 0;

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
                        Outcome  = "skipped",
                        Reason   = "no eligible async methods",
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
                            FilePath   = target.FilePath,
                            MethodName = m,
                            Outcome    = input.DryRun ? "skipped" : "succeeded",
                            Reason     = input.DryRun ? "dry_run" : null,
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
            await _workspaceManager.ApplyProposedChangesAsync(allChanges);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "add_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
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
        RunUpliftInput    input,
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
                ProjectName       = t.ProjectName,
            }).ToList(),
            MaxCallersPerMethod          = input.MaxCallersPerMethod,
            DryRun                       = input.DryRun,
            PropagateCancellationTokens  = input.PropagateCancellationTokens,
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
        int failed    = result.TotalSkipped;

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var items    = new List<OperationItemRecord>();
        foreach (var pm in result.PerMethod)
        {
            foreach (var u in pm.Result.Uplifted)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = u.FilePath,
                    MethodName = u.CallerMethod,
                    Outcome    = "succeeded",
                });
            }
            foreach (var s in pm.Result.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = s.FilePath,
                    MethodName = s.CallerMethod,
                    Outcome    = "failed",
                    Reason     = s.Reason,
                });
            }
            if (pm.Error != null)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = pm.BridgedMethodName,
                    Outcome  = "failed",
                    Reason   = pm.Error,
                });
            }
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "run_uplift", changeId, items, _workspaceManager.GetSolutionRoot());
        var status   = _workspaceManager.GetBreakerStatus();
        var failures = result.PerMethod
            .SelectMany(pm => pm.Result.Skipped.Select(s => new FailureDetail
            {
                FilePath   = s.FilePath,
                MethodName = s.CallerMethod,
                Reason     = s.Reason,
                Outcome    = "failed",
            }))
            .Take(15)
            .ToList();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = 0,
            RolledBack  = 0,
            Attempted   = succeeded + failed,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
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
        CancellationToken   cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed    = 0;
        int skipped   = 0;
        var items     = new List<OperationItemRecord>();
        var failures  = new List<FailureDetail>();
        var changeId  = Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (input.Scope == "project")
            {
                var engineResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, input.Pattern, input.MinScore, input.DryRun, input.ForceRescan);

                if (!input.DryRun && engineResult.Changes.Count > 0)
                {
                    await _workspaceManager.ApplyProposedChangesAsync(engineResult.Changes);
                }

                foreach (var f in engineResult.Flagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = f.FilePath,
                        MethodName = f.MethodName,
                        Outcome    = input.DryRun ? "skipped" : "succeeded",
                        Reason     = input.DryRun ? "dry_run" : null,
                    });
                    succeeded++;
                }
                foreach (var s in engineResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome    = "skipped",
                        Reason     = $"score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in engineResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome    = "skipped",
                        Reason     = "already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                // scope="targets" — explicit list
                var targets = input.Targets ?? new List<FlagCandidateTarget>();
                var tuples  = targets.Select(t =>
                    (FilePath: t.FilePath, MethodName: t.MethodName,
                     Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                var (results, errors) = await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                var allChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < results.Count; i++)
                {
                    var r    = results[i];
                    var tgt  = targets[i];
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
                        FilePath   = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome    = "succeeded",
                    });
                    succeeded++;
                }

                foreach (var (idx, err) in errors)
                {
                    var tgt = targets[idx];
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = tgt.FilePath,
                        MethodName = tgt.MethodName,
                        Outcome    = "failed",
                        Reason     = err,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath   = tgt.FilePath,
                            MethodName = tgt.MethodName,
                            Reason     = err,
                            Outcome    = "failed",
                        });
                    }
                    failed++;
                }

                if (allChanges.Count > 0 && !input.DryRun)
                {
                    await _workspaceManager.ApplyProposedChangesAsync(allChanges);
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
        var status   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status.Severity,
            Directive   = status.Directive,
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
        AsyncifyInput     input,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var items    = new List<OperationItemRecord>();
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
                    await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                }

                foreach (var f in flagResult.Flagged)
                {
                    if (input.Exclusions?.Contains(f.MethodName) == true)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome    = "skipped",
                            Reason     = "excluded",
                        });
                        skipped++;
                        continue;
                    }

                    items.Add(new OperationItemRecord
                    {
                        FilePath   = f.FilePath,
                        MethodName = f.MethodName,
                        Outcome    = input.DryRun ? "skipped" : "succeeded",
                        Reason     = input.DryRun ? "dry_run:flag" : "phase:flag",
                    });
                    if (!input.DryRun) { succeeded++; }
                    else               { skipped++;   }
                }
                foreach (var s in flagResult.Skipped)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Outcome    = "skipped",
                        Reason     = $"phase:flag — score {s.Score} below minScore {input.MinScore}",
                    });
                    skipped++;
                }
                foreach (var a in flagResult.AlreadyFlagged)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath   = a.FilePath,
                        MethodName = a.MethodName,
                        Outcome    = "skipped",
                        Reason     = "phase:flag — already flagged",
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
                            FilePath   = tuples[i].FilePath,
                            MethodName = tuples[i].MethodName,
                            Outcome    = "succeeded",
                            Reason     = "phase:flag",
                        });
                        succeeded++;
                    }
                    foreach (var (idx, err) in flagErrors)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath   = tuples[idx].FilePath,
                            MethodName = tuples[idx].MethodName,
                            Outcome    = "failed",
                            Reason     = $"phase:flag — {err}",
                        });
                        if (failures.Count < 15)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath   = tuples[idx].FilePath,
                                MethodName = tuples[idx].MethodName,
                                Reason     = err,
                                Outcome    = "failed",
                            });
                        }
                        failed++;
                    }
                    if (!input.DryRun && allChanges.Count > 0)
                    {
                        await _workspaceManager.ApplyProposedChangesAsync(allChanges);
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
                    FilePath   = a.FilePath,
                    MethodName = a.MethodName,
                    Outcome    = "succeeded",
                    Reason     = "phase:bridge",
                });
                succeeded++;
            }
            foreach (var s in bridgeResult.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath   = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome    = "failed",
                    Reason     = $"phase:bridge — {s.Reason}",
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath   = s.FilePath,
                        MethodName = s.MethodName,
                        Reason     = s.Reason,
                        Outcome    = "failed",
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
                        ProjectName       = input.ProjectName,
                    })
                    .ToList();

                if (upliftTargets.Count > 0)
                {
                    var upliftResult = await _asyncBatchEngine.RunUpliftBatchMultiAsync(
                        new UpliftBatchMultiInput
                        {
                            Targets                     = upliftTargets,
                            MaxCallersPerMethod         = input.MaxCallersPerMethod,
                            DryRun                      = input.DryRun,
                            PropagateCancellationTokens = input.PropagateCancellationTokens,
                        },
                        cancellationToken);

                    foreach (var pm in upliftResult.PerMethod)
                    {
                        foreach (var u in pm.Result.Uplifted)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath   = u.FilePath,
                                MethodName = u.CallerMethod,
                                Outcome    = "succeeded",
                                Reason     = "phase:uplift",
                            });
                            succeeded++;
                        }
                        foreach (var s in pm.Result.Skipped)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath   = s.FilePath,
                                MethodName = s.CallerMethod,
                                Outcome    = "failed",
                                Reason     = $"phase:uplift — {s.Reason}",
                            });
                            if (failures.Count < 15)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath   = s.FilePath,
                                    MethodName = s.CallerMethod,
                                    Reason     = s.Reason,
                                    Outcome    = "failed",
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
                                { FilePath = fp, MethodNames = null }).ToList(),
                            DryRun       = false,
                            MaxFiles     = bridgedFiles.Count,
                            FlagFailures = false,
                        },
                        cancellationToken);

                    foreach (var a in ctResult.Applied)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = a.FilePath,
                            Outcome  = "succeeded",
                            Reason   = $"phase:propagate_ct — {a.TotalForwarded} call sites forwarded",
                        });
                    }
                    foreach (var f in ctResult.Failed)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            Outcome  = "failed",
                            Reason   = $"phase:propagate_ct — {f.Reason}",
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
        var status2   = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId    = changeId,
            BlobName    = blobName2,
            Succeeded   = succeeded,
            Failed      = failed,
            Skipped     = skipped,
            RolledBack  = 0,
            Attempted   = succeeded + failed + skipped,
            Failures    = failures,
            Severity    = status2.Severity,
            Directive   = status2.Directive,
            BreakerOpen = status2.Open,
        };
    }

    // ── Phase 7 — async_migrate dispatcher ────────────────────────────────────

    [McpServerTool]
    [Description("""
        Unified dispatcher for six async-migration operations. Dispatches to the appropriate
        engine based on the operation string; all operations check the circuit breaker first
        and return BatchResultSummary.

        operation: one of six values — call describe_tool_options("async_migrate") for valid
                   values and required input fields per operation.
        input:     AsyncMigrateInput — fields vary by operation; see describe_tool_options.

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-item details.
        Severity="halt" means the circuit breaker opened — call get_breaker_status then reset_breaker.
        """)]
    public async Task<BatchResultSummary> AsyncMigrate(
        string            operation,
        AsyncMigrateInput input,
        CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "propagate_cancellation_token" => await PropagateCancellationToken(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                cancellationToken),

            "convert_to_async_bridge" => await ConvertToAsyncBridge(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                input.PropagateCancellationTokens,
                cancellationToken),

            "add_cancellation_token" => await AddCancellationToken(
                new BatchTargetInput
                {
                    Targets  = input.Targets ?? [],
                    DryRun   = input.DryRun,
                    MaxItems = input.MaxItems,
                },
                cancellationToken),

            "run_uplift" => await RunUplift(
                new RunUpliftInput
                {
                    Targets                    = input.UpliftTargets ?? [],
                    DryRun                     = input.DryRun,
                    MaxCallersPerMethod        = input.MaxCallersPerMethod,
                    PropagateCancellationTokens = input.PropagateCancellationTokens,
                },
                cancellationToken),

            "flag_migration_candidates" => await FlagMigrationCandidates(
                new FlagCandidatesInput
                {
                    Scope       = input.FlagScope,
                    Targets     = input.FlagTargets,
                    ProjectName = input.ProjectName,
                    Pattern     = input.Pattern,
                    MinScore    = input.MinScore,
                    DryRun      = input.DryRun,
                    ForceRescan = input.ForceRescan,
                },
                cancellationToken),

            "asyncify" => await Asyncify(
                new AsyncifyInput
                {
                    ProjectName                  = input.ProjectName,
                    MethodTargets                = input.MethodTargets,
                    Exclusions                   = input.Exclusions,
                    DryRun                       = input.DryRun,
                    PropagateCancellationTokens  = input.PropagateCancellationTokens,
                    MaxMethods                   = input.MaxMethods,
                    MaxCallersPerMethod          = input.MaxCallersPerMethod,
                    MinScore                     = input.MinScore,
                    ScoreThreshold               = input.ScoreThreshold,
                },
                cancellationToken),

            _ => throw new ArgumentException(
                $"Unknown operation '{operation}'. Valid: propagate_cancellation_token, " +
                "convert_to_async_bridge, add_cancellation_token, run_uplift, " +
                "flag_migration_candidates, asyncify.",
                nameof(operation))
        };
    }

    // ── describe_tool_options ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Returns reference documentation for a named tool: valid operation values, required input
        fields per operation, valid transform/kind/detector names, and parameter defaults. Call
        this once at the start of a session when you need to know what values a tool accepts.

        toolName: the MCP tool name (e.g. "async_migrate", "scan", "apply_file_codemod").

        Returns a ToolOptionsResult with a Description string (human-readable reference table) and
        a StructuredOptions object (machine-readable key→field-list map). Returns ErrorCode =
        "UnknownTool" if the tool name is not recognised.
        """)]
    public ToolOptionsResult DescribeToolOptions(string toolName)
    {
        return toolName switch
        {
            "async_migrate"                         => AsyncMigrateOptions(),
            "scan"                                  => ScanOptions(),
            "scan_migration_candidates"             => ScanMigrationCandidatesOptions(),
            "apply_file_codemod"                    => ApplyFileCodemodOptions(),
            "apply_method_codemod"                  => ApplyMethodCodemodOptions(),
            "apply_class_codemod"                   => ApplyClassCodemodOptions(),
            "generate"                              => GenerateOptions(),
            "convert_switch_to_pattern_safe"        => ConvertSwitchOptions(),
            "analyze_switch_for_pattern_conversion" => AnalyzeSwitchOptions(),
            "analyze_foreach_for_linq_conversion"   => AnalyzeForeachOptions(),
            _ => new ToolOptionsResult
            {
                Description = $"Unknown tool '{toolName}'.",
                Error       = new ResultError("UnknownTool", $"No options registered for '{toolName}'.")
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
            ["convert_to_async_bridge"]      = new { Targets = "list of {FilePath, MethodNames}", DryRun = false, PropagateCancellationTokens = true },
            ["add_cancellation_token"]       = new { Targets = "list of {FilePath, MethodNames?}", DryRun = false, MaxItems = 100 },
            ["run_uplift"]                   = new { UpliftTargets = "list of {BridgedMethodName, ProjectName?}", DryRun = false, MaxCallersPerMethod = 10, PropagateCancellationTokens = true },
            ["flag_migration_candidates"]    = new { FlagScope = "targets|project", DryRun = false, MinScore = 50, ForceRescan = false },
            ["asyncify"]                     = new { DryRun = false, MaxMethods = 50, MaxCallersPerMethod = 10, MinScore = 50, ScoreThreshold = 60 },
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
              ByPattern     — candidate count keyed by pattern name.
              ByClass       — List<ClassCandidateSummary> sorted descending by Count. Each entry
                              has ClassName, ProjectName (.csproj name), FilePath, Count.
              ByScoreBucket — counts in "<0", "0-25", "26-50", "51-75", "76plus" buckets.
                              The '<0' bucket is valid but rare; it applies to methods flagged
                              manually with a negative score (auto-flagging discards sub-zero
                              results before writing the attribute).
              TopCandidates — populated when topN or minScore is set; null otherwise.
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
            ["rejectedForms"]  = new[] { "multiple-variable assignment per case", "different variables across cases", "complex multi-statement bodies" }
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

    private static ToolOptionsResult ScanOptions() => new()
    {
        Description = """
            scan — valid detector IDs grouped by domain (94 total):

            concurrency (26):
              async_in_constructor, async_over_sync, async_void_without_try_catch,
              cancellation_token_not_forwarded, cas_loop_without_backoff,
              check_then_act_on_dictionary, concurrent_collection_opportunities,
              configure_await_missing, double_checked_locking, inconsistent_async_suffix,
              mismatched_await, missing_cancellation_tokens, possible_deadlocks,
              semaphore_usage, sequential_independent_awaits, task_delay_usage,
              task_delay_zero_usage, task_run_in, task_void_usage, task_when_all_usage,
              task_yield_usage, unawaited_fire_and_forget, unobserved_task_in_field,
              unsafe_lazy_init, unsafe_lazy_init_thread, value_task_misuse

            config (3):
              json_anti_patterns, package_inconsistency, project_consistency

            convention (6):
              mutable_public_collection_properties, mutable_public_properties,
              naming_violations, readonly_field_candidates, string_magic_values,
              todo_fixme_comments

            correctness (18):
              all_throw_sites, empty_catch_blocks, exception_handling,
              memory_leaks, misbound_overload_chains, missing_generic_constraints,
              multiple_out_parameter_methods, non_exhaustive_enum_switches,
              possible_infinite_loops, redundant_cast, resource_disposal,
              services_not_registered, stack_overflow_risks, unawaked_dispose,
              unbounded_recursion, unbounded_static_collections, value_type_mutation_intent

            dead-code (8):
              obsolete_callers, uninstantiated_types, unused_constructors,
              unused_event_subscriptions, unused_interfaces, unused_local_variables,
              unused_private_fields, unused_references

            misc (3):
              anti_patterns, blocking_calls_in, finalizer_on_disposable

            performance (11):
              boxing_allocations, implicit_nullable_boxing,
              inefficient_string_comparisons, linq_n1_patterns, linq_redundant_where,
              multiple_enumeration, performance, re_do_s_patterns, regex_new_in_loop,
              string_format_in_loops, use_frozen_collections

            security (5):
              hardcoded_paths, reflection_usage, security, sql_injection,
              unvalidated_regex_source

            structure (14):
              circular_dependencies, circular_type_references,
              duplicate_blocks_in_hierarchy, duplicate_methods,
              interface_extraction_candidates, internal_classes_that_could_be_private,
              large_methods, large_switch_statements, large_types, layer_violations,
              long_parameter_list, namespace_path_mismatches, primitive_obsession,
              structural_smells, type_cohesion

            scope values: "file" | "project" | "solution"
            scopeName: filePath for scope=file; projectName for scope=project; omit for solution.
              For duplicate_blocks_in_hierarchy, scopeName is the root type name.
            File-scope-only detectors require scope="file". unused_references requires scope="project".
            Call describe_scan_detectors for per-detector scope hints and descriptions.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["concurrency"]  = new[] { "async_in_constructor", "async_over_sync", "async_void_without_try_catch", "cancellation_token_not_forwarded", "cas_loop_without_backoff", "check_then_act_on_dictionary", "concurrent_collection_opportunities", "configure_await_missing", "double_checked_locking", "inconsistent_async_suffix", "mismatched_await", "missing_cancellation_tokens", "possible_deadlocks", "semaphore_usage", "sequential_independent_awaits", "task_delay_usage", "task_delay_zero_usage", "task_run_in", "task_void_usage", "task_when_all_usage", "task_yield_usage", "unawaited_fire_and_forget", "unobserved_task_in_field", "unsafe_lazy_init", "unsafe_lazy_init_thread", "value_task_misuse" },
            ["config"]       = new[] { "json_anti_patterns", "package_inconsistency", "project_consistency" },
            ["convention"]   = new[] { "mutable_public_collection_properties", "mutable_public_properties", "naming_violations", "readonly_field_candidates", "string_magic_values", "todo_fixme_comments" },
            ["correctness"]  = new[] { "all_throw_sites", "empty_catch_blocks", "exception_handling", "memory_leaks", "misbound_overload_chains", "missing_generic_constraints", "multiple_out_parameter_methods", "non_exhaustive_enum_switches", "possible_infinite_loops", "redundant_cast", "resource_disposal", "services_not_registered", "stack_overflow_risks", "unawaked_dispose", "unbounded_recursion", "unbounded_static_collections", "value_type_mutation_intent" },
            ["dead-code"]    = new[] { "obsolete_callers", "uninstantiated_types", "unused_constructors", "unused_event_subscriptions", "unused_interfaces", "unused_local_variables", "unused_private_fields", "unused_references" },
            ["misc"]         = new[] { "anti_patterns", "blocking_calls_in", "finalizer_on_disposable" },
            ["performance"]  = new[] { "boxing_allocations", "implicit_nullable_boxing", "inefficient_string_comparisons", "linq_n1_patterns", "linq_redundant_where", "multiple_enumeration", "performance", "re_do_s_patterns", "regex_new_in_loop", "string_format_in_loops", "use_frozen_collections" },
            ["security"]     = new[] { "hardcoded_paths", "reflection_usage", "security", "sql_injection", "unvalidated_regex_source" },
            ["structure"]    = new[] { "circular_dependencies", "circular_type_references", "duplicate_blocks_in_hierarchy", "duplicate_methods", "interface_extraction_candidates", "internal_classes_that_could_be_private", "large_methods", "large_switch_statements", "large_types", "layer_violations", "long_parameter_list", "namespace_path_mismatches", "primitive_obsession", "structural_smells", "type_cohesion" },
        }
    };
}
