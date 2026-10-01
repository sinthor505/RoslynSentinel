using RoslynSentinel.Common.GitWorktree;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// Mints the run id, run directory and branch name for one SubAgent/SubAgentEval call.
/// </summary>
/// <remarks>
/// Every call must get a name that cannot collide with another call's, whether that is a concurrent
/// call in the same server process, a call from another session on the same repo, or a nested call
/// made from inside a call. The id is a millisecond timestamp (matching PlanStepRunner's RunId
/// convention, so run folders sort chronologically) plus 8 hex characters of GUID entropy.
/// PlanStepRunner needs no suffix because each run is a distinct human-launched process; a live
/// server can start several calls in the same millisecond.
/// <para>
/// The run directory is a sibling of the source repo (<c>../RoslynSentinel-TestRuns/SubAgent/&lt;runId&gt;</c>),
/// the same place PlanStepRunner defaults to, never inside the repo: a run folder inside the repo would
/// show up as untracked content in the live session's own <c>git status</c>.
/// </para>
/// </remarks>
public static class SubAgentRunNaming
{
    /// <summary>Folder name, under the test-runs root, that owns every SubAgent run.</summary>
    public const string RunsFolderName = "SubAgent";

    private const string TestRunsRootFolderName = "RoslynSentinel-TestRuns";

    /// <summary>Mints a fresh, collision-resistant run id.</summary>
    public static string NewRunId(DateTimeOffset? utcNow = null)
    {
        var timestamp = (utcNow ?? DateTimeOffset.UtcNow).UtcDateTime;
        return $"{timestamp:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>The folder that owns this run's worktree, build output and logs.</summary>
    public static string RunDirectory(string sourceRepo, string runId)
    {
        var trimmed = sourceRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var root = string.IsNullOrEmpty(parent) ? Path.GetTempPath() : parent;
        return Path.Combine(root, TestRunsRootFolderName, RunsFolderName, runId);
    }

    /// <summary>The disposable git branch the run's worktree is checked out on.</summary>
    public static string BranchName(string runId) => "subagent/" + runId;
}

/// <summary>
/// The single pseudo-step a SubAgent run hands to <see cref="GitWorktreeManager"/>, which is keyed by
/// a step file name. Its only job is to fix the worktree's folder name (<c>&lt;runDir&gt;/subagent/Worktree</c>).
/// </summary>
public sealed class SubAgentWorktreeStep : IWorktreeStep
{
    /// <summary>Folder name (without extension) holding Worktree, Logs and Build under the run directory.</summary>
    public const string FolderName = "subagent";

    /// <summary>Shared instance; the step carries no state.</summary>
    public static SubAgentWorktreeStep Instance { get; } = new();

    public string FileName => FolderName + ".md";
}

/// <summary>
/// A child-server launch failure (worktree creation, server build, MCP connect, LoadSolution) whose
/// <see cref="Exception.Message"/> is already safe and actionable to return to a tool caller, with
/// optional <see cref="Detail"/> (e.g. the tail of a build log). Exists so the tool boundary can
/// return a structured error without ever surfacing a raw exception or stack trace.
/// </summary>
public sealed class SubAgentLaunchException : Exception
{
    public SubAgentLaunchException(string errorCode, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        Detail = detail;
    }

    /// <summary>A <see cref="ToolErrorCode"/> value.</summary>
    public string ErrorCode { get; }

    /// <summary>Extra context for the caller, already trimmed; null when there is none.</summary>
    public string? Detail { get; }
}
