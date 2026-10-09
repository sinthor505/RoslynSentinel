using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// The status of the solution load.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SolutionLoadStatus
{
    /// <summary>No solution has been loaded.</summary>
    NotLoaded,

    /// <summary>A solution load is in progress.</summary>
    Loading,

    /// <summary>A solution was successfully loaded.</summary>
    Loaded,

    /// <summary>The last load attempt failed.</summary>
    Failed,
}

/// <summary>
/// Static class holding timeout constants for solution loads.
/// </summary>
public static class SolutionLoadWait
{
    /// <summary>Default timeout for waiting on a solution to load (30 seconds).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Whether this server process has ever loaded a solution. The state is per process, so a server
/// restarted by an update (or a crash) reports a fresh start even though the chat that is talking
/// to it never ended -> that is exactly what a "No solution is loaded" message needs to explain.
/// </summary>
/// <param name="ServerStartedUtc">When this server process started.</param>
/// <param name="LastLoadedUtc">When a solution last loaded successfully in this process, or null if none has.</param>
/// <param name="LoadInProgress">True while a LoadSolution call holds the workspace lock (typically the start-time auto-load); lets callers say "retry shortly" instead of "call LoadSolution".</param>
public sealed record SolutionLoadState(DateTime ServerStartedUtc, DateTime? LastLoadedUtc, bool LoadInProgress = false, string? LastLoadFailure = null)
{
    /// <summary>True once any solution has loaded successfully in this process.</summary>
    public bool HasLoadedSolutionSinceStart => LastLoadedUtc is not null;

    /// <summary>True while this process has not loaded any solution (a freshly started server).</summary>
    public bool IsFreshStartup => LastLoadedUtc is null;

    /// <summary>Start time of the current process, in UTC. Falls back to "now" if the OS will not say.</summary>
    public static DateTime ProcessStartedUtc
    {
        get;
    } = ReadProcessStartedUtc();

    private static DateTime ReadProcessStartedUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }
}

/// <summary>
/// The single source of the "No solution is loaded" wording, so every tool and engine says the same
/// thing and a freshly started server explains why the solution an agent was just using is gone.
/// </summary>
public static class SolutionNotLoadedMessage
{
    /// <summary>The message when the process has loaded a solution before, or when no state is available.</summary>
    public const string Plain = "No solution is loaded. Call LoadSolution with a .sln, .slnx, or .csproj path.";

    /// <summary>Builds the message for the given state; adds the fresh-start explanation when nothing has loaded yet.</summary>
    public static string Build(SolutionLoadState? state, DateTime? nowUtc = null)
    {
        if (state is null)
        {
            return Plain;
        }

        if (state.LoadInProgress && !state.HasLoadedSolutionSinceStart)
        {
            return "No solution is loaded yet: a solution load started when this server (re)started and is still in progress. Retry this call in a few seconds; calling LoadSolution is not needed.";
        }

        if (!state.IsFreshStartup)
        {
            return Plain;
        }

        var age = FormatAge((nowUtc ?? DateTime.UtcNow) - state.ServerStartedUtc);
        var started = state.ServerStartedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var message = $"No solution is loaded. This server process was freshly (re)started at {started} UTC ({age} ago) "
            + "and no solution has been loaded since, so a solution loaded earlier in this chat was lost with the previous process. "
            + "Files on disk are unaffected. Call LoadSolution with a .sln, .slnx, or .csproj path.";

        if (state.LastLoadFailure is not null)
        {
            message += " The last load attempt failed: " + state.LastLoadFailure;
        }

        return message;
    }

    /// <summary>The message for a load that exceeded its timeout.</summary>
    public static string LoadWaitTimedOut(TimeSpan waited)
    {
        var seconds = (int)Math.Ceiling(waited.TotalSeconds);
        return $"The solution is still loading after waiting {seconds} s, so this call did not run. The load is continuing and has its own time limit. Retry in a few seconds, or call McpServerStatus and wait until solutionLoadStatus is Loaded.";
    }

    /// <summary>The message for a load that was cancelled due to timeout.</summary>
    public static string LoadCancelledAfterTimeout(TimeSpan limit)
    {
        var seconds = (int)Math.Ceiling(limit.TotalSeconds);
        return $"The solution load did not finish within {seconds} s and was cancelled; no solution was loaded. Retry LoadSolution with a larger timeoutSeconds (for example {seconds * 2}), or check the server log for MSBuild errors.";
    }

    /// <summary>The message for a tool whose <c>filePath</c> could not be resolved because no solution is loaded.</summary>
    public static string ForFilePath(string toolName, SolutionLoadState? state)
        => $"{toolName}: 'filePath' could not be resolved. {Build(state)} Then retry with the same filePath.";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes} min";
        }

        return $"{(int)age.TotalHours}h {age.Minutes} min";
    }
}
