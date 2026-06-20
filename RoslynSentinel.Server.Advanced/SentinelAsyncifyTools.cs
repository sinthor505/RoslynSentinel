using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelAsyncifyTools
{
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly AsyncBatchEngine _asyncBatchEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelAsyncifyTools> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public SentinelAsyncifyTools(
        AntiPatternEngine antiPatternEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        AsyncBatchEngine asyncBatchEngine,
        MsToolAugmentEngine msToolAugmentEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelAsyncifyTools> logger)
    {
        _antiPatternEngine = antiPatternEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _asyncBatchEngine = asyncBatchEngine;
        _msToolAugmentEngine = msToolAugmentEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── scan_migration_candidates ─────────────────────────────────────────────

    [McpServerTool(Name = "ScanMigrationCandidates")]
    [Produces(DataTag.MigrationCandidate)]
    [Description("""
        Returns [MigrationCandidate]-attributed methods. Entry point for all async-migration workflows.

        pattern — valid values:
          AsyncBridgeCandidate     — sync wrapper suitable for bridge conversion (main path)
          HandlerExtractCandidate  — code block to extract into a separate handler method
          HandlerToAsyncCandidate  — handler method that should be converted to async
          AsyncCallerUpliftCandidate — sync caller of an already-bridged async method
          null = all patterns.

        summarize=true → guaranteed ≤2KB dashboard.
          MigrationScanSummary fields: ByPattern (count per pattern), ByClass (ClassName, ProjectName,
          Count — sorted desc, capped at 10; ByClassTruncated=true when truncated), ByScoreBucket
          ("<0", "0-25", "26-50", "51-75", "76plus"), TopCandidates (MethodName, ClassName, Pattern,
          Score, Summary — only when topN or minScore set, capped at 5 entries).

        summarize=false + limit/offset → full paged List<MigrationCandidateFinding>. minScore filters in
        both modes; TotalRecords reflects post-filter count. A method flagged for two patterns appears twice.

        To use scan results with flag_migration_candidates(scope: "targets"): map each Candidate.FilePath
        and Candidate.MethodName to a FlagCandidateTarget entry.

        When results exceed the inline threshold, LargeResultInfo is populated — call get_scan_result(scanId)
        to page through results.
        """)]
    public async Task<ToolResult<object>> ScanMigrationCandidates(
        string? filePath = null,
        string? projectName = null,
        string? pattern = null,
        bool summarize = false,
        int? topN = null,
        int? minScore = null,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
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

            // B1 Fix 4: 10 KB overflow safety net — should be unreachable with slim types + caps.
            const int SummaryThresholdBytes = 10 * 1024;
            var summaryJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(summary, _jsonOptions);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Summary JSON size: {SizeBytes} bytes", summaryJson.Length);
            }

            if (summaryJson.Length > SummaryThresholdBytes)
            {
                _logger.LogWarning("Summary JSON size {SizeBytes} bytes exceeds expected limits. " +
                                   "This may indicate an issue with the summarization logic or unusually large data. " +
                                   "Consider reviewing the summary generation and applying stricter caps if necessary.",
                                   summaryJson.Length);

                var scanId = Guid.NewGuid().ToString("N");
                var solutionRoot = _workspaceManager.GetSolutionRoot();
                if (!string.IsNullOrEmpty(solutionRoot))
                {
                    var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans");
                    Directory.CreateDirectory(dir);
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                    var fp = System.IO.Path.Combine(dir, $"scan_{ts}_{scanId}.json");
                    await File.WriteAllTextAsync(fp,
                        System.Text.Json.JsonSerializer.Serialize(summary, _jsonOptions),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    return new ToolResult<object>
                    {
                        Success = true,
                        LargeResult = new LargeResultInfo(
                            resultType: typeof(MigrationScanSummary).Name,
                            writtenToFile: true,
                            filePath: fp,
                            scanId: scanId,
                            sizeBytes: summaryJson.Length,
                            totalRecords: aggregateFindings.Count,
                            message: $"Summary exceeded {SummaryThresholdBytes} bytes ({summaryJson.Length} bytes). " +
                                           $"Use get_scan_result(scanId: \"{scanId}\") to page through results.")
                    };
                }
            }

            return new ToolResult<object>
            {
                Success = true,
                Data = summary
            };
        }
        else
        {
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

            var (offloaded, storedPath, scanId, allBytes) = await SentinelScanTools.StoreScanResultAsync(
                allFindings, _workspaceManager.GetSolutionRoot(), ScanWrapperType.MigrationCandidateFindingList);

            if (offloaded)
            {
                return new ToolResult<object>
                {
                    Success = true,
                    TotalRecords = totalCount,
                    HasMore = hasMore,
                    LargeResult = new LargeResultInfo(
                        resultType: typeof(MigrationCandidateFinding).Name,
                        writtenToFile: true,
                        filePath: storedPath.ToString(),
                        scanId: scanId,
                        sizeBytes: allBytes.Length,
                        totalRecords: totalCount,
                        message: $"Result written to file ({allBytes.Length} bytes, {totalCount} records). " +
                                       $"Use get_scan_result(scanId: \"{scanId}\") to page through results. " +
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
    }

    // ── get_async_migration_progress ─────────────────────────────────────────

    [McpServerTool(Name = "GetAsyncMigrationProgress")]
    [Produces(DataTag.AsyncMigrationProgressReport)]
    [Description("""
        Returns async migration progress statistics for the solution or a single project. Reports: total
        async Task/ValueTask methods, how many have a CancellationToken parameter (and how many still need
        one), percentage coverage, Asyncify-bridge wrapper count ([Obsolete("Asyncify-bridge:...")]),
        bridge call sites pending migration (CS0618), and async void event handlers (informational —
        their signatures cannot be extended). projectName=null → entire solution.
        """)]
    public async Task<ToolResult<AsyncMigrationProgressReport>> GetAsyncMigrationProgress(
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        Progress<string>? progress = null,
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

    // ── flag_migration_candidates ─────────────────────────────────────────────

    [McpServerTool(Name = "FlagMigrationCandidates")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Step 1 of the manual bridge path — marks methods with [MigrationCandidate] attributes so that
        bridge_async_methods can locate them. Skip this step when using the asyncify macro (which flags
        internally). Call scan_migration_candidates first to identify candidates.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)  — survey candidates
          2. flag_migration_candidates                   ← you are here
          3. bridge_async_methods(targets: [...])
          4. uplift_callers(targets: SuggestedUpliftTargets from bridge result)
          5. propagate_cancellation_token(targets: SuggestedPropagateTargets from uplift result)

        scope: "targets" (default) or "project".
          "targets" — flag an explicit list. Required: flagTargets.
          "project" — autonomous scan; scores and flags qualifying methods.
                      Required: leave flagTargets null. Optional: projectName, pattern, minScore, forceRescan.

        flagTargets: scope="targets" — list of { FilePath, MethodName, Pattern, Score?, Reason? }.
        projectName: scope="project" — restrict to one project; null = entire solution.
        pattern: migration pattern to apply (default "AsyncBridgeCandidate").
          Also accepts: "HandlerExtractCandidate", "HandlerToAsyncCandidate", "AsyncCallerUpliftCandidate".
        minScore: minimum score to flag (scope="project" only, default 50).
        forceRescan: re-evaluate already-flagged methods (scope="project" only, default false).
        dryRun: reports what would be flagged without writing files.

        Returns BatchResultSummary. Succeeded = methods flagged. Skipped = below-minScore or already-flagged.
        BlobName = full per-method detail on disk. Use get_operation_detail(changeId) for details.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> FlagMigrationCandidates(
        string scope = "targets",
        List<FlagCandidateTarget>? flagTargets = null,
        string? projectName = null,
        string pattern = "AsyncBridgeCandidate",
        int minScore = 50,
        bool dryRun = false,
        bool forceRescan = false,
        Progress<string>? progress = null,
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

        try
        {
            var result = await FlagMigrationCandidatesCore(
                new FlagCandidatesInput
                {
                    Scope = scope,
                    Targets = flagTargets,
                    ProjectName = projectName,
                    Pattern = pattern,
                    MinScore = minScore,
                    DryRun = dryRun,
                    ForceRescan = forceRescan,
                },
                progress,
                cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── bridge_async_methods ──────────────────────────────────────────────────

    [McpServerTool(Name = "BridgeAsyncMethods")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Step 2 of the bridge path — converts each named method to the Asyncify-bridge pattern:
        a sync wrapper that delegates to an async overload. Call after flag_migration_candidates,
        or use the asyncify macro to run all steps automatically.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)
          2. flag_migration_candidates(scope: "project")
          3. bridge_async_methods                        ← you are here
          4. uplift_callers(targets: SuggestedUpliftTargets)  ← pass SuggestedUpliftTargets directly
          5. propagate_cancellation_token(targets: SuggestedPropagateTargets)

        targets: list of { FilePath, MethodNames } — MethodNames is required (not optional here).
        dryRun: validates without writing files. SuggestedUpliftTargets is still populated.
        maxItems: max (file × method) items to process (default 100).
        propagateCancellationTokens: propagate CT in the new async overload (default true).

        Each method is applied sequentially and written immediately so later methods in the same file
        see the updated source. Errors on one method do not abort others.

        Returns BridgeAsyncMethodsResult:
          Summary.ChangeId / Summary.BlobName — use with get_operation_detail for per-method detail.
          Summary.Succeeded / Summary.Failed / Summary.Attempted — aggregate counts.
          SuggestedUpliftTargets — pass directly as targets to uplift_callers. Each entry has
            BridgedMethodName. Empty when Succeeded=0.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BridgeAsyncMethodsResult>> BridgeAsyncMethods(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        bool propagateCancellationTokens = true,
        Progress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BridgeAsyncMethodsResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        try
        {
            var (summary, suggestedUpliftTargets) = await BridgeAsyncMethodsCore(
                new BatchTargetInput { Targets = targets, DryRun = dryRun, MaxItems = maxItems },
                propagateCancellationTokens,
                progress,
                cancellationToken);
            return new ToolResult<BridgeAsyncMethodsResult>
            {
                Success = true,
                Data = new BridgeAsyncMethodsResult
                {
                    Summary = summary,
                    SuggestedUpliftTargets = suggestedUpliftTargets,
                }
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<BridgeAsyncMethodsResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }
    }

    // ── uplift_callers ────────────────────────────────────────────────────────

    [McpServerTool(Name = "UpliftCallers")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Step 3 of the bridge path — updates sync callers of each bridge wrapper to call the async
        overload directly. Pass SuggestedUpliftTargets from bridge_async_methods as targets.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)
          2. flag_migration_candidates(scope: "project")
          3. bridge_async_methods(targets: [...])
          4. uplift_callers                              ← you are here
          5. propagate_cancellation_token(targets: SuggestedPropagateTargets)  ← pass SuggestedPropagateTargets

        targets: list of { BridgedMethodName, ProjectName? }. Pass SuggestedUpliftTargets from
          bridge_async_methods directly — no transformation required.
        dryRun: reports without writing files. SuggestedPropagateTargets is still populated.
        maxCallersPerMethod: max callers per bridged method (default 10).
        propagateCancellationTokens: propagate CT in updated callers (default true).

        Returns UpliftCallersResult:
          Summary.ChangeId / Summary.BlobName — use with get_operation_detail for detail.
          Summary.Succeeded = callers uplifted. Summary.Failed = callers flagged NeedsManualReview.
          SuggestedPropagateTargets — pass directly as targets to propagate_cancellation_token.
            Each entry has FilePath (files touched during uplift); MethodNames is null (whole file).
            Empty when Succeeded=0.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<UpliftCallersResult>> UpliftCallers(
        List<UpliftTarget> targets,
        bool dryRun = false,
        int maxCallersPerMethod = 10,
        bool propagateCancellationTokens = true,
        Progress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<UpliftCallersResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        try
        {
            var (summary, suggestedPropagateTargets) = await UpliftCallersCore(
                new RunUpliftInput
                {
                    Targets = targets,
                    DryRun = dryRun,
                    MaxCallersPerMethod = maxCallersPerMethod,
                    PropagateCancellationTokens = propagateCancellationTokens,
                },
                progress,
                cancellationToken);
            return new ToolResult<UpliftCallersResult>
            {
                Success = true,
                Data = new UpliftCallersResult
                {
                    Summary = summary,
                    SuggestedPropagateTargets = suggestedPropagateTargets,
                }
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<UpliftCallersResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              "An unexpected error occurred.", ex.Message)
            };
        }
    }

    // ── propagate_cancellation_token ──────────────────────────────────────────

    [McpServerTool(Name = "PropagateCancellationToken")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Step 4 of the bridge path — threads CancellationToken through async call chains in the
        specified files. Pass SuggestedPropagateTargets from uplift_callers as targets.
        Also usable standalone to clean up CT forwarding in any set of files.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)
          2. flag_migration_candidates(scope: "project")
          3. bridge_async_methods(targets: [...])
          4. uplift_callers(targets: [...])
          5. propagate_cancellation_token               ← you are here
               targets: SuggestedPropagateTargets from uplift_callers — no transformation required.

        targets: list of { FilePath, MethodNames? }. null MethodNames = all eligible methods in the file.
          Pass SuggestedPropagateTargets from uplift_callers directly.
        dryRun: computes without writing files.
        maxItems: max files to process (default 100).

        Returns BatchResultSummary. BlobName = full per-file detail on disk.
        Use get_operation_detail(changeId) for details.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> PropagateCancellationToken(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        Progress<string>? progress = null,
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

        try
        {
            var result = await PropagateCancellationTokenCore(
                new BatchTargetInput { Targets = targets, DryRun = dryRun, MaxItems = maxItems },
                progress,
                cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── add_cancellation_token ────────────────────────────────────────────────

    [McpServerTool(Name = "AddCancellationToken")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Utility — adds a CancellationToken parameter to async methods that lack one. Independent of
        the main bridge path; use as needed to ensure async methods accept CT. Differs from
        propagate_cancellation_token, which threads an existing CT through async call chains — this
        tool adds the CT parameter to the method signature itself.

        targets: list of { FilePath, MethodNames? }. null MethodNames = all eligible async methods in the file.
        dryRun: computes without writing files.
        maxItems: max files to process (default 100).

        Returns BatchResultSummary. Succeeded = files modified. BlobName = full detail on disk.
        Use get_operation_detail(changeId) for per-file details.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> AddCancellationToken(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        Progress<string>? progress = null,
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

        try
        {
            var result = await AddCancellationTokenCore(
                new BatchTargetInput { Targets = targets, DryRun = dryRun, MaxItems = maxItems },
                progress,
                cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── extract_event_handlers ────────────────────────────────────────────────

    [McpServerTool(Name = "ExtractEventHandlers")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Event handler path, step 1 — extracts a nominated code block from inside a method into a new
        private method using semantic analysis. Produces the correct return type (fixes the standard
        extract_method bug where selections ending with 'return expr' produce void).

        Event handler path:
          1. scan_migration_candidates(pattern: "HandlerExtractCandidate")  — find handlers to extract
          2. extract_event_handlers                     ← you are here
          3. event_handlers_to_async(projectName: "...")

        targets: list of { FilePath, NewMethodName, ContextSnippet, LineBefore?, LineAfter? }.
          FilePath       — absolute path to the .cs file.
          NewMethodName  — valid C# identifier for the new extracted method (required).
          ContextSnippet — short unique fragment identifying the code block to extract (required).
          LineBefore / LineAfter — optional disambiguation lines.
          Targets in the same file are processed sequentially — each extraction sees the file as left
          by the previous one.
        dryRun: validates that each ContextSnippet is locatable without writing files. Use as a
          pre-flight check before committing.

        Returns BatchResultSummary. Failed = 1 per target where ContextSnippet could not be located
        or extraction failed. Failures[].Reason contains the diagnostic.
        BlobName = full per-target detail on disk.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> ExtractEventHandlers(
        List<HandlerExtractTarget> targets,
        bool dryRun = false,
        Progress<string>? progress = null,
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

        try
        {
            var result = await HandlerExtractCore(targets, dryRun, progress, cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── event_handlers_to_async ───────────────────────────────────────────────

    [McpServerTool(Name = "EventHandlersToAsync")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Event handler path, step 2 — converts all [MigrationCandidate("HandlerToAsyncCandidate")]-flagged
        methods to the Asyncify-bridge pattern (sync wrapper + async overload). Auto-discovers candidates
        by pattern; no explicit method list required.

        Event handler path:
          1. scan_migration_candidates(pattern: "HandlerExtractCandidate")
          2. extract_event_handlers(targets: [...])
          3. event_handlers_to_async                    ← you are here

        projectName: scope discovery to one project; null = entire solution.
        dryRun: validates without writing files.
        maxItems: max methods to process (default 100).
        propagateCancellationTokens: propagate CT in the new async overload (default true).

        Returns BatchResultSummary. Succeeded = methods converted. Skipped = over maxItems limit.
        BlobName = full per-method detail on disk. Use get_operation_detail(changeId) for details.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> EventHandlersToAsync(
        string? projectName = null,
        bool dryRun = false,
        int maxItems = 100,
        bool propagateCancellationTokens = true,
        Progress<string>? progress = null,
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

        try
        {
            var result = await HandlerToAsyncCore(
                projectName, dryRun, maxItems, propagateCancellationTokens, progress, cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── asyncify (macro) ──────────────────────────────────────────────────────

    [McpServerTool(Name = "Asyncify")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Full-workflow macro — runs the complete bridge path (Flag → Bridge → Uplift → Propagate CT)
        in a single call. The server owns and executes the fixed sequence. Use bridge_async_methods,
        uplift_callers, and propagate_cancellation_token individually for step-by-step control.

        Internal sequence:
          Phase 1 (Flag)       — discovers qualifying sync methods and flags
                                 [MigrationCandidate("AsyncBridgeCandidate")].
                                 Skipped when methodTargets is provided.
          Phase 2 (Bridge)     — converts flagged methods to the Asyncify-bridge pattern.
          Phase 3 (Uplift)     — uplifts callers of each bridge wrapper to the async overload.
          Phase 4 (Propagate)  — propagates CancellationToken in all bridged files.
                                 Skipped when propagateCancellationTokens=false or dryRun=true.

        Checks the circuit breaker before starting; records total outcome across all phases;
        writes one forensic blob. Full detail on disk — only summary counts returned inline.

        projectName: project to process; null = entire solution.
        methodTargets: explicit (FilePath, MethodName) list — skips Phase 1 (flag discovery).
        exclusions: method names to skip in every phase.
        dryRun: reports without writing files.
        propagateCancellationTokens: run Phase 4 after bridge+uplift (default true).
        maxMethods: max methods in bridge phase (default 50).
        maxCallersPerMethod: max callers per bridged method in uplift (default 10).
        minScore: minimum discovery score in Phase 1 (default 50).
        scoreThreshold: max score eligible for bridge conversion in Phase 2 (default 60).
        maxRuntimeSeconds: wall-clock limit in seconds — the current phase item finishes, then
          remaining phases are skipped and a partial result is returned. 0 = no limit (default).
          Set this below the MCP transport timeout to guarantee the tool returns in time.
        maxIterations: total items cap across all phases (bridged + uplifted + CT-propagated).
          Remaining phases are skipped when the count is reached. 0 = no limit (default).

        Returns BatchResultSummary. BlobName = full per-phase, per-method detail on disk.
        Succeeded = bridges + uplifts across all phases. Skipped = below-minScore / remaining candidates.
        When stopped early, Directive contains "stopped_early" with the reason.
        Use get_operation_detail(changeId) for per-phase breakdown.
        Severity="halt" → circuit breaker opened; call get_breaker_status then reset_breaker.
        """)]
    public async Task<ToolResult<BatchResultSummary>> Asyncify(
        string? projectName = null,
        List<FlagCandidateTarget>? methodTargets = null,
        List<string>? exclusions = null,
        bool dryRun = false,
        bool propagateCancellationTokens = true,
        int maxMethods = 50,
        int maxCallersPerMethod = 10,
        int minScore = 50,
        int scoreThreshold = 60,
        int maxRuntimeSeconds = 0,
        int maxIterations = 0,
        Progress<string>? progress = null,
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

        try
        {
            var result = await AsyncifyCore(
                new AsyncifyInput
                {
                    ProjectName = projectName,
                    MethodTargets = methodTargets,
                    Exclusions = exclusions,
                    DryRun = dryRun,
                    PropagateCancellationTokens = propagateCancellationTokens,
                    MaxMethods = maxMethods,
                    MaxCallersPerMethod = maxCallersPerMethod,
                    MinScore = minScore,
                    ScoreThreshold = scoreThreshold,
                    MaxRuntimeSeconds = maxRuntimeSeconds,
                    MaxIterations = maxIterations,
                },
                progress, cancellationToken);
            return new ToolResult<BatchResultSummary> { Success = true, Data = result };
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
    }

    // ── internal core implementations ─────────────────────────────────────────

    private async Task<BatchResultSummary> PropagateCancellationTokenCore(
        BatchTargetInput input,
        Progress<string>? progress = null,
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
            result = await _asyncBatchEngine.PropagateCancellationTokenBatchAsync(batchInput, progress: progress, cancellationToken: cancellationToken);
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
                Outcome = a.TotalForwarded > 0 ? OperationOutcome.Succeeded : OperationOutcome.Skipped,
                Reason = a.TotalForwarded == 0 ? "no eligible call sites" : null,
            });
        }
        foreach (var f in result.Failed)
        {
            items.Add(new OperationItemRecord { FilePath = f.FilePath, Outcome = OperationOutcome.Failed, Reason = f.Reason });
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "propagate_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();
        var failures = result.Failed
            .Take(15)
            .Select(f => new FailureDetail { FilePath = f.FilePath, Reason = f.Reason, Outcome = OperationOutcome.Failed })
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

    private async Task<(BatchResultSummary Summary, List<UpliftTarget> SuggestedUpliftTargets)> BridgeAsyncMethodsCore(
        BatchTargetInput input,
        bool propagateCancellationTokens = true,
        Progress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return (halt, new List<UpliftTarget>());
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
                    Reason = "MethodNames must be specified for bridge_async_methods",
                    Outcome = OperationOutcome.Failed,
                };
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = OperationOutcome.Failed, Reason = fd.Reason });
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
                        Outcome = OperationOutcome.Skipped,
                        Reason = "dry_run",
                    });
                    succeeded++;
                    continue;
                }

                try
                {
                    string? updatedSource;
                    var convertResult = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                        target.FilePath, methodName);
                    updatedSource = convertResult.UpdatedText;

                    if (propagateCancellationTokens)
                    {
                        var asyncMethod = methodName + "Async";
                        var propagationResult = await _asyncOptimizationEngine
                            .PropagateCancellationTokenInMethodAsync(target.FilePath, asyncMethod, progress: progress, cancellationToken: cancellationToken);
                        if (!string.IsNullOrEmpty(propagationResult.UpdatedText))
                        {
                            updatedSource = propagationResult.UpdatedText;
                        }
                    }

                    var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<FilePath, string> { { target.FilePath, updatedSource } });

                    string? beforeSource782 = null;
                    applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource782);

                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        MethodName = methodName,
                        Outcome = OperationOutcome.Succeeded,
                        BeforeSource = beforeSource782,
                    });
                    succeeded++;
                }
                catch (Exception ex)
                {
                    string reason = ex.Message;
                    bool handled = false;

                    if (ex is InvalidOperationException && ex.Message.Contains("already exists"))
                    {
                        var asyncMethodName = methodName + "Async";
                        try
                        {
                            var ctResult = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync(
                                target.FilePath, asyncMethodName);
                            if (ctResult.Outcome == EditOutcome.Modified && ctResult.UpdatedText != null)
                            {
                                var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                                    new Dictionary<FilePath, string> { { target.FilePath, ctResult.UpdatedText } });
                                string? beforeSrc = null;
                                applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSrc);
                                items.Add(new OperationItemRecord
                                {
                                    FilePath = target.FilePath,
                                    MethodName = methodName,
                                    Outcome = OperationOutcome.Succeeded,
                                    Reason = $"CT added to existing async overload '{asyncMethodName}'",
                                    BeforeSource = beforeSrc,
                                });
                                succeeded++;
                                handled = true;
                            }
                            else if (ctResult.Outcome == EditOutcome.NoChange)
                            {
                                items.Add(new OperationItemRecord
                                {
                                    FilePath = target.FilePath,
                                    MethodName = methodName,
                                    Outcome = OperationOutcome.Skipped,
                                    Reason = $"Async overload '{asyncMethodName}' already exists and already has CancellationToken",
                                });
                                handled = true;
                            }
                            else
                            {
                                reason = $"{ex.Message}; CT-add fallback: {ctResult.Message ?? ctResult.Outcome.ToString()}";
                            }
                        }
                        catch (Exception ctEx)
                        {
                            reason = $"{ex.Message}; CT-add fallback failed: {ctEx.Message}";
                        }
                    }

                    if (!handled)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = target.FilePath,
                            MethodName = methodName,
                            Outcome = OperationOutcome.Failed,
                            Reason = reason,
                        });
                        if (failures.Count < 15)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = target.FilePath,
                                MethodName = methodName,
                                Reason = reason,
                                Outcome = OperationOutcome.Failed,
                            });
                        }
                        failed++;
                        _logger.LogWarning(
                            "BridgeAsyncMethods: {Method} in {File} failed: {Reason}",
                            methodName, target.FilePath, reason);
                    }
                }
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "bridge_async_methods", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        var summary = new BatchResultSummary
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

        var suggestedUpliftTargets = items
            .Where(i => (i.Outcome == OperationOutcome.Succeeded ||
                         (i.Outcome == OperationOutcome.Skipped && i.Reason == "dry_run"))
                        && i.MethodName != null)
            .Select(i => new UpliftTarget { BridgedMethodName = i.MethodName! })
            .DistinctBy(t => t.BridgedMethodName)
            .ToList();

        return (summary, suggestedUpliftTargets);
    }

    private async Task<BatchResultSummary> AddCancellationTokenCore(
        BatchTargetInput input,
        Progress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        var allChanges = new Dictionary<FilePath, string>();
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
                        target.FilePath, target.MethodNames, progress: progress, cancellationToken: cancellationToken);

                if (updatedSource.StartsWith("// Error:"))
                {
                    var reason = updatedSource;
                    items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = OperationOutcome.Failed, Reason = reason });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = OperationOutcome.Failed });
                    }
                    failed++;
                }
                else if (modified.Count == 0)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        Outcome = OperationOutcome.Skipped,
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
                            Outcome = input.DryRun ? OperationOutcome.Skipped : OperationOutcome.Succeeded,
                            Reason = input.DryRun ? "dry_run" : null,
                        });
                    }

                    succeeded++;
                }
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = OperationOutcome.Failed, Reason = reason });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = OperationOutcome.Failed });
                }
                failed++;
                _logger.LogWarning("AddCancellationToken: {File} failed: {Reason}", target.FilePath, reason);
            }
        }

        if (allChanges.Count > 0)
        {
            var applyResult941 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
            if (applyResult941.PreImages != null)
            {
                foreach (var item in items)
                {
                    if (item.Outcome == OperationOutcome.Succeeded && item.BeforeSource == null)
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

    private async Task<(BatchResultSummary Summary, List<BatchTarget> SuggestedPropagateTargets)> UpliftCallersCore(
        RunUpliftInput input,
        Progress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return (halt, new List<BatchTarget>());
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
            result = await _asyncBatchEngine.RunUpliftBatchMultiAsync(multiInput, progress: progress, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpliftCallers batch unexpected exception");
            throw new InvalidOperationException(
                $"UpliftCallers failed: {ex.GetType().Name}: {ex.Message}", ex);
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
                    Outcome = OperationOutcome.Succeeded,
                });
            }
            foreach (var s in pm.Result.Skipped)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.CallerMethod,
                    Outcome = OperationOutcome.Failed,
                    Reason = s.Reason,
                });
            }
            if (pm.Error != null)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = pm.BridgedMethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = pm.Error,
                });
            }
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "uplift_callers", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();
        var failures = result.PerMethod
            .SelectMany(pm => pm.Result.Skipped.Select(s => new FailureDetail
            {
                FilePath = s.FilePath,
                MethodName = s.CallerMethod,
                Reason = s.Reason,
                Outcome = OperationOutcome.Failed,
            }))
            .Take(15)
            .ToList();

        var summary = new BatchResultSummary
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

        var suggestedPropagateTargets = result.PerMethod
            .SelectMany(pm => pm.Result.Uplifted.Select(u => u.FilePath))
            .Distinct()
            .Select(fp => new BatchTarget { FilePath = fp })
            .ToList();

        return (summary, suggestedPropagateTargets);
    }

    private async Task<BatchResultSummary> FlagMigrationCandidatesCore(
        FlagCandidatesInput input,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
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

                    foreach (var f in engineResult.Flagged)
                    {
                        string? beforeSource1135 = null;
                        applyResult1126.PreImages?.TryGetValue(f.FilePath, out beforeSource1135);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = OperationOutcome.Succeeded,
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
                            Outcome = input.DryRun ? OperationOutcome.Skipped : OperationOutcome.Succeeded,
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
                        Outcome = OperationOutcome.Skipped,
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
                        Outcome = OperationOutcome.Skipped,
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

                var allChanges = new Dictionary<FilePath, string>();
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
                        Outcome = OperationOutcome.Succeeded,
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
                        Outcome = OperationOutcome.Failed,
                        Reason = err,
                    });
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = tgt.FilePath,
                            MethodName = tgt.MethodName,
                            Reason = err,
                            Outcome = OperationOutcome.Failed,
                        });
                    }
                    failed++;
                }

                if (allChanges.Count > 0 && !input.DryRun)
                {
                    var applyResult1223 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                    if (applyResult1223.PreImages != null)
                    {
                        foreach (var item in items)
                        {
                            if (item.Outcome == OperationOutcome.Succeeded && item.BeforeSource == null)
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

    private async Task<BatchResultSummary> AsyncifyCore(
        AsyncifyInput input,
        Progress<string>? progress,
        CancellationToken? cancellationToken = default)
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None);
        if (input.MaxRuntimeSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(input.MaxRuntimeSeconds));

        var innerToken = cts.Token;
        int iterationsUsed = 0;
        bool stoppedEarly = false;
        string stopReason = "";

        void CheckIterations(int phaseItems)
        {
            iterationsUsed += phaseItems;
            if (input.MaxIterations > 0 && iterationsUsed >= input.MaxIterations && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                stopReason = $"maxIterations ({input.MaxIterations}) reached after {iterationsUsed} items";
            }
        }

        try
        {
            // ── Phase 1: Flag ─────────────────────────────────────────────────
            if (input.MethodTargets == null || input.MethodTargets.Count == 0)
            {
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
                                Outcome = OperationOutcome.Skipped,
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
                            Outcome = OperationOutcome.Succeeded,
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
                                Outcome = OperationOutcome.Skipped,
                                Reason = "excluded",
                            });
                            skipped++;
                            continue;
                        }

                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = OperationOutcome.Skipped,
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
                        Outcome = OperationOutcome.Skipped,
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
                        Outcome = OperationOutcome.Skipped,
                        Reason = "phase:flag — already flagged",
                    });
                    skipped++;
                }
            }
            else
            {
                var tuples = input.MethodTargets
                    .Where(t => input.Exclusions?.Contains(t.MethodName) != true)
                    .Select(t => (FilePath: t.FilePath, MethodName: t.MethodName,
                                  Pattern: t.Pattern, Score: t.Score, Reason: t.Reason))
                    .ToList();

                if (tuples.Count > 0)
                {
                    var (flagResults, flagErrors) =
                        await _asyncOptimizationEngine.FlagMultipleMigrationCandidatesAsync(tuples);

                    var allChanges = new Dictionary<FilePath, string>();
                    for (int i = 0; i < flagResults.Count; i++)
                    {
                        var r = flagResults[i];
                        if (r == null || r.Line == -1) { continue; }

                        foreach (var kv in r.Changes) { allChanges[kv.Key] = kv.Value; }
                        items.Add(new OperationItemRecord
                        {
                            FilePath = tuples[i].FilePath,
                            MethodName = tuples[i].MethodName,
                            Outcome = OperationOutcome.Succeeded,
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
                            Outcome = OperationOutcome.Failed,
                            Reason = $"phase:flag — {err}",
                        });
                        if (failures.Count < 15)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = tuples[idx].FilePath,
                                MethodName = tuples[idx].MethodName,
                                Reason = err,
                                Outcome = OperationOutcome.Failed,
                            });
                        }
                        failed++;
                    }
                    if (!input.DryRun && allChanges.Count > 0)
                    {
                        var applyResult1421 = await _workspaceManager.ApplyProposedChangesAsync(allChanges);
                        if (applyResult1421.PreImages != null)
                        {
                            foreach (var item in items)
                            {
                                if (item.Outcome == OperationOutcome.Succeeded && item.BeforeSource == null)
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
                progress: progress,
                cancellationToken: innerToken);

            foreach (var a in bridgeResult.Applied)
            {
                if (input.Exclusions?.Contains(a.MethodName) == true) { continue; }
                items.Add(new OperationItemRecord
                {
                    FilePath = a.FilePath,
                    MethodName = a.MethodName,
                    Outcome = OperationOutcome.Succeeded,
                    Reason = "phase:bridge",
                });
                succeeded++;
            }
            foreach (var s in bridgeResult.Skipped)
            {
                bool isValidationFailure = s.Reason.Contains("NeedsManualReview")
                    || s.Reason.Contains("already has CancellationToken")
                    || s.Reason.Contains("event handler");
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome = isValidationFailure ? OperationOutcome.Skipped : OperationOutcome.Failed,
                    Reason = $"phase:bridge — {s.Reason}",
                });
                if (isValidationFailure)
                {
                    skipped++;
                }
                else
                {
                    if (failures.Count < 15)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = s.FilePath,
                            MethodName = s.MethodName,
                            Reason = s.Reason,
                            Outcome = OperationOutcome.Failed,
                        });
                    }
                    failed++;
                }
            }
            if (bridgeResult.RemainingCandidates > 0)
            {
                skipped += bridgeResult.RemainingCandidates;
            }

            CheckIterations(bridgeResult.Applied.Count + bridgeResult.Skipped.Count);
            if (innerToken.IsCancellationRequested)
            {
                stoppedEarly = true;
                if (stopReason.Length == 0) stopReason = "maxRuntimeSeconds exceeded after bridge phase";
                goto WriteSummary;
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
                        progress: progress, cancellationToken: innerToken);

                    foreach (var pm in upliftResult.PerMethod)
                    {
                        foreach (var u in pm.Result.Uplifted)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = u.FilePath,
                                MethodName = u.CallerMethod,
                                Outcome = OperationOutcome.Succeeded,
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
                                Outcome = OperationOutcome.Failed,
                                Reason = $"phase:uplift — {s.Reason}",
                            });
                            if (failures.Count < 15)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath = s.FilePath,
                                    MethodName = s.CallerMethod,
                                    Reason = s.Reason,
                                    Outcome = OperationOutcome.Failed,
                                });
                            }
                            failed++;
                        }
                    }

                    CheckIterations(upliftResult.TotalUplifted + upliftResult.TotalSkipped);
                    if (innerToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        if (stopReason.Length == 0) stopReason = "maxRuntimeSeconds exceeded after uplift phase";
                        goto WriteSummary;
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
                        progress: progress, cancellationToken: innerToken);

                    foreach (var a in ctResult.Applied)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = a.FilePath,
                            Outcome = OperationOutcome.Succeeded,
                            Reason = $"phase:propagate_ct — {a.TotalForwarded} call sites forwarded",
                        });
                    }
                    foreach (var f in ctResult.Failed)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            Outcome = OperationOutcome.Failed,
                            Reason = $"phase:propagate_ct — {f.Reason}",
                        });
                    }
                }
            }

        WriteSummary:;
        }
        catch (OperationCanceledException) when (innerToken.IsCancellationRequested
                                                  && !(cancellationToken?.IsCancellationRequested ?? false))
        {
            stoppedEarly = true;
            if (stopReason.Length == 0)
                stopReason = "maxRuntimeSeconds exceeded mid-phase";
            _logger.LogInformation("Asyncify stopped early: {Reason}", stopReason);
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
            Directive = stoppedEarly
                ? $"stopped_early — {stopReason}. Phases completed are in blob: {blobName2}."
                : status2.Directive,
            BreakerOpen = status2.Open,
        };
    }

    private async Task<BatchResultSummary> HandlerToAsyncCore(
        string? projectName,
        bool dryRun,
        int maxItems,
        bool propagateCancellationTokens,
        Progress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        List<MigrationCandidateFinding> candidates;
        try
        {
            candidates = await _asyncOptimizationEngine.FindMigrationCandidatesAsync(
                filePath: null, projectName: projectName, pattern: "HandlerToAsyncCandidate",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EventHandlersToAsync discovery unexpected exception");
            throw new InvalidOperationException(
                $"EventHandlersToAsync discovery failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        int overLimit = Math.Max(0, candidates.Count - maxItems);
        var toProcess = candidates.Take(maxItems).ToList();

        int succeeded = 0;
        int failed = 0;
        int processed = 0;
        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();

        foreach (var candidate in toProcess)
        {
            if (processed >= maxItems)
            {
                break;
            }

            processed++;
            var methodName = candidate.MethodName;
            var filePath = candidate.FilePath;

            if (dryRun)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = filePath,
                    MethodName = methodName,
                    Outcome = OperationOutcome.Skipped,
                    Reason = "dry_run",
                });
                succeeded++;
                continue;
            }

            try
            {
                string? updatedSource;
                var convertResult = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                    filePath, methodName, progress: progress!, cancellationToken: cancellationToken);
                updatedSource = convertResult.UpdatedText;

                if (propagateCancellationTokens)
                {
                    var asyncMethod = methodName + "Async";
                    var propagationResult = await _asyncOptimizationEngine
                        .PropagateCancellationTokenInMethodAsync(filePath, asyncMethod, progress: progress!, cancellationToken: cancellationToken);
                    if (!string.IsNullOrEmpty(propagationResult.UpdatedText))
                    {
                        updatedSource = propagationResult.UpdatedText;
                    }
                }

                var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                    new Dictionary<FilePath, string> { { filePath, updatedSource } });

                string? beforeSource = null;
                _ = applyResult.PreImages?.TryGetValue(filePath, out beforeSource);

                items.Add(new OperationItemRecord
                {
                    FilePath = filePath,
                    MethodName = methodName,
                    Outcome = OperationOutcome.Succeeded,
                    BeforeSource = beforeSource,
                });
                succeeded++;
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                items.Add(new OperationItemRecord
                {
                    FilePath = filePath,
                    MethodName = methodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = filePath,
                        MethodName = methodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                _logger.LogWarning(
                    "EventHandlersToAsync: {Method} in {File} failed: {Reason}",
                    methodName, filePath, reason);
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: overLimit);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "event_handlers_to_async", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        return new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = overLimit,
            RolledBack = 0,
            Attempted = succeeded + failed + overLimit,
            Failures = failures,
            Severity = status.Severity,
            Directive = status.Directive,
            BreakerOpen = status.Open,
        };
    }

    private async Task<BatchResultSummary> HandlerExtractCore(
        List<HandlerExtractTarget> targets,
        bool dryRun,
        Progress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            return halt;
        }

        int succeeded = 0;
        int failed = 0;
        var items = new List<OperationItemRecord>();
        var failures = new List<FailureDetail>();

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.NewMethodName))
            {
                var reason = $"NewMethodName is required for extract_event_handlers (file: {target.FilePath})";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(target.ContextSnippet) && !target.ExtractEntireBody)
            {
                var reason = $"ContextSnippet is required for extract_event_handlers (file: {target.FilePath})";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                continue;
            }

            MsAugmentResult extractResult;
            try
            {
                extractResult = await _msToolAugmentEngine.ExtractMethodSafeAsync(
                    target.FilePath,
                    target.NewMethodName,
                    target.ContextSnippet,
                    target.LineBefore,
                    target.LineAfter,
                    target.ExtractEntireBody,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                _logger.LogWarning(
                    "ExtractEventHandlers: {Method} in {File} threw: {Reason}",
                    target.NewMethodName, target.FilePath, reason);
                continue;
            }

            if (!extractResult.Success)
            {
                var reason = extractResult.Error ?? "ExtractMethodSafeAsync returned failure with no message";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                _logger.LogWarning(
                    "ExtractEventHandlers: {Method} in {File} failed: {Reason}",
                    target.NewMethodName, target.FilePath, reason);
                continue;
            }

            if (dryRun)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Skipped,
                    Reason = "dry_run",
                });
                succeeded++;
                continue;
            }

            try
            {
                var updatedContent = extractResult.UpdatedContent!;
                var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                    new Dictionary<FilePath, string> { { target.FilePath, updatedContent } });

                string? beforeSource = null;
                _ = applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource);

                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Succeeded,
                    BeforeSource = beforeSource,
                });
                succeeded++;
            }
            catch (Exception ex)
            {
                var reason = $"apply failed: {ex.GetType().Name}: {ex.Message}";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 15)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = OperationOutcome.Failed,
                    });
                }
                failed++;
                _logger.LogWarning(
                    "ExtractEventHandlers: apply changes for {Method} in {File} failed: {Reason}",
                    target.NewMethodName, target.FilePath, reason);
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "extract_event_handlers", changeId, items, _workspaceManager.GetSolutionRoot());
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
}
