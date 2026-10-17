using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Backs the QualifyCrefsTool tool: resolves the scope to documents, qualifies bare member crefs per document through
/// <see cref="CrefQualifier"/>, and writes each project's edits as one validated batch through
/// <see cref="ValidateAndApplyHelper"/>. dryRun runs the same compile gate and returns the same batch without writing.
/// Projects run one after another. Never throws; every failure is a structured <see cref="SentinelCallToolResult{T}"/> error.
/// </summary>
public class QualifyCrefsImpl
{
    private const string ToolName = "QualifyCrefs";

    private readonly ValidationEngine _validationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;

    public QualifyCrefsImpl(ValidationEngine validationEngine, IWorkspaceManager workspaceManager, ILogger logger)
    {
        _validationEngine = validationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Qualifies the bare member crefs in the scoped documents. dryRun writes nothing and returns the planned summary
    /// (with diffs when returnDiff is set). Otherwise each project's batch is applied in turn.
    /// </summary>
    /// <remarks>
    /// Each project is isolated: an exception in one project is recorded as that project's failed outcome, and the
    /// batches earlier projects already wrote keep their ChangeIds. Only failures before the project loop reach the
    /// outer catch. In a dry run, partially_applied means some projects would be rejected by the compile gate.
    /// </remarks>
    public async Task<SentinelCallToolResult<object>> QualifyAsync(
        ToolScope scope,
        string? scopeName,
        string? containingType,
        bool dryRun,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        if (scope != ToolScope.solution && string.IsNullOrWhiteSpace(scopeName))
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"scopeName is required when scope is '{scope}'. Pass a file path for scope: file or a project name for scope: project. Use scope: solution to cover the whole solution.");
        }

        try
        {
            var documentScope = scope switch
            {
                ToolScope.file => DocumentScope.ForFile(_workspaceManager.ResolveFromWire(scopeName!.Trim())),
                ToolScope.project => DocumentScope.ForProject(scopeName!.Trim()),
                _ => DocumentScope.WholeSolution,
            };

            var initial = await _workspaceManager.GetDocumentsAsync(documentScope, ReadSource.Committed, cancellationToken);

            // One group per project, one entry per file (a linked file appears in several projects; the first one wins).
            var groups = initial
                .Where(d => d.FilePath is not null)
                .GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .GroupBy(d => d.Project.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groups.Count == 0)
            {
                return Error(ToolErrorCode.InvalidArgument,
                    $"scope '{scope}' matched no documents in the loaded solution. Check scopeName (a file path or project name), or reload the solution if the file was just added.");
            }

            var filter = BuildFilter(containingType);
            var runs = new List<QualifyCrefsProjectRun>();

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runs.Add(await QualifyProjectAsync(group, filter, dryRun, returnDiff, cancellationToken));
            }

            var outcomes = runs.Select(r => QualifyCrefsReport.ToOutcome(r, dryRun)).ToList();
            var attempted = runs.Where(r => r.Apply is not null).ToList();

            if (attempted.Count > 0 && attempted.All(r => r.Apply!.Error is not null))
            {
                var failures = outcomes.Where(o => o.IsError).ToList();
                var message = (dryRun
                        ? "No project passed validation; nothing would be written. "
                        : "No project's batch could be applied; every file is unchanged. ")
                    + string.Join(" ", failures.Select(f => $"{f.Project}: {f.Error}"));
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(
                        failures[0].ErrorCode ?? ToolErrorCode.Exception,
                        $"{ToolName}: {message}",
                        StructuredDetail: failures.Cast<object>().ToList()),
                };
            }

            return Success(QualifyCrefsReport.Build(outcomes, dryRun, _workspaceManager.WorkspaceVersion));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tool} failed while resolving the scope", ToolName);

            // ToolException subclasses carry their own code and an agent-facing message; the mapper keeps them.
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, ToolName),
            };
        }
    }

    /// <summary>
    /// Qualifies one project's documents and applies (or validates) its batch. An exception here is recorded as a
    /// failed project outcome instead of being thrown, so batches already written by earlier projects keep their
    /// ChangeIds. OperationCanceledException is rethrown. The error text comes from ToolErrorMapper, never raw.
    /// </summary>
    private async Task<QualifyCrefsProjectRun> QualifyProjectAsync(
        IGrouping<string, Document> group,
        Func<ISymbol, bool>? filter,
        bool dryRun,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        try
        {
            // Re-read the committed solution per project: an earlier project's write has already resynced it,
            // so Document objects from the first read are stale for later projects.
            var wanted = new HashSet<string>(group.Select(d => d.FilePath!), StringComparer.OrdinalIgnoreCase);
            var fresh = await _workspaceManager.GetDocumentsAsync(
                DocumentScope.ForProject(group.Key), ReadSource.Committed, cancellationToken);

            var changes = new Dictionary<FilePathWrapper, string>();
            var files = new List<string>();
            var qualified = 0;
            var skippedAlreadyQualified = 0;
            var skippedTypeOrPredefined = 0;
            var skippedUnresolved = 0;
            var droppedByRebind = 0;

            foreach (var document in fresh.Where(d => d.FilePath is not null && wanted.Contains(d.FilePath)))
            {
                var result = await CrefQualifier.QualifyDocumentAsync(document, filter, cancellationToken);
                qualified += result.Qualified;
                skippedAlreadyQualified += result.SkippedAlreadyQualified;
                skippedTypeOrPredefined += result.SkippedTypeOrPredefined;
                skippedUnresolved += result.SkippedUnresolved;
                droppedByRebind += result.DroppedByRebind;

                if (result.Edits.Count == 0)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken);
                changes[new FilePathWrapper(document.FilePath!)] = text.WithChanges(result.Edits).ToString();
                files.Add(document.FilePath!);
            }

            ApplyOutcome? apply = null;
            if (changes.Count > 0)
            {
                apply = await ValidateAndApplyHelper.ValidateAndApplyAsync(
                    _validationEngine,
                    _workspaceManager,
                    _logger,
                    changes,
                    ToolName,
                    dryRun: dryRun,
                    returnDiff: returnDiff,
                    cancellationToken: cancellationToken);
            }

            return new QualifyCrefsProjectRun(
                group.Key,
                files,
                qualified,
                skippedAlreadyQualified,
                skippedTypeOrPredefined,
                skippedUnresolved,
                droppedByRebind,
                apply);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tool} failed for project {Project}", ToolName, group.Key);

            // Recorded as this project's failed outcome: nothing of this project landed, and the counters are unknown.
            return new QualifyCrefsProjectRun(
                group.Key,
                new List<string>(),
                0,
                0,
                0,
                0,
                0,
                new ApplyOutcome(null, ToolErrorMapper.ToResultError(ex, _workspaceManager, ToolName), dryRun));
        }
    }

    /// <summary>
    /// Matches the containing type by simple name or by display string. Null when no filter was given, so every
    /// bare member cref is considered.
    /// </summary>
    private static Func<ISymbol, bool>? BuildFilter(string? containingType)
    {
        if (string.IsNullOrWhiteSpace(containingType))
        {
            return null;
        }

        var wanted = containingType.Trim();
        return symbol => symbol.ContainingType is { } owner
            && (string.Equals(owner.Name, wanted, StringComparison.Ordinal)
                || string.Equals(owner.ToDisplayString(), wanted, StringComparison.Ordinal));
    }

    private static SentinelCallToolResult<object> Success(object data) =>
        new SentinelCallToolResult<object> { IsError = false, SuccessData = data };

    private static SentinelCallToolResult<object> Error(string code, string message) =>
        new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(code, $"{ToolName}: {message}"),
        };
}
