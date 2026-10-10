namespace RoslynSentinel.Common;

/// <summary>How much of a full <see cref="BuildResult"/> the wire response should carry.</summary>
/// <param name="MaxDetails">Maximum number of individual error (and, when requested, warning) rows returned.</param>
/// <param name="IncludeOutput">Keep StdoutTail/StderrTail even when errors were parsed (or the build succeeded).</param>
/// <param name="IncludeWarnings">Return the warning list and warning summary (the warning count is always kept).</param>
public sealed record BuildProjection(int MaxDetails, bool IncludeOutput, bool IncludeWarnings);

/// <summary>
/// Pure projection of a full (engine-side) <see cref="BuildResult"/> into the small payload the Build tool
/// returns: green builds carry counts only; failed builds carry a per-project/per-file breakdown, the
/// root-cause projects' errors first, a capped detail list and the number omitted. No I/O: the tool layer
/// stores <see cref="BuildResult.FullDiagnostics"/> and sets <see cref="BuildResult.FullDiagnosticsResultId"/>.
/// </summary>
public static class BuildResultProjector
{
    /// <summary>Longest message returned; a longer one is cut to 297 characters plus "..." (exactly 300).</summary>
    public const int MaxMessageLength = 300;

    /// <summary>Most rows in <see cref="BuildFailureBreakdown.ByFile"/>.</summary>
    public const int MaxFileBreakdownRows = 20;

    /// <summary>Most groups kept in ErrorSummary.</summary>
    public const int MaxErrorSummaryGroups = 10;

    /// <summary>Most example locations kept per ErrorSummary/WarningSummary group.</summary>
    public const int MaxSummaryLocations = 3;

    private const string Unknown = "(unknown)";
    private const string Ellipsis = "...";

    /// <summary>
    /// Shrinks <paramref name="full"/> for the wire. <paramref name="transitiveDependencies"/> maps a project name
    /// to the names of the projects it transitively depends on; null means no graph is known, so every project
    /// that has errors counts as a root cause. A project absent from the map is treated as having no dependencies.
    /// Leaves <see cref="BuildResult.FullDiagnostics"/> and <see cref="BuildResult.FullDiagnosticsResultId"/> untouched.
    /// </summary>
    public static BuildResult Project(
        BuildResult full,
        BuildProjection options,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? transitiveDependencies)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(options);

        List<DiagnosticInfo> errors = full.FullDiagnostics?.Errors ?? full.Errors ?? [];
        List<DiagnosticInfo> warnings = full.FullDiagnostics?.Warnings ?? full.Warnings ?? [];
        int maxDetails = Math.Max(0, options.MaxDetails);

        List<DiagnosticInfo> projectedWarnings = options.IncludeWarnings ? CapAndCut(warnings, maxDetails) : [];
        List<DiagnosticGroupSummary> projectedWarningSummary = options.IncludeWarnings
            ? CutSummaries(full.WarningSummary, int.MaxValue)
            : [];

        if (full.Outcome == BuildOutcome.Succeeded)
        {
            return full with
            {
                Errors = [],
                Warnings = projectedWarnings,
                ErrorSummary = [],
                WarningSummary = projectedWarningSummary,
                StdoutTail = options.IncludeOutput ? full.StdoutTail : null,
                StderrTail = options.IncludeOutput ? full.StderrTail : null,
                Breakdown = null,
                OmittedErrorCount = 0,
                SuppressedDownstreamErrorCount = 0,
            };
        }

        // Failed (or NotRun). Tails stay when nothing parsed, otherwise the cause would be invisible.
        bool keepTails = options.IncludeOutput || errors.Count == 0;

        BuildFailureBreakdown? breakdown = null;
        List<DiagnosticInfo> details = [];
        int suppressed = 0;

        if (errors.Count > 0)
        {
            Dictionary<string, int> errorsByProject = CountByProject(errors);
            Dictionary<string, bool> isRoot = ClassifyRootCauses(errorsByProject.Keys, transitiveDependencies);

            suppressed = errors.Count(e => !isRoot[ProjectOf(e)]);

            breakdown = new BuildFailureBreakdown(
                ByProject: errorsByProject
                    .Select(kv => new BuildProjectErrorCount(kv.Key, kv.Value, isRoot[kv.Key]))
                    .OrderByDescending(p => p.IsRootCause)
                    .ThenByDescending(p => p.ErrorCount)
                    .ThenBy(p => p.Project, StringComparer.Ordinal)
                    .ToList(),
                ByFile: errors
                    .GroupBy(FileOf, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new BuildFileErrorCount(g.Key, g.Count()))
                    .OrderByDescending(f => f.ErrorCount)
                    .ThenBy(f => f.File, StringComparer.Ordinal)
                    .Take(MaxFileBreakdownRows)
                    .ToList());

            // Root-cause errors first, original order kept inside each half.
            details = errors.Where(e => isRoot[ProjectOf(e)])
                .Concat(errors.Where(e => !isRoot[ProjectOf(e)]))
                .Take(maxDetails)
                .Select(CutMessage)
                .ToList();
        }

        // The engine may already have capped Errors (no FullDiagnostics): trust the exact ErrorCount for "omitted".
        int totalErrors = Math.Max(full.ErrorCount, errors.Count);

        return full with
        {
            Errors = details,
            Warnings = projectedWarnings,
            ErrorSummary = CutSummaries(full.ErrorSummary, MaxErrorSummaryGroups),
            WarningSummary = projectedWarningSummary,
            StdoutTail = keepTails ? full.StdoutTail : null,
            StderrTail = keepTails ? full.StderrTail : null,
            Breakdown = breakdown,
            OmittedErrorCount = Math.Max(0, totalErrors - details.Count),
            SuppressedDownstreamErrorCount = suppressed,
        };
    }

    private static string ProjectOf(DiagnosticInfo d) => string.IsNullOrWhiteSpace(d.Project) ? Unknown : d.Project;

    private static string FileOf(DiagnosticInfo d)
    {
        string file = d.FilePath.ToString();
        return string.IsNullOrWhiteSpace(file) ? Unknown : file;
    }

    private static Dictionary<string, int> CountByProject(List<DiagnosticInfo> errors)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DiagnosticInfo e in errors)
        {
            string project = ProjectOf(e);
            counts[project] = counts.GetValueOrDefault(project) + 1;
        }

        return counts;
    }

    // A project with errors is a root cause when none of the projects it transitively depends on has errors.
    private static Dictionary<string, bool> ClassifyRootCauses(
        IEnumerable<string> erroringProjects,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? transitiveDependencies)
    {
        var erroring = new HashSet<string>(erroringProjects, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in erroring)
        {
            bool root = true;
            if (transitiveDependencies is not null
                && transitiveDependencies.TryGetValue(project, out IReadOnlySet<string>? dependencies))
            {
                root = !dependencies.Any(d => erroring.Contains(d) && !string.Equals(d, project, StringComparison.OrdinalIgnoreCase));
            }

            result[project] = root;
        }

        return result;
    }

    private static List<DiagnosticInfo> CapAndCut(List<DiagnosticInfo> source, int max) =>
        source.Take(max).Select(CutMessage).ToList();

    private static DiagnosticInfo CutMessage(DiagnosticInfo d) =>
        d.Message is { Length: > MaxMessageLength }
            ? d with { Message = Cut(d.Message) }
            : d;

    // Locations are cut to the first few: the per-file breakdown already says where the errors are, and ten long
    // paths per group is what pushed a 100-error payload over the offload threshold.
    private static List<DiagnosticGroupSummary> CutSummaries(List<DiagnosticGroupSummary>? summaries, int max) =>
        (summaries ?? [])
            .Take(max)
            .Select(s => s with
            {
                MessageTemplate = s.MessageTemplate is { Length: > MaxMessageLength } ? Cut(s.MessageTemplate) : s.MessageTemplate,
                Locations = s.Locations?.Take(MaxSummaryLocations).ToList() ?? [],
            })
            .ToList();

    private static string Cut(string message) =>
        string.Concat(message.AsSpan(0, MaxMessageLength - Ellipsis.Length), Ellipsis);
}
