using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
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
    private readonly ValidationEngine _validationEngine;
    private readonly FailureRouter _failureRouter;
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
        ValidationEngine validationEngine,
        FailureRouter failureRouter,
        ILogger<SentinelAsyncifyTools> logger)
    {
        _antiPatternEngine = antiPatternEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _asyncBatchEngine = asyncBatchEngine;
        _msToolAugmentEngine = msToolAugmentEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _failureRouter = failureRouter;
        _logger = logger;
    }

    // Score thresholds — used as parameter defaults and referenced in zero-result Directive messages.
    private const int DefaultMinScore = 50;
    private const int DefaultScoreThreshold = 50;

    // ── scan_migration_candidates ─────────────────────────────────────────────

    [McpServerTool(Name = "ScanAsyncMigrationCandidates")]
    [Produces(DataTag.MigrationCandidate)]
    [Description("""
        Step 1 of the bridge workflow — flags qualifying methods with [MigrationCandidate] attributes
        then reports the results. With default parameters, scans the entire solution.

        Set forceRescan=false when running multiple scans back-to-back to skip re-flagging and just
        read existing attributes.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)     ← you are here (flags + reports)
          2. bridge_async_methods(targets: [...])
          3. uplift_callers(targets: SuggestedUpliftTargets)
          4. propagate_cancellation_token(targets: SuggestedPropagateTargets)

        scope: solution (default), project, or file.
          solution — scan entire solution (projectName and filePath ignored).
          project  — restrict to one project; projectName required.
          file     — restrict to a single file; filePath required (flag phase skipped).
        projectName: required when scope=project.
        filePath: optional additional filter — restrict results to a single file (scan only; does not
          affect the flag phase).
        forceRescan: re-evaluate already-flagged methods during the flag phase (default true). Set
          false to skip flagging and just read existing attributes.
        minScore: minimum score to flag and to filter scan results (default 50).
        pattern: null (default) = all patterns.

        summarize=true → guaranteed ≤2KB dashboard.
          MigrationScanSummary fields: ByPattern (count per pattern), ByClass (ClassName, ProjectName,
          Count — sorted desc, capped at 10; ByClassTruncated=true when truncated), ByScoreBucket
          ("<0", "0-25", "26-50", "51-75", "76plus"), TopCandidates (MethodName, ClassName, Pattern,
          Score, Summary — only when topN or minScore set, capped at 5 entries),
          FlagPhase (BatchResultSummary from the internal flag run — null when forceRescan=false).

        summarize=false + limit/offset → full paged List<MigrationCandidateFinding>. minScore filters in
        both modes; TotalRecords reflects post-filter count. A method flagged for two patterns appears twice.
        When results exceed the inline threshold, LargeResultInfo is populated with a scanId for paging.
        """)]
    public async Task<ToolResult<object>> ScanAsyncMigrationCandidates(
        ToolScope scope = ToolScope.solution,
        string? projectName = null,
        string? filePath = null,
        AsyncMigrationPattern? pattern = null,
        bool summarize = false,
        int? topN = null,
        int? minScore = null,
        bool forceRescan = true,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        // ── auto-flag phase (skipped for file scope or when forceRescan=false) ──
        var scopedProjectName = scope == ToolScope.project ? projectName : null;
        var scopedFilePath = scope == ToolScope.file ? filePath : null;
        BatchResultSummary? flagPhaseResult = null;
        if (forceRescan && scope != ToolScope.file)
        {
            var flagInput = new FlagCandidatesInput
            {
                Scope = "project",
                ProjectName = scopedProjectName,
                Pattern = pattern?.ToString() ?? "AsyncBridgeCandidate",
                MinScore = minScore ?? DefaultMinScore,
                ForceRescan = true,
                DryRun = false,
            };
            flagPhaseResult = await FlagMigrationCandidatesCore(flagInput, progress, cancellationToken);
        }

        // ── summarize=true path: always inline, never touches threshold ───
        if (summarize)
        {
            List<MigrationCandidateFinding> summaryFindings;
            try
            {
                summaryFindings = await _asyncOptimizationEngine
                    .FindMigrationCandidatesAsync(scopedFilePath, scopedProjectName, pattern?.ToString());
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

            var buckets = CandidateScoreAnalyzer.ComputeBuckets(aggregateFindings.Select(f => f.Score));

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
                ByClassTruncated: byClassTruncated,
                MinScore: CandidateScoreAnalyzer.ComputeMin(aggregateFindings.Select(f => f.Score)),
                FlagPhase: flagPhaseResult);

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
                    .FindMigrationCandidatesAsync(scopedFilePath, scopedProjectName, pattern?.ToString());
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
        RequestContext<CallToolRequestParams> requestParams = null,
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

    // ── FlagAsyncMigrationCandidates: internal use only (not exposed as MCP tool) ──
    // Flagging is now integrated into ScanAsyncMigrationCandidates (autoFlag=true, default).
    // Use scan_migration_candidates for the standard workflow.
    public async Task<ToolResult<BatchResultSummary>> FlagAsyncMigrationCandidates(
        string scope = "project",
        List<FlagCandidateTarget>? flagTargets = null,
        string? projectName = null,
        AsyncMigrationPattern pattern = AsyncMigrationPattern.AsyncBridgeCandidate,
        int minScore = DefaultMinScore,
        bool dryRun = false,
        bool forceRescan = false,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (scope == "targets" && (flagTargets == null || flagTargets.Count == 0))
            return new ToolResult<BatchResultSummary>
            {
                Success = true,
                Data = new BatchResultSummary
                {
                    Severity = "ok",
                    Directive = $"scope=\"targets\" requires flagTargets to be non-empty — no methods were flagged. " +
                                $"Provide a flagTargets list, or omit scope to use autonomous project-wide discovery (default minScore={DefaultMinScore}).",
                }
            };

        try
        {
            var result = await FlagMigrationCandidatesCore(
                new FlagCandidatesInput
                {
                    Scope = scope,
                    Targets = flagTargets,
                    ProjectName = projectName,
                    Pattern = pattern.ToString(),
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

    // ── remove_migration_candidates ──────────────────────────────────────────

    [McpServerTool(Name = "ClearAsyncMigrationCandidateFlags")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Removes [MigrationCandidate] attributes from methods, optionally filtered by pattern.
        Use before forceRescan to get a completely clean slate, or to undo a flagging run.
        Does NOT delete the MigrationCandidateAttribute.cs helper file — remove that manually
        if you want to remove all traces.

        scope: "project" (default) or "file".
        projectName: restrict removal to one project (scope="project"). Null = entire solution.
        filePath: restrict removal to one file (scope="file").
        pattern: remove only attributes with this pattern string (e.g. "AsyncBridgeCandidate",
                 "NeedsManualReview"). Null or omitted = remove all [MigrationCandidate] attributes.
        dryRun: reports what would be removed without writing files.

        Returns BatchResultSummary. Succeeded = attributes removed. BlobName = full per-method detail.
        """)]
    public async Task<ToolResult<BatchResultSummary>> ClearAsyncMigrationCandidateFlags(
        string scope = "project",
        string? projectName = null,
        string? filePath = null,
        string? pattern = null,
        bool dryRun = false,
        RequestContext<CallToolRequestParams> requestParams = null,
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
            FilePath? resolvedFilePath = null;
            if (scope == "file")
            {
                if (string.IsNullOrEmpty(filePath))
                    return new ToolResult<BatchResultSummary>
                    {
                        Success = false,
                        Error = new ResultError(MigrationErrorCode.InvalidArgument,
                                      "scope=\"file\" requires a filePath.")
                    };
                resolvedFilePath = FilePath.FromWire(filePath, _workspaceManager.GetSolutionRoot());
            }

            var engineResult = await _asyncOptimizationEngine.RemoveMigrationCandidatesAsync(
                projectName: scope == "project" ? projectName : null,
                filePath: resolvedFilePath.HasValue ? (string)resolvedFilePath.Value : null,
                pattern: pattern,
                dryRun: dryRun,
                cancellationToken: cancellationToken);

            if (!dryRun && engineResult.Changes.Count > 0)
                await _workspaceManager.ApplyProposedChangesAsync(engineResult.Changes, validateChanges: true);

            var items = engineResult.Removed.Select(r => new OperationItemRecord
            {
                FilePath = r.FilePath,
                MethodName = r.MethodName,
                Outcome = dryRun ? ItemRecordOutcome.Skipped : ItemRecordOutcome.Succeeded,
                Reason = dryRun ? $"dry_run — would remove [{r.RemovedPattern}]"
                                : $"removed [{r.RemovedPattern}]",
            }).ToList();

            _workspaceManager.RecordBatchOutcome(
                succeeded: dryRun ? 0 : engineResult.TotalRemoved,
                failed: 0, rolledBack: 0, skipped: dryRun ? engineResult.TotalRemoved : 0);

            var changeId = Guid.NewGuid().ToString("N")[..8];
            var blobName = await OperationBlobWriter.WriteAsync(
                "remove_migration_candidates", changeId, items, _workspaceManager.GetSolutionRoot());

            var patternLabel = pattern == null ? "all patterns" : $"pattern={pattern}";
            var directive = engineResult.TotalRemoved == 0
                ? $"No [MigrationCandidate] attributes found for {patternLabel}. Nothing was changed."
                : dryRun
                    ? $"dry_run: {engineResult.TotalRemoved} attribute(s) would be removed from {engineResult.FilesModified} file(s). Re-run with dryRun=false to apply."
                    : $"Removed {engineResult.TotalRemoved} [MigrationCandidate] attribute(s) from {engineResult.FilesModified} file(s). " +
                      $"Run flag_migration_candidates(scope=\"project\", forceRescan=true) to re-score all methods.";

            return new ToolResult<BatchResultSummary>
            {
                Success = true,
                Data = new BatchResultSummary
                {
                    BlobName = blobName,
                    ChangeId = changeId,
                    Succeeded = dryRun ? 0 : engineResult.TotalRemoved,
                    Skipped = dryRun ? engineResult.TotalRemoved : 0,
                    Failed = 0,
                    Attempted = engineResult.TotalRemoved,
                    Severity = "ok",
                    Directive = directive,
                    BreakerOpen = false,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearAsyncMigrationCandidateFlags failed");
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.Exception,
                              $"ClearAsyncMigrationCandidateFlags failed unexpectedly ({ex.GetType().Name}). Details: {ex.Message}")
            };
        }
    }

    // ── bridge_async_methods ──────────────────────────────────────────────────

    [McpServerTool(Name = "BridgeAsyncMethods")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Step 2 of the bridge workflow — converts each named method to the Asyncify-bridge pattern:
        a sync wrapper that delegates to an async overload.
        Use the asyncify macro to run all steps automatically.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)   — flags + reports
          2. bridge_async_methods                         ← you are here
          3. uplift_callers(targets: SuggestedUpliftTargets)
          4. propagate_cancellation_token(targets: SuggestedPropagateTargets)

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
            BridgedMethodName; SymbolId is null (enrich from ObsoleteCallerFinding.SymbolId when
            disambiguation is needed). Empty when Succeeded=0.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        IMPORTANT: targets must be non-empty. An empty list is a no-op — no methods are processed.
        Call scan_migration_candidates(summarize: true) first to discover and flag candidates.
        PREFER asyncify macro: use individual tools only when manual step-by-step control is required.
        """)]
    public async Task<ToolResult<BridgeAsyncMethodsResult>> BridgeAsyncMethods(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        bool propagateCancellationTokens = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BridgeAsyncMethodsResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (targets == null || targets.Count == 0)
            return new ToolResult<BridgeAsyncMethodsResult>
            {
                Success = true,
                Data = new BridgeAsyncMethodsResult
                {
                    Summary = new BatchResultSummary { Directive = "targets was empty — no methods processed. Call scan_migration_candidates(summarize: true) first to discover and flag candidates." },
                    SuggestedUpliftTargets = new List<UpliftTarget>()
                }
            };

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
        Step 3 of the bridge workflow — updates sync callers of each bridge wrapper to call the async
        overload directly. Pass SuggestedUpliftTargets from bridge_async_methods as targets.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)   — flags + reports
          2. bridge_async_methods(targets: [...])
          3. uplift_callers                               ← you are here
          4. propagate_cancellation_token(targets: SuggestedPropagateTargets)

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
        IMPORTANT: targets must be non-empty. An empty list is a no-op — no callers are uplifted.
        Pass SuggestedUpliftTargets from bridge_async_methods as targets.
        PREFER asyncify macro: use individual tools only when manual step-by-step control is required.
        """)]
    public async Task<ToolResult<UpliftCallersResult>> UpliftCallers(
        List<UpliftTarget> targets,
        bool dryRun = false,
        int maxCallersPerMethod = 10,
        bool propagateCancellationTokens = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<UpliftCallersResult>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (targets == null || targets.Count == 0)
            return new ToolResult<UpliftCallersResult>
            {
                Success = true,
                Data = new UpliftCallersResult
                {
                    Summary = new BatchResultSummary { Directive = "targets was empty — no callers uplifted. Pass SuggestedUpliftTargets from bridge_async_methods as targets, or use the asyncify macro." },
                    SuggestedPropagateTargets = new List<BatchTarget>()
                }
            };

        try
        {
            (BatchResultSummary summary, List<BatchTarget> suggestedPropagateTargets, OperationSummary operationSummary) = await UpliftCallersCore(
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
                    OperationSummary = operationSummary,
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
        Step 4 of the bridge workflow — threads CancellationToken through async call chains in the
        specified files. Pass SuggestedPropagateTargets from uplift_callers as targets.
        Also usable standalone to clean up CT forwarding in any set of files.

        Full bridge workflow:
          1. scan_migration_candidates(summarize: true)   — flags + reports
          2. bridge_async_methods(targets: [...])
          3. uplift_callers(targets: [...])
          4. propagate_cancellation_token                  ← you are here
               targets: SuggestedPropagateTargets from uplift_callers — no transformation required.

        targets: list of { FilePath, MethodNames? }. null MethodNames = all eligible methods in the file.
          Pass SuggestedPropagateTargets from uplift_callers directly.
        dryRun: computes without writing files.
        maxItems: max files to process (default 100).

        Returns BatchResultSummary. BlobName = full per-file detail on disk.
        Use get_operation_detail(changeId) for details.
        Severity="halt" → breaker open; call get_breaker_status then reset_breaker.
        IMPORTANT: targets must be non-empty. An empty list is a no-op — no files are processed.
        Pass SuggestedPropagateTargets from uplift_callers as targets, or specify files explicitly.
        PREFER asyncify macro: use individual tools only when manual step-by-step control is required.
        """)]
    public async Task<ToolResult<BatchResultSummary>> PropagateCancellationToken(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (targets == null || targets.Count == 0)
            return new ToolResult<BatchResultSummary>
            {
                Success = true,
                Data = new BatchResultSummary { Directive = "targets was empty — no files processed. Pass SuggestedPropagateTargets from uplift_callers as targets, or specify files explicitly. Prefer the asyncify macro." }
            };

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
    [Produces(DataTag.CancellationTokenSlot, Preference = 100)]
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
        IMPORTANT: targets must be non-empty. An empty list is a no-op — no files are processed.
        Specify the files (FilePath) where CancellationToken parameters should be added.
        PREFER asyncify macro: use individual tools only when manual step-by-step control is required.
        """)]
    public async Task<ToolResult<BatchResultSummary>> AddCancellationToken(
        List<BatchTarget> targets,
        bool dryRun = false,
        int maxItems = 100,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (targets == null || targets.Count == 0)
            return new ToolResult<BatchResultSummary>
            {
                Success = true,
                Data = new BatchResultSummary { Directive = "targets was empty — no files processed. Specify the files (FilePath) where CancellationToken parameters should be added. Prefer the asyncify macro." }
            };

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
        Targeted extraction — extracts a nominated code block from inside a method into a new private
        method using semantic analysis. Produces the correct return type (fixes the standard
        extract_method bug where selections ending with 'return expr' produce void).

        The asyncify macro (Phase 0) automates full-body extraction for HandlerExtractCandidate
        methods. Use this tool when you need a custom extracted method name, partial-body (snippet)
        extraction, or targeted one-off extraction outside the macro workflow.

        Event handler path (manual):
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
        IMPORTANT: targets must be non-empty. An empty list is a no-op — no handlers are extracted.
        Call scan_migration_candidates(pattern: "HandlerExtractCandidate") to find candidates.
        PREFER asyncify macro (Phase 0 auto-extracts HandlerExtractCandidate methods): use this tool
        only for custom extracted method names, partial-body extraction, or one-off targeted extraction.
        """)]
    public async Task<ToolResult<BatchResultSummary>> ExtractEventHandlers(
        List<HandlerExtractTarget> targets,
        bool dryRun = false,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        if (_workspaceManager.CurrentSolution == null)
        {
            return new ToolResult<BatchResultSummary>
            {
                Success = false,
                Error = new ResultError(MigrationErrorCode.SolutionNotLoaded,
                              "No solution is loaded. Call load_solution first.")
            };
        }

        if (targets == null || targets.Count == 0)
            return new ToolResult<BatchResultSummary>
            {
                Success = true,
                Data = new BatchResultSummary { Directive = "targets was empty — no handlers extracted. Call scan_migration_candidates(pattern: \"HandlerExtractCandidate\") to find candidates, then pass them as targets. Prefer the asyncify macro for auto-extraction." }
            };

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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

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
          Phase 0 (Extract)    — extracts the entire body of each HandlerExtractCandidate event
                                 handler into a new private method (name = PascalCase of the
                                 handler name, e.g. "button1_Click" → "Button1Click"). Uses
                                 ExtractEntireBody so no ContextSnippet or manual input is needed.
                                 The extracted method is then picked up by Phase 3a.
          Phase 1 (Flag)       — discovers qualifying sync methods and flags
                                 [MigrationCandidate("AsyncBridgeCandidate")].
                                 Skipped when methodTargets is provided.
          Phase 2 (Bridge)     — converts flagged methods to the Asyncify-bridge pattern.
          Phase 3 (Uplift)     — uplifts callers of each bridge wrapper to the async overload.
          Phase 3a (HandlerBridge) — bridges HandlerToAsyncCandidate methods (extracted event
                                 handler bodies from Phase 0, or pre-existing flags). After
                                 bridging, their event-handler callers become AsyncHandlerCandidate
                                 and are picked up by Phase 3b.
          Phase 3b (Handler)   — converts AsyncHandlerCandidate event handlers in-place to
                                 async void (replaces bridge calls with await asyncCall()).
          Phase 4 (Propagate)  — propagates CancellationToken in all bridged/handler files.
                                 Skipped when propagateCancellationTokens=false or dryRun=true.

        extract_event_handlers remains available for targeted extraction with custom method names
        or snippet-based (partial-body) extraction not covered by Phase 0.

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
        scoreThreshold: min score eligible for bridge conversion in Phase 2 (default 60). Only methods
          scoring ≥ scoreThreshold are bridged. Raise to focus on highest-impact candidates; lower to
          include more. Use MinCandidateScore from a prior run to calibrate.
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
        int minScore = DefaultMinScore,
        int scoreThreshold = DefaultScoreThreshold,
        int maxRuntimeSeconds = 0,
        int maxIterations = 0,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams.Params?.ProgressToken ?? new ProgressToken();
        IProgress<string> progress = new Progress<string>(msg => requestParams.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

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
        IProgress<string>? progress,
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
                $"PropagateCancellationToken failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}", ex);
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
                Outcome = a.TotalForwarded > 0 ? ItemRecordOutcome.Succeeded : ItemRecordOutcome.Skipped,
                Reason = a.TotalForwarded == 0 ? "no eligible call sites" : null,
            });
        }
        foreach (var f in result.Failed)
        {
            items.Add(new OperationItemRecord { FilePath = f.FilePath, Outcome = ItemRecordOutcome.Failed, Reason = f.Reason });
        }

        var blobName = await OperationBlobWriter.WriteAsync(
            "propagate_cancellation_token", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();
        var failures = result.Failed
            .Take(15)
            .Select(f => new FailureDetail { FilePath = f.FilePath, Reason = f.Reason, Outcome = ItemRecordOutcome.Failed })
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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(input.DryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };
    }

    private async Task<(BatchResultSummary Summary, List<UpliftTarget> SuggestedUpliftTargets)> BridgeAsyncMethodsCore(
        BatchTargetInput input,
        bool propagateCancellationTokens = true,
        IProgress<string>? progress = null,
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
                    Outcome = ItemRecordOutcome.Failed,
                };
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = ItemRecordOutcome.Failed, Reason = fd.Reason });
                if (failures.Count < 10) { failures.Add(fd); }
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
                        Outcome = ItemRecordOutcome.Skipped,
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

                    var bridgeValidation = await _validationEngine.ValidateChangesAsync(
                        new Dictionary<FilePath, string> { { target.FilePath, updatedSource } },
                        cancellationToken: cancellationToken);
                    if (!bridgeValidation.Success)
                    {
                        var diagMsg = string.Join("; ", bridgeValidation.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                        var reason782 = $"Validation: {bridgeValidation.Diagnostics.Count} error(s) — {diagMsg}";
                        items.Add(new OperationItemRecord
                        {
                            FilePath = target.FilePath,
                            MethodName = methodName,
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = reason782,
                            CompilerDiagnostics = bridgeValidation.Diagnostics,
                        });
                        failures.Add(new FailureDetail { FilePath = target.FilePath, MethodName = methodName, Reason = reason782, Outcome = ItemRecordOutcome.Failed, CompilerDiagnostics = bridgeValidation.Diagnostics });
                        failed++;
                        continue;
                    }

                    var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<FilePath, string> { { target.FilePath, updatedSource } }, validateChanges: true);

                    string? beforeSource782 = null;
                    applyResult.PreImages?.TryGetValue(target.FilePath, out beforeSource782);

                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        MethodName = methodName,
                        Outcome = ItemRecordOutcome.Succeeded,
                        BeforeSource = beforeSource782,
                    });
                    succeeded++;
                }
                catch (Exception ex)
                {
                    string reason = ex.Message;
                    bool handled = false;
                    List<DiagnosticInfo>? compilerDiagnostics = null;

                    if (ex is InvalidOperationException && ex.Message.Contains("already exists"))
                    {
                        var asyncMethodName = methodName + "Async";
                        try
                        {
                            var ctResult = await _asyncOptimizationEngine.AddCancellationTokenToMethodAsync(
                                target.FilePath, asyncMethodName);
                            if (ctResult.Outcome == EditOutcome.Modified && ctResult.UpdatedText != null)
                            {
                                var ctApplyResult = await _workspaceManager.ApplyProposedChangesAsync(
                                    new Dictionary<FilePath, string> { { target.FilePath, ctResult.UpdatedText } },
                                    validateChanges: true);
                                if (ctApplyResult.Success)
                                {
                                    string? beforeSrc = null;
                                    ctApplyResult.PreImages?.TryGetValue(target.FilePath, out beforeSrc);
                                    items.Add(new OperationItemRecord
                                    {
                                        FilePath = target.FilePath,
                                        MethodName = methodName,
                                        Outcome = ItemRecordOutcome.Succeeded,
                                        Reason = $"CT added to existing async overload '{asyncMethodName}'",
                                        BeforeSource = beforeSrc,
                                    });
                                    succeeded++;
                                    handled = true;
                                }
                                else if (ctApplyResult.ValidationResult != null)
                                {
                                    var diagMsg = string.Join("; ", ctApplyResult.ValidationResult.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                                    reason = $"Validation: {ctApplyResult.ValidationResult.Diagnostics.Count} error(s) — {diagMsg}";
                                    compilerDiagnostics = ctApplyResult.ValidationResult.Diagnostics;
                                }
                            }
                            else if (ctResult.Outcome == EditOutcome.NoChange)
                            {
                                items.Add(new OperationItemRecord
                                {
                                    FilePath = target.FilePath,
                                    MethodName = methodName,
                                    Outcome = ItemRecordOutcome.Skipped,
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
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = reason,
                            CompilerDiagnostics = compilerDiagnostics,
                        });
                        if (failures.Count < 10)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = target.FilePath,
                                MethodName = methodName,
                                Reason = reason,
                                Outcome = ItemRecordOutcome.Failed,
                                CompilerDiagnostics = compilerDiagnostics,
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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(input.DryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };

        var suggestedUpliftTargets = items
            .Where(i => (i.Outcome == ItemRecordOutcome.Succeeded ||
                         (i.Outcome == ItemRecordOutcome.Skipped && i.Reason == "dry_run"))
                        && i.MethodName != null)
            .Select(i => new UpliftTarget { BridgedMethodName = i.MethodName! })
            .DistinctBy(t => t.BridgedMethodName)
            .ToList();

        return (summary, suggestedUpliftTargets);
    }

    private async Task<BatchResultSummary> AddCancellationTokenCore(
        BatchTargetInput input,
        IProgress<string>? progress,
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
                    items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = ItemRecordOutcome.Failed, Reason = reason });
                    if (failures.Count < 10)
                    {
                        failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = ItemRecordOutcome.Failed });
                    }
                    failed++;
                }
                else if (modified.Count == 0)
                {
                    items.Add(new OperationItemRecord
                    {
                        FilePath = target.FilePath,
                        Outcome = ItemRecordOutcome.Skipped,
                        Reason = "no eligible async methods",
                    });
                    skipped++;
                }
                else
                {
                    if (!input.DryRun)
                    {
                        var ctFileValidation = await _validationEngine.ValidateChangesAsync(
                            new Dictionary<FilePath, string> { { target.FilePath, updatedSource } },
                            cancellationToken: cancellationToken);
                        if (!ctFileValidation.Success)
                        {
                            var diagMsg = string.Join("; ", ctFileValidation.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                            var valReason = $"Validation: {ctFileValidation.Diagnostics.Count} error(s) — {diagMsg}";
                            items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = ItemRecordOutcome.Failed, Reason = valReason, CompilerDiagnostics = ctFileValidation.Diagnostics });
                            if (failures.Count < 10)
                                failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = valReason, Outcome = ItemRecordOutcome.Failed, CompilerDiagnostics = ctFileValidation.Diagnostics });
                            failed++;
                            continue;
                        }
                        allChanges[target.FilePath] = updatedSource;
                    }

                    foreach (var m in modified)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = target.FilePath,
                            MethodName = m,
                            Outcome = input.DryRun ? ItemRecordOutcome.Skipped : ItemRecordOutcome.Succeeded,
                            Reason = input.DryRun ? "dry_run" : null,
                        });
                    }

                    succeeded++;
                }
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                items.Add(new OperationItemRecord { FilePath = target.FilePath, Outcome = ItemRecordOutcome.Failed, Reason = reason });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail { FilePath = target.FilePath, Reason = reason, Outcome = ItemRecordOutcome.Failed });
                }
                failed++;
                _logger.LogWarning("AddCancellationToken: {File} failed: {Reason}", target.FilePath, reason);
            }
        }

        if (allChanges.Count > 0)
        {
            var applyResult941 = await _workspaceManager.ApplyProposedChangesAsync(allChanges, validateChanges: true);
            if (applyResult941.PreImages != null)
            {
                foreach (var item in items)
                {
                    if (item.Outcome == ItemRecordOutcome.Succeeded && item.BeforeSource == null)
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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(input.DryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };
    }

    private async Task<(BatchResultSummary Summary, List<BatchTarget> SuggestedPropagateTargets, OperationSummary OperationSummary)> UpliftCallersCore(
        RunUpliftInput input,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        BatchResultSummary? halt = _workspaceManager.CheckBreaker();
        if (halt != null)
        {
            OperationSummary haltSummary = OperationSummary.FromCounts(
                blobName: "",
                changeId: "",
                succeeded: 0,
                alreadySatisfied: 0,
                skipped: 0,
                failed: 0,
                blocked: 0,
                attempted: 0,
                actionable: Array.Empty<ItemFailure>(),
                actionableTruncated: false,
                directive: halt.Directive,
                breakerOpen: true);
            return (halt, new List<BatchTarget>(), haltSummary);
        }

        UpliftBatchMultiInput multiInput = new UpliftBatchMultiInput
        {
            Targets = input.Targets.Select(t => new UpliftBatchMultiTarget
            {
                BridgedMethodName = t.BridgedMethodName,
                ProjectName = t.ProjectName,
                SymbolId = t.SymbolId,
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
                $"UpliftCallers failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}", ex);
        }

        // ── Classify each item into ItemOutcome ────────────────────────────────

        string changeId = Guid.NewGuid().ToString("N")[..8];
        List<OperationItemRecord> items = new List<OperationItemRecord>();

        int succeeded = 0;
        int alreadySatisfied = 0;
        int failed = 0;
        int blocked = 0;
        List<ItemFailure> actionable = new List<ItemFailure>();

        foreach (UpliftBatchMultiMethodResult pm in result.PerMethod)
        {
            string projectName = pm.ProjectName ?? "";

            foreach (UpliftCallerInfo u in pm.Result.Uplifted)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = u.FilePath,
                    MethodName = u.CallerMethod,
                    Outcome = ItemRecordOutcome.Succeeded,
                });
                succeeded++;
            }

            foreach (UpliftSkippedInfo s in pm.Result.Skipped)
            {
                (ItemOutcome itemOutcome, FailureReason failureReason) = ClassifyUpliftSkipReason(s.Reason);

                ItemRecordOutcome blobOutcome = itemOutcome == ItemOutcome.AlreadySatisfied
                    ? ItemRecordOutcome.Skipped
                    : ItemRecordOutcome.Failed;

                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.CallerMethod,
                    Outcome = blobOutcome,
                    Reason = s.Reason,
                    CompilerDiagnostics = s.Diagnostics.Count > 0 ? s.Diagnostics : null,
                });

                if (itemOutcome == ItemOutcome.AlreadySatisfied)
                {
                    alreadySatisfied++;
                }
                else
                {
                    ItemContext ctx = new ItemContext
                    {
                        FilePath = s.FilePath,
                        MethodName = s.CallerMethod,
                        ProjectName = projectName,
                        ChangeId = changeId,
                    };
                    ToolHint? hint = _failureRouter.Route(failureReason, ctx);

                    if (actionable.Count < 15)
                    {
                        actionable.Add(new ItemFailure
                        {
                            FilePath = s.FilePath,
                            MethodName = s.CallerMethod,
                            Outcome = itemOutcome,
                            Reason = failureReason,
                            Detail = s.Reason,
                            SuggestedTool = hint,
                        });
                    }

                    if (itemOutcome == ItemOutcome.Blocked)
                    {
                        blocked++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            if (pm.Error != null)
            {
                items.Add(new OperationItemRecord
                {
                    FilePath = pm.BridgedMethodName,
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = pm.Error,
                });
                if (actionable.Count < 15)
                {
                    ItemContext ctx = new ItemContext
                    {
                        FilePath = pm.BridgedMethodName,
                        MethodName = "",
                        ProjectName = pm.ProjectName ?? "",
                        ChangeId = changeId,
                    };
                    actionable.Add(new ItemFailure
                    {
                        FilePath = pm.BridgedMethodName,
                        MethodName = "",
                        Outcome = ItemOutcome.Failed,
                        Reason = FailureReason.SymbolNotResolved,
                        Detail = pm.Error,
                        SuggestedTool = _failureRouter.Route(FailureReason.SymbolNotResolved, ctx),
                    });
                }
                failed++;
            }
        }

        int attempted = succeeded + alreadySatisfied + failed + blocked;
        bool actionableTruncated = (failed + blocked) > actionable.Count;

        // AlreadySatisfied and Skipped are excluded from the failure rate that drives severity/breaker.
        _workspaceManager.RecordBatchOutcome(succeeded, failed + blocked, rolledBack: 0, skipped: alreadySatisfied);

        string blobName = await OperationBlobWriter.WriteAsync(
            "uplift_callers", changeId, items, _workspaceManager.GetSolutionRoot());

        BreakerStatusReport status = _workspaceManager.GetBreakerStatus();

        // ── Legacy BatchResultSummary (wire shape unchanged) ───────────────────

        List<FailureDetail> legacyFailures = actionable
            .Select(f => new FailureDetail
            {
                FilePath = f.FilePath,
                MethodName = f.MethodName,
                Reason = f.Detail,
                Outcome = ItemRecordOutcome.Failed,
            })
            .Take(15)
            .ToList();

        BatchResultSummary summary = new BatchResultSummary
        {
            ChangeId = changeId,
            BlobName = blobName,
            Succeeded = succeeded,
            Failed = failed + blocked,
            Skipped = alreadySatisfied,
            RolledBack = 0,
            Attempted = attempted,
            Failures = legacyFailures,
            FailuresTruncated = (failed + blocked) > 10,
            FailuresByReason = (failed + blocked) > 10
                ? legacyFailures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count())
                : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(input.DryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };

        // ── OperationSummary (structured routing) ──────────────────────────────

        OperationOutcome outcome = OperationSummary.DeriveOutcome(succeeded, alreadySatisfied, 0, failed, blocked, attempted);
        string directive = BuildUpliftDirective(outcome, succeeded, alreadySatisfied, failed, blocked, status);

        OperationSummary operationSummary = OperationSummary.FromCounts(
            blobName: blobName,
            changeId: changeId,
            succeeded: succeeded,
            alreadySatisfied: alreadySatisfied,
            skipped: 0,
            failed: failed,
            blocked: blocked,
            attempted: attempted,
            actionable: actionable,
            actionableTruncated: actionableTruncated,
            directive: directive,
            breakerOpen: status.Open);

        List<BatchTarget> suggestedPropagateTargets = result.PerMethod
            .SelectMany(pm => pm.Result.Uplifted.Select(u => u.FilePath))
            .Distinct()
            .Select(fp => new BatchTarget { FilePath = fp })
            .ToList();

        return (summary, suggestedPropagateTargets, operationSummary);
    }

    private static (ItemOutcome Outcome, FailureReason Reason) ClassifyUpliftSkipReason(string reason)
    {
        // Already in target state — not actionable, excluded from failure rate
        if (reason.Contains("already async", StringComparison.OrdinalIgnoreCase)
         || reason.Contains("already has CancellationToken", StringComparison.OrdinalIgnoreCase))
        {
            return (ItemOutcome.AlreadySatisfied, FailureReason.AlreadyAsync);
        }

        // Async overload exists but lacks CT — blocked, routable via AddCancellationToken
        if (reason.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return (ItemOutcome.Blocked, FailureReason.OverloadAlreadyExists);
        }

        // File or symbol not found
        if (reason.Contains("file not found", StringComparison.OrdinalIgnoreCase)
         || reason.Contains("not found on disk", StringComparison.OrdinalIgnoreCase))
        {
            return (ItemOutcome.Failed, FailureReason.SymbolNotResolved);
        }

        // Transform produced compiler errors
        if (reason.Contains("Validation produced", StringComparison.OrdinalIgnoreCase)
         || reason.Contains("compiler error", StringComparison.OrdinalIgnoreCase))
        {
            return (ItemOutcome.Failed, FailureReason.CompilerErrorAfterTransform);
        }

        return (ItemOutcome.Failed, FailureReason.Unknown);
    }

    private static string BuildUpliftDirective(
        OperationOutcome outcome,
        int succeeded,
        int alreadySatisfied,
        int failed,
        int blocked,
        BreakerStatusReport status)
    {
        if (status.Open)
        {
            return status.Directive;
        }

        string base_ = outcome switch
        {
            OperationOutcome.CompletedFully =>
                $"{succeeded} caller(s) uplifted successfully.",
            OperationOutcome.CompletedWithNoOps =>
                succeeded > 0
                    ? $"{succeeded} caller(s) uplifted; {alreadySatisfied} were already in the target state."
                    : $"All {alreadySatisfied} caller(s) were already in the target state — nothing to do.",
            OperationOutcome.PartialProgress =>
                $"{succeeded} caller(s) uplifted; {failed + blocked} could not be completed. Review Actionable for next steps.",
            OperationOutcome.NoProgress =>
                $"No callers were uplifted; {failed + blocked} failure(s) require attention. Review Actionable for next steps.",
            OperationOutcome.NothingToDo =>
                "No callers found to uplift.",
            _ =>
                ""
        };

        string breakerSuffix = string.IsNullOrEmpty(status.Directive) ? "" : " " + status.Directive;
        return base_ + breakerSuffix;
    }

    private async Task<BatchResultSummary> FlagMigrationCandidatesCore(
        FlagCandidatesInput input,
        IProgress<string>? progress,
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
        int? minCandidateScore = null;

        try
        {
            if (input.Scope == "project")
            {
                var engineResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, input.Pattern, input.MinScore, input.DryRun, input.ForceRescan);

                if (!input.DryRun && engineResult.Changes.Count > 0)
                {
                    var applyResult1126 = await _workspaceManager.ApplyProposedChangesAsync(
                        engineResult.Changes, validateChanges: true);
                    if (!applyResult1126.Success && applyResult1126.ValidationResult != null)
                        _logger.LogWarning("FlagMigrationCandidates: validation found {Count} error(s) in attribute changes — skipping write",
                            applyResult1126.ValidationResult.Diagnostics.Count);

                    foreach (var f in engineResult.Flagged)
                    {
                        string? beforeSource1135 = null;
                        applyResult1126.PreImages?.TryGetValue(f.FilePath, out beforeSource1135);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = ItemRecordOutcome.Succeeded,
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
                            Outcome = input.DryRun ? ItemRecordOutcome.Skipped : ItemRecordOutcome.Succeeded,
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
                        Outcome = ItemRecordOutcome.Skipped,
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
                        Outcome = ItemRecordOutcome.Skipped,
                        Reason = "already flagged",
                    });
                    skipped++;
                }

                minCandidateScore = CandidateScoreAnalyzer.ComputeMin(engineResult.Flagged.Select(f => f.Score));
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
                        Outcome = ItemRecordOutcome.Succeeded,
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
                        Outcome = ItemRecordOutcome.Failed,
                        Reason = err,
                    });
                    if (failures.Count < 10)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = tgt.FilePath,
                            MethodName = tgt.MethodName,
                            Reason = err,
                            Outcome = ItemRecordOutcome.Failed,
                        });
                    }
                    failed++;
                }

                if (allChanges.Count > 0 && !input.DryRun)
                {
                    var applyResult1223 = await _workspaceManager.ApplyProposedChangesAsync(
                        allChanges, validateChanges: true);
                    if (!applyResult1223.Success && applyResult1223.ValidationResult != null)
                        _logger.LogWarning("FlagMigrationCandidates: validation found {Count} error(s) in attribute changes — skipping write",
                            applyResult1223.ValidationResult.Diagnostics.Count);
                    if (applyResult1223.PreImages != null)
                    {
                        foreach (var item in items)
                        {
                            if (item.Outcome == ItemRecordOutcome.Succeeded && item.BeforeSource == null)
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
                $"FlagMigrationCandidates failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName = await OperationBlobWriter.WriteAsync(
            "flag_migration_candidates", changeId, items, _workspaceManager.GetSolutionRoot());
        var status = _workspaceManager.GetBreakerStatus();

        var flagDirective = status.Open ? status.Directive
            : succeeded > 0 ? WriteStatusNote(input.DryRun, succeeded) + status.Directive
            : skipped > 0
                ? $"No methods were flagged — {skipped} candidate(s) were skipped (scored below minScore={input.MinScore} or already flagged). " +
                  $"Default minScore is {DefaultMinScore}. Lower minScore or use forceRescan=true to re-evaluate existing flags."
                : $"No methods in the solution qualified for flagging at minScore={input.MinScore}. " +
                  $"Try lowering minScore (e.g., minScore=25), or run forceRescan=true to re-evaluate already-flagged methods.";

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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = flagDirective,
            BreakerOpen = status.Open,
            MinCandidateScore = minCandidateScore,
        };
    }

    private async Task<BatchResultSummary> AsyncifyCore(
        AsyncifyInput input,
        IProgress<string>? progress,
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (input.MaxRuntimeSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(input.MaxRuntimeSeconds));

        var innerToken = cts.Token;
        int iterationsUsed = 0;
        bool stoppedEarly = false;
        string stopReason = "";
        int? bridgeMinScore = null;
        string bridgeStopReason = "";
        int bridgeSkippedCount = 0;
        int bridgeRemainingCandidates = 0;
        int flagPhaseScanned = 0;
        int flagPhaseNewFlags = 0;
        var handlerConvertedFiles = new List<FilePath>();
        var handlerBridgedFiles = new List<FilePath>();

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
            // ── Phase 0: Extract HandlerExtractCandidate event handler bodies ──
            // Event handlers flagged HandlerExtractCandidate have async-eligible business logic
            // inline. Extract the entire body into a new private method (PascalCase name derived
            // from the handler name) so Phase 3a can bridge it. ExtractEntireBody=true means the
            // method name from the scan finding is the only input needed — no ContextSnippet.
            {
                List<MigrationCandidateFinding> extractCandidates;
                try
                {
                    extractCandidates = await _asyncOptimizationEngine.FindMigrationCandidatesAsync(
                        filePath: null, projectName: input.ProjectName, pattern: "HandlerExtractCandidate",
                        cancellationToken: innerToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AsyncifyCore Phase 0: HandlerExtractCandidate discovery failed — skipping");
                    extractCandidates = new List<MigrationCandidateFinding>();
                }

                var extractTargets = extractCandidates
                    .Where(h => input.Exclusions?.Contains(h.MethodName) != true)
                    .ToList();

                foreach (var candidate in extractTargets)
                {
                    CheckIterations(1);
                    if (innerToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        if (stopReason.Length == 0) stopReason = "maxRuntimeSeconds exceeded during handler-extract phase";
                        goto WriteSummary;
                    }

                    var newMethodName = ToPascalCase(candidate.MethodName);

                    if (input.DryRun)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Skipped,
                            Reason = $"dry_run phase:handler_extract → would extract to '{newMethodName}'",
                        });
                        succeeded++;
                        continue;
                    }

                    try
                    {
                        var extractResult = await _msToolAugmentEngine.ExtractMethodSafeAsync(
                            candidate.FilePath,
                            newMethodName,
                            candidate.MethodName,
                            lineBefore: null,
                            lineAfter: null,
                            extractEntireBody: true,
                            ct: innerToken);

                        if (!extractResult.Success)
                        {
                            var reason = extractResult.Error ?? "extraction returned no error message";
                            items.Add(new OperationItemRecord
                            {
                                FilePath = candidate.FilePath,
                                MethodName = candidate.MethodName,
                                Outcome = ItemRecordOutcome.Failed,
                                Reason = reason,
                            });
                            if (failures.Count < 10)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath = candidate.FilePath,
                                    MethodName = candidate.MethodName,
                                    Reason = reason,
                                    Outcome = ItemRecordOutcome.Failed,
                                });
                            }
                            failed++;
                            continue;
                        }

                        var extractValidation = await _validationEngine.ValidateChangesAsync(
                            new Dictionary<FilePath, string> { { candidate.FilePath, extractResult.UpdatedContent! } },
                            cancellationToken: innerToken);
                        if (!extractValidation.Success)
                        {
                            var diagMsg = string.Join("; ", extractValidation.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                            var valReason = $"Validation: {extractValidation.Diagnostics.Count} error(s) — {diagMsg}";
                            items.Add(new OperationItemRecord { FilePath = candidate.FilePath, MethodName = candidate.MethodName, Outcome = ItemRecordOutcome.Failed, Reason = valReason, CompilerDiagnostics = extractValidation.Diagnostics });
                            if (failures.Count < 10) failures.Add(new FailureDetail { FilePath = candidate.FilePath, MethodName = candidate.MethodName, Reason = valReason, Outcome = ItemRecordOutcome.Failed, CompilerDiagnostics = extractValidation.Diagnostics });
                            failed++;
                            continue;
                        }

                        await _workspaceManager.ApplyProposedChangesAsync(
                            new Dictionary<FilePath, string> { { candidate.FilePath, extractResult.UpdatedContent! } },
                            validateChanges: true);

                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Succeeded,
                            Reason = $"phase:handler_extract → '{newMethodName}'",
                        });
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AsyncifyCore Phase 0: extraction failed for '{Method}'", candidate.MethodName);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = ex.Message,
                        });
                        if (failures.Count < 10)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = candidate.FilePath,
                                MethodName = candidate.MethodName,
                                Reason = ex.Message,
                                Outcome = ItemRecordOutcome.Failed,
                            });
                        }
                        failed++;
                    }
                }
            }

            // ── Phase 1: Flag ─────────────────────────────────────────────────
            if (input.MethodTargets == null || input.MethodTargets.Count == 0)
            {
                var flagResult = await _asyncOptimizationEngine.FlagCandidatesInProjectAsync(
                    input.ProjectName, "AsyncBridgeCandidate", input.MinScore,
                    input.DryRun, forceRescan: false);

                flagPhaseNewFlags = flagResult.Flagged.Count;
                flagPhaseScanned = flagResult.Flagged.Count + flagResult.Skipped.Count + flagResult.AlreadyFlagged.Count;

                if (!input.DryRun && flagResult.Changes.Count > 0)
                {
                    var applyResult1317 = await _workspaceManager.ApplyProposedChangesAsync(
                        flagResult.Changes, validateChanges: true);
                    if (!applyResult1317.Success && applyResult1317.ValidationResult != null)
                        _logger.LogWarning("AsyncifyCore Phase 1: validation found {Count} error(s) in flag attribute changes — skipping write",
                            applyResult1317.ValidationResult.Diagnostics.Count);

                    foreach (var f in flagResult.Flagged)
                    {
                        if (input.Exclusions?.Contains(f.MethodName) == true)
                        {
                            items.Add(new OperationItemRecord
                            {
                                FilePath = f.FilePath,
                                MethodName = f.MethodName,
                                Outcome = ItemRecordOutcome.Skipped,
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
                            Outcome = ItemRecordOutcome.Succeeded,
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
                                Outcome = ItemRecordOutcome.Skipped,
                                Reason = "excluded",
                            });
                            skipped++;
                            continue;
                        }

                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            MethodName = f.MethodName,
                            Outcome = ItemRecordOutcome.Skipped,
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
                        Outcome = ItemRecordOutcome.Skipped,
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
                        Outcome = ItemRecordOutcome.Skipped,
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
                            Outcome = ItemRecordOutcome.Succeeded,
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
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = $"phase:flag — {err}",
                        });
                        if (failures.Count < 10)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = tuples[idx].FilePath,
                                MethodName = tuples[idx].MethodName,
                                Reason = err,
                                Outcome = ItemRecordOutcome.Failed,
                            });
                        }
                        failed++;
                    }
                    if (!input.DryRun && allChanges.Count > 0)
                    {
                        var applyResult1421 = await _workspaceManager.ApplyProposedChangesAsync(
                            allChanges, validateChanges: true);
                        if (!applyResult1421.Success && applyResult1421.ValidationResult != null)
                            _logger.LogWarning("AsyncifyCore Phase 1: validation found {Count} error(s) in explicit-target flag changes — skipping write",
                                applyResult1421.ValidationResult.Diagnostics.Count);
                        if (applyResult1421.PreImages != null)
                        {
                            foreach (var item in items)
                            {
                                if (item.Outcome == ItemRecordOutcome.Succeeded && item.BeforeSource == null)
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

            bridgeMinScore = bridgeResult.MinCandidateScore;
            bridgeStopReason = bridgeResult.StopReason;
            bridgeSkippedCount = bridgeResult.Skipped.Count;
            bridgeRemainingCandidates = bridgeResult.RemainingCandidates;

            foreach (var a in bridgeResult.Applied)
            {
                if (input.Exclusions?.Contains(a.MethodName) == true) { continue; }
                items.Add(new OperationItemRecord
                {
                    FilePath = a.FilePath,
                    MethodName = a.MethodName,
                    Outcome = ItemRecordOutcome.Succeeded,
                    Reason = "phase:bridge",
                });
                succeeded++;
            }
            foreach (var s in bridgeResult.Skipped)
            {
                bool requiresManualReview = s.Reason.Contains("NeedsManualReview")
                    || s.Reason.Contains("already has CancellationToken")
                    || s.Reason.Contains("event handler");
                var bridgeDiags = s.Diagnostics.Count > 0 ? s.Diagnostics : null;
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome = requiresManualReview ? ItemRecordOutcome.Skipped : ItemRecordOutcome.Failed,
                    Reason = $"phase:bridge — {s.Reason}",
                    CompilerDiagnostics = bridgeDiags,
                });
                if (requiresManualReview)
                {
                    skipped++;
                }
                else
                {
                    if (failures.Count < 10)
                    {
                        failures.Add(new FailureDetail
                        {
                            FilePath = s.FilePath,
                            MethodName = s.MethodName,
                            Reason = s.Reason,
                            Outcome = ItemRecordOutcome.Failed,
                            CompilerDiagnostics = bridgeDiags,
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
            // Already-bridged methods (stale AsyncBridgeCandidate flags from a prior run) were
            // skipped by the bridge phase, but their callers may still need uplift. Include them
            // so Asyncify makes progress on re-runs without requiring a manual flag refresh first.
            var alreadyBridgedSkipped = bridgeResult.Skipped
                .Where(s => s.Reason.Contains("already has CancellationToken", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bridgeResult.Applied.Count > 0 || alreadyBridgedSkipped.Count > 0)
            {
                var upliftTargets = bridgeResult.Applied
                    .Where(a => input.Exclusions?.Contains(a.MethodName) != true)
                    .Select(a => new UpliftBatchMultiTarget
                    {
                        BridgedMethodName = a.MethodName,
                        ProjectName = input.ProjectName,
                    })
                    .Concat(alreadyBridgedSkipped
                        .Where(s => input.Exclusions?.Contains(s.MethodName) != true)
                        .Select(s => new UpliftBatchMultiTarget
                        {
                            BridgedMethodName = s.MethodName,
                            ProjectName = input.ProjectName,
                        }))
                    .DistinctBy(t => t.BridgedMethodName)
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
                                Outcome = ItemRecordOutcome.Succeeded,
                                Reason = "phase:uplift",
                            });
                            succeeded++;
                        }
                        foreach (var s in pm.Result.Skipped)
                        {
                            var upliftDiags = s.Diagnostics.Count > 0 ? s.Diagnostics : null;
                            items.Add(new OperationItemRecord
                            {
                                FilePath = s.FilePath,
                                MethodName = s.CallerMethod,
                                Outcome = ItemRecordOutcome.Failed,
                                Reason = $"phase:uplift — {s.Reason}",
                                CompilerDiagnostics = upliftDiags,
                            });
                            if (failures.Count < 10)
                            {
                                failures.Add(new FailureDetail
                                {
                                    FilePath = s.FilePath,
                                    MethodName = s.CallerMethod,
                                    Reason = s.Reason,
                                    Outcome = ItemRecordOutcome.Failed,
                                    CompilerDiagnostics = upliftDiags,
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

            // ── Phase 3a: Bridge HandlerToAsyncCandidate extracted methods ─────────
            // Extracted event-handler bodies that have been flagged HandlerToAsyncCandidate need
            // the same bridge conversion as AsyncBridgeCandidate — but they weren't discovered in
            // Phase 1 (which only flags AsyncBridgeCandidate). After bridging, their event-handler
            // callers typically become AsyncHandlerCandidate and are picked up by Phase 3b below.
            {
                List<MigrationCandidateFinding> handlerToAsyncCandidates;
                try
                {
                    handlerToAsyncCandidates = await _asyncOptimizationEngine.FindMigrationCandidatesAsync(
                        filePath: null, projectName: input.ProjectName, pattern: "HandlerToAsyncCandidate",
                        cancellationToken: innerToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AsyncifyCore Phase 3a: HandlerToAsyncCandidate discovery failed — skipping");
                    handlerToAsyncCandidates = new List<MigrationCandidateFinding>();
                }

                var handlerToAsyncTargets = handlerToAsyncCandidates
                    .Where(h => input.Exclusions?.Contains(h.MethodName) != true)
                    .ToList();

                foreach (var candidate in handlerToAsyncTargets)
                {
                    CheckIterations(1);
                    if (innerToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        if (stopReason.Length == 0) stopReason = "maxRuntimeSeconds exceeded during handler-to-async phase";
                        goto WriteSummary;
                    }

                    if (input.DryRun)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Skipped,
                            Reason = "dry_run phase:handler_to_async",
                        });
                        succeeded++;
                        continue;
                    }

                    try
                    {
                        string? updatedSource;
                        var convertResult = await _asyncOptimizationEngine.ConvertToAsyncBridgeAsync(
                            candidate.FilePath, candidate.MethodName, progress: progress!, cancellationToken: innerToken);
                        updatedSource = convertResult.UpdatedText;

                        if (input.PropagateCancellationTokens)
                        {
                            var asyncMethod = candidate.MethodName + "Async";
                            var propagationResult = await _asyncOptimizationEngine
                                .PropagateCancellationTokenInMethodAsync(candidate.FilePath, asyncMethod, progress: progress!, cancellationToken: innerToken);
                            if (!string.IsNullOrEmpty(propagationResult.UpdatedText))
                                updatedSource = propagationResult.UpdatedText;
                        }

                        var applyResult3a = await _workspaceManager.ApplyProposedChangesAsync(
                            new Dictionary<FilePath, string> { { candidate.FilePath, updatedSource } },
                            validateChanges: true, cancellationToken: innerToken);
                        if (!applyResult3a.Success && applyResult3a.ValidationResult != null)
                        {
                            var diagMsg = string.Join("; ", applyResult3a.ValidationResult.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                            throw new InvalidOperationException($"Validation: {applyResult3a.ValidationResult.Diagnostics.Count} error(s) — {diagMsg}");
                        }
                        handlerBridgedFiles.Add(candidate.FilePath);

                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Succeeded,
                            Reason = "phase:handler_to_async",
                        });
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AsyncifyCore Phase 3a: bridge conversion failed for '{Method}'", candidate.MethodName);
                        items.Add(new OperationItemRecord
                        {
                            FilePath = candidate.FilePath,
                            MethodName = candidate.MethodName,
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = ex.Message,
                        });
                        if (failures.Count < 10)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = candidate.FilePath,
                                MethodName = candidate.MethodName,
                                Reason = ex.Message,
                                Outcome = ItemRecordOutcome.Failed,
                            });
                        }
                        failed++;
                    }
                }
            }

            // ── Phase 3b: Convert AsyncHandlerCandidate event handlers in-place ──
            // Event handlers flagged as AsyncHandlerCandidate call obsolete bridge wrappers but
            // cannot be uplifted via the standard caller-bridge path (fixed delegate signatures).
            // Convert each in-place: add async modifier, replace bridge call with await asyncCall().
            {
                List<MigrationCandidateFinding> handlerCandidates;
                try
                {
                    handlerCandidates = await _asyncOptimizationEngine.FindMigrationCandidatesAsync(
                        filePath: null, projectName: input.ProjectName, pattern: "AsyncHandlerCandidate",
                        cancellationToken: innerToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AsyncifyCore Phase 3b: candidate discovery failed — skipping handler phase");
                    handlerCandidates = new List<MigrationCandidateFinding>();
                }

                var handlerTargets = handlerCandidates
                    .Where(h => input.Exclusions?.Contains(h.MethodName) != true)
                    .ToList();

                foreach (var handler in handlerTargets)
                {
                    CheckIterations(1);
                    if (innerToken.IsCancellationRequested)
                    {
                        stoppedEarly = true;
                        if (stopReason.Length == 0) stopReason = "maxRuntimeSeconds exceeded during handler phase";
                        goto WriteSummary;
                    }

                    if (input.DryRun)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = handler.FilePath,
                            MethodName = handler.MethodName,
                            Outcome = ItemRecordOutcome.Skipped,
                            Reason = "dry_run phase:handler",
                        });
                        succeeded++;
                        continue;
                    }

                    try
                    {
                        var handlerResult = await _asyncOptimizationEngine
                            .ConvertEventHandlerCallerToAsyncVoidAsync(
                                handler.FilePath, handler.MethodName, cancellationToken: innerToken);

                        if (!string.IsNullOrEmpty(handlerResult.UpdatedText))
                        {
                            var applyResult3b = await _workspaceManager.ApplyProposedChangesAsync(
                                new Dictionary<FilePath, string> { { handler.FilePath, handlerResult.UpdatedText } },
                                validateChanges: true, cancellationToken: innerToken);
                            if (!applyResult3b.Success && applyResult3b.ValidationResult != null)
                            {
                                var diagMsg = string.Join("; ", applyResult3b.ValidationResult.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                                throw new InvalidOperationException($"Validation: {applyResult3b.ValidationResult.Diagnostics.Count} error(s) — {diagMsg}");
                            }
                            handlerConvertedFiles.Add(handler.FilePath);
                        }

                        items.Add(new OperationItemRecord
                        {
                            FilePath = handler.FilePath,
                            MethodName = handler.MethodName,
                            Outcome = ItemRecordOutcome.Succeeded,
                            Reason = "phase:handler",
                        });
                        succeeded++;
                    }
                    catch (Exception ex) when (
                        ex.Message.Contains("is already async") ||
                        ex.Message.Contains("does not call any Asyncify-bridge"))
                    {
                        // Stale AsyncHandlerCandidate flag: either already converted in a prior run
                        // or flagged under the old broad approach for non-bridge obsolete calls.
                        // Just strip the attribute — no NeedsManualReview needed.
                        _logger.LogInformation(
                            "AsyncifyCore Phase 3b: '{Method}' stale flag — stripping", handler.MethodName);
                        try
                        {
                            var removeResult = await _asyncOptimizationEngine.RemoveMigrationCandidatesAsync(
                                filePath: handler.FilePath.Absolute,
                                pattern: "AsyncHandlerCandidate",
                                cancellationToken: innerToken);
                            if (removeResult.Changes.Count > 0)
                                await _workspaceManager.ApplyProposedChangesAsync(removeResult.Changes);
                        }
                        catch (Exception removeEx)
                        {
                            _logger.LogWarning(removeEx,
                                "Could not strip stale flag for '{Method}'", handler.MethodName);
                        }
                        items.Add(new OperationItemRecord
                        {
                            FilePath = handler.FilePath,
                            MethodName = handler.MethodName,
                            Outcome = ItemRecordOutcome.Skipped,
                            Reason = "stale-flag: stripped",
                        });
                        skipped++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AsyncifyCore Phase 3b: handler conversion failed for '{Method}'", handler.MethodName);

                        try
                        {
                            // Remove AsyncHandlerCandidate first so it doesn't stack with NeedsManualReview.
                            var removeResult = await _asyncOptimizationEngine.RemoveMigrationCandidatesAsync(
                                filePath: handler.FilePath.Absolute,
                                pattern: "AsyncHandlerCandidate",
                                cancellationToken: innerToken);
                            if (removeResult.Changes.Count > 0)
                                await _workspaceManager.ApplyProposedChangesAsync(removeResult.Changes);

                            var flagResult = await _asyncOptimizationEngine.FlagMigrationCandidateAsync(
                                handler.FilePath, handler.MethodName, "NeedsManualReview",
                                score: 0,
                                reason: $"Handler in-place: {ex.Message}",
                                cancellationToken: innerToken);
                            await _workspaceManager.ApplyProposedChangesAsync(flagResult.Changes);
                        }
                        catch (Exception flagEx)
                        {
                            _logger.LogWarning(flagEx,
                                "Could not flag handler '{Method}' as NeedsManualReview", handler.MethodName);
                        }

                        items.Add(new OperationItemRecord
                        {
                            FilePath = handler.FilePath,
                            MethodName = handler.MethodName,
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = ex.Message,
                        });
                        if (failures.Count < 10)
                        {
                            failures.Add(new FailureDetail
                            {
                                FilePath = handler.FilePath.Absolute,
                                MethodName = handler.MethodName,
                                Reason = ex.Message,
                                Outcome = ItemRecordOutcome.Failed,
                            });
                        }
                        failed++;
                    }
                }
            }

            // ── Phase 4: Propagate CT ─────────────────────────────────────────
            if (input.PropagateCancellationTokens && !input.DryRun)
            {
                var bridgedFiles = bridgeResult.Applied
                    .Select(a => a.FilePath)
                    .Concat(handlerConvertedFiles)
                    .Concat(handlerBridgedFiles)
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
                            Outcome = ItemRecordOutcome.Succeeded,
                            Reason = $"phase:propagate_ct — {a.TotalForwarded} call sites forwarded",
                        });
                    }
                    foreach (var f in ctResult.Failed)
                    {
                        items.Add(new OperationItemRecord
                        {
                            FilePath = f.FilePath,
                            Outcome = ItemRecordOutcome.Failed,
                            Reason = $"phase:propagate_ct — {f.Reason}",
                        });
                    }
                }
            }

        WriteSummary:;
        }
        catch (OperationCanceledException) when (innerToken.IsCancellationRequested
                                                  && !(cancellationToken.IsCancellationRequested))
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
                $"Asyncify failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}", ex);
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: skipped);

        var blobName2 = await OperationBlobWriter.WriteAsync(
            "asyncify", changeId, items, _workspaceManager.GetSolutionRoot());
        var status2 = _workspaceManager.GetBreakerStatus();

        string directive;
        if (stoppedEarly)
        {
            directive = $"stopped_early — {stopReason}. Phases completed are in blob: {blobName2}.";
        }
        else if (succeeded == 0 && !status2.Open && !stoppedEarly
                 && bridgeStopReason == "no_candidates" && !bridgeMinScore.HasValue)
        {
            if (flagPhaseScanned > 0 && flagPhaseNewFlags == 0)
            {
                directive = $"Phase 1 (flag) scanned {flagPhaseScanned} methods but none qualified for async bridging " +
                            $"— all scored below minScore={input.MinScore} or were already marked for manual review. " +
                            $"Re-run Asyncify with a lower minScore (e.g., minScore=25), or run " +
                            $"flag_migration_candidates(scope=\"project\", minScore=25) first to review the candidate pool.";
            }
            else
            {
                directive = "No [MigrationCandidate] attributes found in the solution. " +
                            $"Run flag_migration_candidates(scope=\"project\", minScore={DefaultMinScore}) first to identify " +
                            "and tag async migration candidates, then re-run asyncify.";
            }
        }
        else if (succeeded == 0 && !status2.Open && !stoppedEarly && bridgeMinScore.HasValue)
        {
            directive = $"No candidates scored at or above scoreThreshold={input.ScoreThreshold}; " +
                        $"the highest-scoring candidate found has score={bridgeMinScore.Value}. " +
                        $"Re-run with scoreThreshold={bridgeMinScore.Value} (or lower) to include it.";
        }
        else if (succeeded == 0 && !status2.Open && !stoppedEarly
                 && bridgeStopReason == "batch_complete" && bridgeSkippedCount > 0)
        {
            directive = $"{bridgeSkippedCount} candidate(s) were attempted but all required manual review — " +
                        $"the async transform produced compiler errors (likely no async API equivalent exists for the called sync methods). " +
                        $"Call get_operation_detail(changeId=\"{changeId}\", filter=\"skipped\") to see per-method skip reasons and compiler diagnostics.";
        }
        else
        {
            var stopDesc = bridgeStopReason switch
            {
                "batch_complete" => "All eligible candidates were processed in this run.",
                "budget_exhausted" => $"Stopped after maxMethods={input.MaxMethods} limit — {bridgeRemainingCandidates} eligible candidate(s) remain; re-run to continue.",
                "dry_run" => "Dry run complete — no files were written to disk.",
                _ when bridgeStopReason.Length > 0 => $"Bridge phase ended: {bridgeStopReason}.",
                _ => string.Empty,
            };
            var writeNote = WriteStatusNote(input.DryRun, succeeded);
            directive = stopDesc.Length > 0
                ? $"{writeNote}{stopDesc} {status2.Directive}".TrimEnd()
                : $"{writeNote}{status2.Directive}".TrimEnd();
        }

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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status2.Severity,
            Directive = directive,
            BreakerOpen = status2.Open,
            MinCandidateScore = bridgeMinScore,
            Suggestions = AsyncMigrationDiagnostic.Analyse(succeeded, failed, changeId, items),
        };
    }

    private async Task<BatchResultSummary> HandlerToAsyncCore(
        string? projectName,
        bool dryRun,
        int maxItems,
        bool propagateCancellationTokens,
        IProgress<string>? progress,
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
                $"EventHandlersToAsync discovery failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}", ex);
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
                    Outcome = ItemRecordOutcome.Skipped,
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

                var handlerToAsyncValidation = await _validationEngine.ValidateChangesAsync(
                    new Dictionary<FilePath, string> { { filePath, updatedSource } },
                    cancellationToken: cancellationToken);
                if (!handlerToAsyncValidation.Success)
                {
                    var diagMsg = string.Join("; ", handlerToAsyncValidation.Diagnostics.Take(3).Select(d => $"[{d.Id}] {d.Message}"));
                    throw new InvalidOperationException($"Validation: {handlerToAsyncValidation.Diagnostics.Count} error(s) — {diagMsg}");
                }

                var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                    new Dictionary<FilePath, string> { { filePath, updatedSource } });

                string? beforeSource = null;
                _ = applyResult.PreImages?.TryGetValue(filePath, out beforeSource);

                items.Add(new OperationItemRecord
                {
                    FilePath = filePath,
                    MethodName = methodName,
                    Outcome = ItemRecordOutcome.Succeeded,
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
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = filePath,
                        MethodName = methodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(dryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };
    }

    private async Task<BatchResultSummary> HandlerExtractCore(
        List<HandlerExtractTarget> targets,
        bool dryRun,
        IProgress<string>? progress,
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
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
                    Outcome = ItemRecordOutcome.Skipped,
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
                    Outcome = ItemRecordOutcome.Succeeded,
                    BeforeSource = beforeSource,
                });
                succeeded++;
            }
            catch (Exception ex)
            {
                var reason = $"apply failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}";
                items.Add(new OperationItemRecord
                {
                    FilePath = target.FilePath,
                    MethodName = target.NewMethodName,
                    Outcome = ItemRecordOutcome.Failed,
                    Reason = reason,
                });
                if (failures.Count < 10)
                {
                    failures.Add(new FailureDetail
                    {
                        FilePath = target.FilePath,
                        MethodName = target.NewMethodName,
                        Reason = reason,
                        Outcome = ItemRecordOutcome.Failed,
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
            FailuresTruncated = failed > 10,
            FailuresByReason = failed > 10 ? failures.GroupBy(f => f.Reason).ToDictionary(g => g.Key, g => g.Count()) : null,
            Severity = status.Severity,
            Directive = WriteStatusNote(dryRun, succeeded) + status.Directive,
            BreakerOpen = status.Open,
        };
    }

    // Returns a short prefix for BatchResultSummary.Directive that tells the model whether
    // source files were actually written to disk or only computed (dry run).
    private static string WriteStatusNote(bool dryRun, int succeeded) =>
        dryRun ? "Dry run — no files written to disk. " :
        succeeded > 0 ? $"{succeeded} change(s) written to disk. " :
        "";

    // Converts an event-handler name to PascalCase by splitting on '_' and capitalising each part.
    // Example: "button1_Click" → "Button1Click", "Form_Load" → "FormLoad".
    private static string ToPascalCase(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpper(p[0]) + p[1..]));
    }
}
