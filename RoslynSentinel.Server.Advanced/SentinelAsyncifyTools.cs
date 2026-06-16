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
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
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
        AsyncSafetyEngine asyncSafetyEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        AsyncBatchEngine asyncBatchEngine,
        MsToolAugmentEngine msToolAugmentEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelAsyncifyTools> logger)
    {
        _antiPatternEngine = antiPatternEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _asyncBatchEngine = asyncBatchEngine;
        _msToolAugmentEngine = msToolAugmentEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "ScanMigrationCandidates")]
    [Produces(DataTag.MigrationCandidate)]
    [Description("""
        Returns [MigrationCandidate]-attributed methods added by flag_migration_candidate. Syntax-level — no compilation needed. pattern: call describe_advanced_tool_options("scan_migration_candidates") for valid values. summarize=true → guaranteed ≤2KB dashboard (byClass capped at 10, TopCandidates capped at 5 regardless of topN; ByClassTruncated=true when truncated). summarize=false + limit/offset → full paged candidate records. minScore filters in both modes; TotalRecords reflects post-filter count. A method flagged for two patterns appears twice. When results exceed the inline threshold, LargeResultInfo is populated instead of Data — call get_scan_result(scanId) to read in pages.
        """)]
    public async Task<ToolResult<object>> ScanMigrationCandidates(
        string? filePath = null,
        string? projectName = null,
        string? pattern = null,
        bool summarize = false,
        int? topN = null,
        int? minScore = null,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0)
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

    // ── Phase 8: get_async_migration_progress ─────────────────────────────────

    [McpServerTool(Name = "GetAsyncMigrationProgress")]
    [Produces(DataTag.AsyncMigrationProgressReport)]
    [Description("""
        Returns async migration progress statistics for the solution or a single project. Reports: total async Task/ValueTask methods, how many have a CancellationToken parameter (and how many still need one), percentage coverage, Asyncify-bridge wrapper count ([Obsolete("Asyncify-bridge:...")]), bridge call sites pending migration (CS0618), and async void event handlers (informational — their signatures cannot be extended). projectName=null → entire solution.
        """)]
    public async Task<ToolResult<AsyncMigrationProgressReport>> GetAsyncMigrationProgress(
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
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
                            .PropagateCancellationTokenInMethodAsync(target.FilePath, asyncMethod, cancellationToken);
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
                    var reason = ex.Message;
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
                        target.FilePath, target.MethodNames, cancellationToken);

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
            "run_uplift", changeId, items, _workspaceManager.GetSolutionRoot());
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
                    // Backfill BeforeSource on succeeded records now that PreImages are available.
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
                        // Backfill BeforeSource on succeeded records now that PreImages are available.
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
                cancellationToken);

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
                items.Add(new OperationItemRecord
                {
                    FilePath = s.FilePath,
                    MethodName = s.MethodName,
                    Outcome = OperationOutcome.Failed,
                    Reason = $"phase:bridge — {s.Reason}",
                });
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

    [Description("""
        Pattern-driven bridge conversion for HandlerToAsync candidates. Discovers all methods
        flagged [MigrationCandidate("HandlerToAsync")] in the specified project (or solution)
        and converts each to the Asyncify-bridge pattern. Checks the circuit breaker; records
        outcome; writes a forensic blob.

        input.ProjectName              — scope discovery to one project; null = entire solution.
        input.DryRun                   — when true, validates without writing files.
        input.MaxItems                 — max methods to process (default 100).
        input.PropagateCancellationTokens — propagate CT in the new async overload (default true).

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-method details.
        """)]
    private async Task<BatchResultSummary> HandlerToAsync(
        string? projectName,
        bool dryRun,
        int maxItems,
        bool propagateCancellationTokens,
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
                filePath: null, projectName: projectName, pattern: "HandlerToAsync",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandlerToAsync discovery unexpected exception");
            throw new InvalidOperationException(
                $"HandlerToAsync discovery failed: {ex.GetType().Name}: {ex.Message}", ex);
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
                    filePath, methodName, cancellationToken);
                updatedSource = convertResult.UpdatedText;

                if (propagateCancellationTokens)
                {
                    var asyncMethod = methodName + "Async";
                    var propagationResult = await _asyncOptimizationEngine
                        .PropagateCancellationTokenInMethodAsync(filePath, asyncMethod, cancellationToken);
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
                    "HandlerToAsync: {Method} in {File} failed: {Reason}",
                    methodName, filePath, reason);
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: overLimit);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "handler_to_async", changeId, items, _workspaceManager.GetSolutionRoot());
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

    [Description("""
        Batch-first method extraction for HandlerExtract candidates. Extracts each nominated
        code block into a new private method using semantic analysis to determine the correct
        return type (fixes the standard extract_method bug where selections ending with
        'return expr' produce void instead of the expression's actual type).

        Each target specifies one extraction via a contextSnippet — a short, unique fragment
        of the code block to extract. Targets in the same file are processed sequentially so
        each extraction sees the file as left by the previous one. Checks the circuit breaker;
        records outcome; writes a forensic blob.

        input.HandlerExtractTargets — list of { FilePath, NewMethodName, ContextSnippet,
                                       LineBefore?, LineAfter? }
        input.DryRun               — when true, validates the contextSnippet is locatable
                                     without writing files.

        Returns BatchResultSummary. Use get_operation_detail(changeId) for per-target details.
        """)]
    private async Task<BatchResultSummary> HandlerExtract(
        List<HandlerExtractTarget> targets,
        bool dryRun,
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
                var reason = $"NewMethodName is required for handler_extract (file: {target.FilePath})";
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

            if (string.IsNullOrWhiteSpace(target.ContextSnippet))
            {
                var reason = $"ContextSnippet is required for handler_extract (file: {target.FilePath})";
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
                    "HandlerExtract: {Method} in {File} threw: {Reason}",
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
                    "HandlerExtract: {Method} in {File} failed: {Reason}",
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
                    "HandlerExtract: apply changes for {Method} in {File} failed: {Reason}",
                    target.NewMethodName, target.FilePath, reason);
            }
        }

        _workspaceManager.RecordBatchOutcome(succeeded, failed, rolledBack: 0, skipped: 0);

        var changeId = Guid.NewGuid().ToString("N")[..8];
        var blobName = await OperationBlobWriter.WriteAsync(
            "handler_extract", changeId, items, _workspaceManager.GetSolutionRoot());
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

    // ── Phase 7 — async_migrate dispatcher ────────────────────────────────────

    [McpServerTool(Name = "AsyncMigrate")]
    [Produces(DataTag.BatchResultSummary)]
    [Description("""
        Unified dispatcher for eight async-migration operations. All operations check the circuit breaker first and return BatchResultSummary. operation: call describe_advanced_tool_options("async_migrate") for valid values and required input fields per operation. Use get_operation_detail(changeId) for per-item details. Severity="halt" → circuit breaker opened; call get_breaker_status then reset_breaker. ErrorCode="SolutionNotLoaded" → call load_solution first. ErrorCode="InvalidArgument" → unknown operation name.
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
                        Targets = input.BatchTargets ?? [],
                        DryRun = input.DryRun,
                        MaxItems = input.MaxItems,
                    },
                    cancellationToken),

                "convert_to_async_bridge" => await ConvertToAsyncBridge(
                    new BatchTargetInput
                    {
                        Targets = input.BatchTargets ?? [],
                        DryRun = input.DryRun,
                        MaxItems = input.MaxItems,
                    },
                    input.PropagateCancellationTokens,
                    cancellationToken),

                "add_cancellation_token" => await AddCancellationToken(
                    new BatchTargetInput
                    {
                        Targets = input.BatchTargets ?? [],
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

                "handler_to_async" => await HandlerToAsync(
                    input.ProjectName,
                    input.DryRun,
                    input.MaxItems,
                    input.PropagateCancellationTokens,
                    cancellationToken),

                "handler_extract" => await HandlerExtract(
                    input.HandlerExtractTargets ?? [],
                    input.DryRun,
                    cancellationToken),

                _ => throw new ArgumentException(
                    $"Unknown operation '{operation}'. Valid: propagate_cancellation_token, " +
                    "convert_to_async_bridge, add_cancellation_token, run_uplift, " +
                    "flag_migration_candidates, asyncify, handler_to_async, handler_extract.",
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

    internal static ToolOptionsResult AsyncMigrateOptions() => new()
    {
        Description = """
            async_migrate — operation values and required input fields:

              "propagate_cancellation_token"
                  input.BatchTargets    — list of { FilePath, MethodNames? }
                  input.DryRun          — optional, default false
                  input.MaxItems        — optional, default 100

              "convert_to_async_bridge"
                  input.BatchTargets    — list of { FilePath, MethodNames } (MethodNames required)
                  input.DryRun          — optional, default false
                  input.PropagateCancellationTokens — optional, default true

              "add_cancellation_token"
                  input.BatchTargets    — list of { FilePath, MethodNames? }
                  input.DryRun          — optional, default false
                  input.MaxItems        — optional, default 100

              "run_uplift"
                  input.UpliftTargets   — list of { BridgedMethodName, ProjectName? }
                  input.DryRun          — optional, default false
                  input.MaxCallersPerMethod       — optional, default 10
                  input.PropagateCancellationTokens — optional, default true
                  NOTE: also the apply-step for AsyncCallerUplift-flagged candidates.
                        Call scan_migration_candidates(pattern="AsyncCallerUplift") to identify
                        which bridge methods have callers that need uplift, then pass those
                        bridge method names as UpliftTargets.

              "flag_migration_candidates"
                  input.FlagScope       — "targets" (default) or "project"
                  input.FlagTargets     — list of { FilePath, MethodName, Pattern, Score?, Reason? } (scope=targets)
                  input.ProjectName     — project name (scope=project); null = entire solution
                  input.Pattern         — optional, default "AsyncBridgeCandidate"
                                          also accepts: "HandlerExtract", "HandlerToAsync", "AsyncCallerUplift"
                  input.MinScore        — optional, default 50
                  input.DryRun          — optional, default false
                  input.ForceRescan     — optional, default false

              "asyncify"
                  input.ProjectName     — project; null = entire solution
                  input.MethodTargets   — list of { FilePath, MethodName } (singular MethodName, not MethodNames); skips discovery phase when provided
                  input.Exclusions      — method names to skip
                  input.DryRun          — optional, default false
                  input.PropagateCancellationTokens — optional, default true
                  input.MaxMethods      — optional, default 50
                  input.MaxCallersPerMethod       — optional, default 10
                  input.MinScore        — optional, default 50
                  input.ScoreThreshold  — optional, default 60

              "handler_to_async"
                  Auto-discovers all [MigrationCandidate("HandlerToAsync")]-flagged methods and
                  converts each to the Asyncify-bridge pattern (sync wrapper + async overload).
                  input.ProjectName     — scope to one project; null = entire solution
                  input.DryRun          — optional, default false
                  input.MaxItems        — optional, default 100
                  input.PropagateCancellationTokens — optional, default true

              "handler_extract"
                  Extracts nominated code blocks into new private methods using semantic analysis
                  (correct return type inference; fixes the standard extract_method void-return bug).
                  Typical workflow: scan_migration_candidates(pattern="HandlerExtract") to find
                  candidates → inspect source → call handler_extract with specific snippets.
                  input.HandlerExtractTargets — list of:
                      FilePath        — absolute path to the .cs file
                      NewMethodName   — C# identifier for the new extracted method (required)
                      ContextSnippet  — short unique fragment of the code block to extract (required)
                      LineBefore      — optional line before the snippet for disambiguation
                      LineAfter       — optional line after the snippet for disambiguation
                  input.DryRun        — when true, validates each contextSnippet is locatable
                                        without writing files; useful for pre-flight check
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["propagate_cancellation_token"] = new { BatchTargets = new[] { new { FilePath = "path/to/file.cs", MethodNames = new[] { "MyMethod" } } }, DryRun = false, MaxItems = 100 },
            ["convert_to_async_bridge"] = new { BatchTargets = new[] { new { FilePath = "path/to/file.cs", MethodNames = new[] { "MyMethod" } } }, DryRun = false, PropagateCancellationTokens = true },
            ["add_cancellation_token"] = new { BatchTargets = new[] { new { FilePath = "path/to/file.cs", MethodNames = new[] { "MyMethod" } } }, DryRun = false, MaxItems = 100 },
            ["run_uplift"] = new { UpliftTargets = new[] { new { BridgedMethodName = "MyMethod", ProjectName = "MyProject" } }, DryRun = false, MaxCallersPerMethod = 10, PropagateCancellationTokens = true },
            ["flag_migration_candidates"] = new { FlagScope = "targets|project", FlagTargets = new[] { new { FilePath = "path/to/file.cs", MethodName = "MyMethod" } }, DryRun = false, MinScore = 50, ForceRescan = false },
            ["asyncify"] = new { MethodTargets = new[] { new { FilePath = "path/to/file.cs", MethodName = "MySingleMethod" } }, Exclusions = new[] { "MethodToSkip" }, DryRun = false, MaxMethods = 50, MaxCallersPerMethod = 10, MinScore = 50, ScoreThreshold = 60 },
            ["handler_to_async"] = new { ProjectName = (string?)null, DryRun = false, MaxItems = 100, PropagateCancellationTokens = true },
            ["handler_extract"] = new { HandlerExtractTargets = new[] { new { FilePath = "path/to/file.cs", NewMethodName = "HandleRequest", ContextSnippet = "var result = Process(input);", LineBefore = (string?)null, LineAfter = (string?)null } }, DryRun = false },
        }
    };

    internal static ToolOptionsResult ScanMigrationCandidatesOptions() => new()
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
}