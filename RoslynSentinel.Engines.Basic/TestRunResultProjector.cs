using System.Linq;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// RunTest counterpart to BuildResultProjector. Truncation is applied at the tool boundary
/// (after ParseTrx), not inside ParseTrx, so the engine keeps full data.
/// </summary>
public static class TestRunResultProjector
{
    private const int MaxMessageLength = 300;
    private const int MaxTrailingMessageLength = 150;
    private const int MaxStackChars = 300;
    private const int MaxStackLines = 2;
    private const int MaxDetailedRows = 10;
    private const int MaxFailureGroups = 10;

    /// <summary>Projects a test run result for output: truncates messages and stacks, drops streams and output unless requested.</summary>
    public static TestRunResult Project(TestRunResult result, TestResultsFilter resultsType, bool includeOutput)
    {
        var rows = result.Results.Select((row, index) => ProjectRow(row, index, includeOutput)).ToList();
        var keepStreams = includeOutput || !result.RunCompleted || result.TotalCount == 0;
        var matchingCount = resultsType switch
        {
            TestResultsFilter.failed => result.FailedCount,
            TestResultsFilter.skipped => result.SkippedCount,
            _ => result.TotalCount,
        };

        return result with
        {
            Results = rows,
            StdoutTail = keepStreams ? result.StdoutTail : null,
            StderrTail = keepStreams ? result.StderrTail : null,
            FailureSummary = result.FailureSummary.Take(MaxFailureGroups).ToList(),
            OmittedResultCount = Math.Max(0, matchingCount - result.Results.Count),
        };
    }

    private static TestCaseResult ProjectRow(TestCaseResult row, int index, bool includeOutput)
    {
        var detailed = index < MaxDetailedRows;
        return row with
        {
            ErrorMessage = Truncate(row.ErrorMessage, detailed ? MaxMessageLength : MaxTrailingMessageLength),
            ErrorStackTrace = detailed ? TrimStack(row.ErrorStackTrace) : null,
            Output = includeOutput ? row.Output : null,
        };
    }

    private static string? TrimStack(string? stack)
    {
        if (stack is null)
        {
            return null;
        }

        var lines = stack
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxStackLines);

        return Truncate(string.Join("\n", lines), MaxStackChars);
    }

    private static string? Truncate(string? text, int limit)
    {
        const string ellipsis = "...";
        if (text is null || text.Length <= limit)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, limit - ellipsis.Length), ellipsis);
    }
}
