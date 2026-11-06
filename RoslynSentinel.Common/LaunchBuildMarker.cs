using System.Reflection;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// The launcher's last build outcome, read from <c>launch-outcome.json</c> in the instance folder. When
/// <c>BuildFailed</c> is true the launcher's build of committed HEAD (<c>AttemptedSha</c>) failed and this server runs
/// the older build <c>ServedSha</c>; <c>FirstErrors</c> holds the first compiler errors and <c>Hint</c> the advice.
/// <c>PullOk</c> is whether the launcher's pull of the source clone succeeded: null = not reported (an older launcher or
/// marker; unknown, NOT a failure), false = the pull failed and this server may be older than the dev repo
/// (<c>PullError</c> is the capped reason).
/// </summary>
public sealed record LaunchBuildResult(string? Utc, string? AttemptedSha, string? ServedSha, bool BuildFailed, IReadOnlyList<string> FirstErrors, bool? PullOk, string? PullError, string? Hint);

/// <summary>
/// Reads the launcher's build marker. A missing, unreadable or malformed marker reads as "no outcome" (null); nothing here throws.
/// </summary>
public static class LaunchBuildMarker
{
    /// <summary>File the launcher overwrites in the instance folder on every launch.</summary>
    public const string FileName = "launch-outcome.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<LaunchBuildResult?> CurrentLazy =
        new(() => Read(ServerInstancePaths.Current.WorkingDir), LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _envelopeWarningTaken;

    /// <summary>The marker of this server's instance, read once per process; null when there is no marker.</summary>
    public static LaunchBuildResult? Current => CurrentLazy.Value;

    /// <summary>Parses the marker JSON. Returns null for empty, malformed or non-object input.</summary>
    internal static LaunchBuildResult? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        LaunchBuildResult? raw;
        try
        {
            raw = JsonSerializer.Deserialize<LaunchBuildResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (raw is null)
        {
            return null;
        }

        var buildFailed = raw.BuildFailed;
        var attempted = raw.AttemptedSha ?? "?";
        var served = raw.ServedSha ?? "?";
        var restart = ToolCallHint.Call("McpServerControl", ("operation", "StopServer"), ("confirmServerStop", "ConfirmServerStop"));
        var pullError = CapPullError(raw.PullError);
        var hints = new List<string>();
        if (buildFailed)
        {
            hints.Add($"Committed HEAD {attempted} does not build; this server runs older build {served}. Fix and commit, then {restart}.");
        }

        // Only an explicit false is a failure; a missing pullOk (older launcher) stays unknown and says nothing.
        if (raw.PullOk == false)
        {
            // Recovery grounded in scripts/roslynsentinel-instances-common.ps1 (Repair-RoslynSentinelSourceClone): the
            // launcher removes an index.lock older than 10 minutes, runs git reset --hard HEAD and retries the pull once.
            hints.Add("The launcher could not update its source clone, so this server may be OLDER than the dev repo"
                + (pullError is null ? "." : $" (pull error: {pullError}).")
                + $" Recover: wait until any <instances root>\\source\\.git\\index.lock is over 10 minutes old (or delete it), then {restart}; the next launch removes a stale lock, resets the clone and retries the pull.");
        }

        var hint = hints.Count == 0 ? null : string.Join(" ", hints);
        return new LaunchBuildResult(raw.Utc, raw.AttemptedSha, raw.ServedSha, buildFailed, raw.FirstErrors ?? Array.Empty<string>(), raw.PullOk, pullError, hint);
    }

    /// <summary>Longest <c>pullError</c> kept; the launcher already caps it, this guards a hand-edited or foreign marker.</summary>
    internal const int MaxPullErrorLength = 300;

    private static string? CapPullError(string? pullError)
    {
        if (string.IsNullOrWhiteSpace(pullError))
        {
            return null;
        }

        return pullError.Length > MaxPullErrorLength ? pullError[..MaxPullErrorLength] : pullError;
    }

    /// <summary>Reads and parses the marker in <paramref name="workingDir"/>; null when absent, unreadable or malformed.</summary>
    internal static LaunchBuildResult? Read(string workingDir)
    {
        try
        {
            var path = Path.Combine(workingDir, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Value for the response envelope's <c>LaunchWarning</c>: the hint once per process, only when the current
    /// launch's build failed or its source pull failed, and this is a server process. Null otherwise (the flag is not
    /// consumed in those cases).
    /// </summary>
    public static string? TakeEnvelopeWarning() => IsServerProcess() ? TakeEnvelopeWarning(Current) : null;

    /// <summary>Returns <paramref name="outcome"/>'s hint the first time only when its build or pull failed; null otherwise.</summary>
    internal static string? TakeEnvelopeWarning(LaunchBuildResult? outcome)
    {
        if (outcome is null || (!outcome.BuildFailed && outcome.PullOk != false))
        {
            return null;
        }

        return Interlocked.Exchange(ref _envelopeWarningTaken, 1) == 0 ? outcome.Hint : null;
    }

    /// <summary>Clears the once-per-process flag. Tests only.</summary>
    internal static void ResetEnvelopeWarningForTests() => Interlocked.Exchange(ref _envelopeWarningTaken, 0);

    private static bool IsServerProcess() =>
        Assembly.GetEntryAssembly()?.GetName().Name?.StartsWith("RoslynSentinel.Server.", StringComparison.Ordinal) == true;
}
