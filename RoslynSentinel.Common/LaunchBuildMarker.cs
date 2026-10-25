using System.Reflection;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// The launcher's last build outcome, read from <c>launch-outcome.json</c> in the instance folder. When
/// <c>BuildFailed</c> is true the launcher's build of committed HEAD (<c>AttemptedSha</c>) failed and this server runs
/// the older build <c>ServedSha</c>; <c>FirstErrors</c> holds the first compiler errors and <c>Hint</c> the advice.
/// </summary>
public sealed record LaunchBuildResult(string? Utc, string? AttemptedSha, string? ServedSha, bool BuildFailed, IReadOnlyList<string> FirstErrors, string? Hint);

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
        var hint = buildFailed
            ? $"Committed HEAD {attempted} does not build; this server runs older build {served}. Fix and commit, then McpServerControl StopServer."
            : null;
        return new LaunchBuildResult(raw.Utc, raw.AttemptedSha, raw.ServedSha, buildFailed, raw.FirstErrors ?? Array.Empty<string>(), hint);
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
    /// launch's build failed and this is a server process. Null otherwise (the flag is not consumed in those cases).
    /// </summary>
    public static string? TakeEnvelopeWarning() => IsServerProcess() ? TakeEnvelopeWarning(Current) : null;

    /// <summary>Returns <paramref name="outcome"/>'s hint the first time only when its build failed; null otherwise.</summary>
    internal static string? TakeEnvelopeWarning(LaunchBuildResult? outcome)
    {
        if (outcome is not { BuildFailed: true })
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
