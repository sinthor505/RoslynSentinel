using System.Globalization;
using System.Text.Json;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The persisted record of the last full test run, stored as camelCase JSON in
/// <c>&lt;solutionRoot&gt;/.roslynsentinel/full-test-marker.json</c> (see <see cref="FullTestMarkerStore.PathFor"/>).
/// Format (other writers, such as scripts/Invoke-WorktreeTests.ps1, must produce the same shape):
/// <code>{"lastFullRunUtc":"2026-11-10T03:00:00Z","totalCount":1234,"failedCount":0,"fastRunsSince":3}</code>
/// <c>lastFullRunUtc</c> is an ISO 8601 UTC time; <c>totalCount</c>/<c>failedCount</c> are the counts of that full run;
/// <c>fastRunsSince</c> counts completed fast runs since it (0 right after a full run). A full solution-scope run writes
/// the marker with <c>fastRunsSince</c> 0; each completed fast run adds 1. A missing, unreadable or corrupt file counts as
/// "no full run recorded" and the next write replaces it. Several server processes share the file, last writer wins.
/// There is no head SHA field: the server has no in-process way to read HEAD without spawning git.
/// </summary>
public record FullTestMarker(DateTime LastFullRunUtc, int TotalCount, int FailedCount, int FastRunsSince);

/// <summary>
/// Present on a fast RunTest result (and, as a warning finding, on a Git commit result) when the full suite is stale:
/// <see cref="Reason"/> says why, <see cref="LastFullRunUtc"/> is null when no full run is recorded, and
/// <see cref="FastRunsSince"/> is the number of completed fast runs since the last full one.
/// </summary>
public record FullSuiteStale(string Reason, DateTime? LastFullRunUtc, int FastRunsSince);

/// <summary>
/// Reads and updates the full-test marker (<see cref="FullTestMarker"/>) and decides whether the full suite is stale: the
/// last full run is more than <see cref="FullSuiteStalenessOptions.MaxAgeDays"/> days old, or more than
/// <see cref="FullSuiteStalenessOptions.MaxFastRuns"/> fast runs ago, or there is no readable marker. Nothing here throws:
/// a marker problem must never fail a tool call.
/// </summary>
public static class FullTestMarkerStore
{
    /// <summary>The marker's file name inside the solution root's <c>.roslynsentinel</c> folder.</summary>
    public const string FileName = "full-test-marker.json";

    /// <summary>Finding source used for the Git commit warning.</summary>
    public const string FindingSource = "FullSuiteStale";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary><c>&lt;solutionRoot&gt;/.roslynsentinel/full-test-marker.json</c>.</summary>
    public static string PathFor(string solutionRoot) => Path.Combine(solutionRoot, ".roslynsentinel", FileName);

    /// <summary>Reads the marker; null when the file is missing, unreadable, corrupt or has no valid <c>lastFullRunUtc</c>.</summary>
    public static FullTestMarker? TryRead(string? solutionRoot)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return null;
        }

        try
        {
            var path = PathFor(solutionRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var marker = JsonSerializer.Deserialize<FullTestMarker>(File.ReadAllText(path), JsonOptions);
            if (marker is null || marker.LastFullRunUtc == default || marker.TotalCount < 0 || marker.FailedCount < 0 || marker.FastRunsSince < 0)
            {
                return null;
            }

            return marker with { LastFullRunUtc = marker.LastFullRunUtc.ToUniversalTime() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The marker to write after a RunTest, or null when nothing changes. Only a completed run counts. A full
    /// solution-scope run with no user filter resets the marker (<c>fastRunsSince</c> 0). A full run of another scope, or a full
    /// run narrowed by a filter (<paramref name="filtered"/>), neither resets nor increments.
    /// A fast run (any scope) increments <c>fastRunsSince</c>; with no marker there is nothing to increment, so none is created
    /// and the suite stays "no full run recorded".
    /// </summary>
    public static FullTestMarker? NextMarker(
        FullTestMarker? current, TestSuite suite, ToolScope scope, bool runCompleted, int totalCount, int failedCount, DateTime utcNow, bool filtered = false)
    {
        if (!runCompleted)
        {
            return null;
        }

        if (suite == TestSuite.full)
        {
            return scope == ToolScope.solution && !filtered ? new FullTestMarker(utcNow, totalCount, failedCount, 0) : null;
        }

        return current is null ? null : current with { FastRunsSince = current.FastRunsSince + 1 };
    }

    /// <summary>Applies <see cref="NextMarker"/> to the marker on disk (read, update, atomic write). Never throws; a null or empty root does nothing.</summary>
    public static void RecordRun(string? solutionRoot, TestSuite suite, ToolScope scope, TestRunResult run, DateTime? utcNow = null, string? filter = null)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return;
        }

        try
        {
            var next = NextMarker(TryRead(solutionRoot), suite, scope, run.RunCompleted, run.TotalCount, run.FailedCount, utcNow ?? DateTime.UtcNow, filtered: !string.IsNullOrWhiteSpace(filter));
            if (next is not null)
            {
                Write(PathFor(solutionRoot), next);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort bookkeeping: a locked or read-only marker must not fail the test run.
        }
    }

    /// <summary>Stale (with the reason) or null when fresh: no marker, last full run older than <paramref name="maxAge"/>, or more than <paramref name="maxFastRuns"/> fast runs since.</summary>
    public static FullSuiteStale? Evaluate(FullTestMarker? marker, DateTime utcNow, TimeSpan maxAge, int maxFastRuns)
    {
        if (marker is null)
        {
            return new FullSuiteStale("no full run recorded", null, 0);
        }

        var reasons = new List<string>();
        var age = utcNow - marker.LastFullRunUtc;
        if (age > maxAge)
        {
            reasons.Add($"last full run {FormatDays(age)} ago (limit {maxAge.TotalDays.ToString("0.##", CultureInfo.InvariantCulture)} days)");
        }

        if (marker.FastRunsSince > maxFastRuns)
        {
            reasons.Add($"{marker.FastRunsSince} fast runs since the last full run (limit {maxFastRuns})");
        }

        return reasons.Count == 0 ? null : new FullSuiteStale(string.Join("; ", reasons), marker.LastFullRunUtc, marker.FastRunsSince);
    }

    /// <summary>Evaluates the marker under <paramref name="solutionRoot"/> against <see cref="FullSuiteStalenessOptions"/>. Null (no warning) when there is no solution root.</summary>
    public static FullSuiteStale? EvaluateForRoot(string? solutionRoot, DateTime? utcNow = null)
    {
        if (string.IsNullOrEmpty(solutionRoot))
        {
            return null;
        }

        return Evaluate(
            TryRead(solutionRoot),
            utcNow ?? DateTime.UtcNow,
            TimeSpan.FromDays(FullSuiteStalenessOptions.MaxAgeDays),
            FullSuiteStalenessOptions.MaxFastRuns);
    }

    /// <summary>One status line, for example "Full suite last run 3 days / 52 fast runs ago. Next: RunTest(...suite: "full") before committing."; the call is rendered by <see cref="ToolCallHint"/>.</summary>
    public static string Describe(FullSuiteStale stale, DateTime? utcNow = null)
    {
        var call = ToolCallHint.Call("RunTest", ("suite", "full"));
        if (stale.LastFullRunUtc is not { } last)
        {
            return $"No full suite run recorded. Run {call} before committing.";
        }

        return $"Full suite last run {FormatDays((utcNow ?? DateTime.UtcNow) - last)} / {stale.FastRunsSince} fast runs ago. Run {call} before committing.";
    }

    /// <summary>The Git commit warning: one Warning finding when the suite is stale, none when fresh (or no solution root). Reads the marker only, never increments.</summary>
    public static IReadOnlyList<Finding> StaleFindings(string? solutionRoot, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var stale = EvaluateForRoot(solutionRoot, now);
        return stale is null ? [] : [new Finding(FindingSource, Describe(stale, now), FindingSeverity.Warning)];
    }

    // "3 days", "1 day", "5 hours", "under an hour".
    private static string FormatDays(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            var days = (int)Math.Floor(age.TotalDays);
            return days == 1 ? "1 day" : $"{days} days";
        }

        var hours = (int)Math.Floor(age.TotalHours);
        return hours switch { < 1 => "under an hour", 1 => "1 hour", _ => $"{hours} hours" };
    }

    // Atomic: write a temp file next to the marker, then replace the marker (File.Move with overwrite), so a reader never sees a half-written file.
    private static void Write(string path, FullTestMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(marker, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp file.
            }
        }
    }
}
