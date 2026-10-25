using System.Text.RegularExpressions;

namespace RoslynSentinel.Common;

/// <summary>What started a workspace reload attempt.</summary>
public enum WorkspaceReloadTriggerKind
{
    /// <summary>An explicit LoadSolution call.</summary>
    ExplicitLoad,

    /// <summary>The start-time automatic load.</summary>
    StartupLoad,

    /// <summary>A .sln or .slnx file changed on disk.</summary>
    SolutionFileChange,

    /// <summary>A project file changed on disk.</summary>
    ProjectFileChange,

    /// <summary>The file watcher lost events and the workspace was re-synced.</summary>
    WatcherOverflow,

    /// <summary>The file watcher reported an error.</summary>
    WatcherError,

    /// <summary>A write tool synchronised the workspace after its own change.</summary>
    PostWriteSync,

    /// <summary>A write tool reloaded the workspace after its own change.</summary>
    PostWriteReload,

    /// <summary>A source file change that could not be folded into the loaded solution.</summary>
    SourceFileChange,
}

/// <summary>
/// Sticky record of the last failed workspace reload. <c>TriggerFile</c> is a file name only, never a path.
/// </summary>
public sealed record WorkspaceReloadFailure(WorkspaceReloadTriggerKind Trigger, string? TriggerFile, DateTime FailedAtUtc, int Attempts, bool OldWorkspaceKept, string Cause);

/// <summary>
/// Turns exception and message text into a short, single-line, path-free cause string that is safe
/// to expose on a tool result.
/// </summary>
public static class WorkspaceReloadCause
{
    private const int MaxLength = 200;

    private static readonly Regex PathPattern = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s""'<>|]*|\\\\[^\s""'<>|]+|(?<![\w.])/[^\s""'<>|/]+/[^\s""'<>|]*",
        RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Type name and sanitized message, for example "ArgumentException: ...".</summary>
    public static string Sanitize(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var typeName = ex.GetType().Name;
        if (string.IsNullOrWhiteSpace(ex.Message))
        {
            return typeName;
        }

        return typeName + ": " + Sanitize(ex.Message);
    }

    /// <summary>Keeps the first non-empty line, replaces absolute paths with their file names, collapses whitespace and truncates to 200 characters.</summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "unknown";
        }

        var firstLine = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (firstLine is null)
        {
            return "unknown";
        }

        var withoutPaths = PathPattern.Replace(firstLine, match => Path.GetFileName(match.Value));
        var collapsed = WhitespaceRun.Replace(withoutPaths, " ").Trim();
        if (collapsed.Length == 0)
        {
            return "unknown";
        }

        return collapsed.Length > MaxLength ? collapsed[..MaxLength] + "..." : collapsed;
    }
}

/// <summary>
/// Wording and classification helpers for the stale-workspace refusal. Shared so every refusal path
/// (current and future) reports the same text.
/// </summary>
public static class WorkspaceStaleText
{
    /// <summary>How to recover from a stale workspace: fix the cause, then force a reload.</summary>
    public static string Recovery => $"Fix the cause (for example a broken .csproj or .sln file), then call {ToolCallHint.Call("LoadSolution", ("forceReload", "true"))}. Edits to .csproj, .sln and .slnx files are still allowed.";

    /// <summary>One-line reason naming the failed reload trigger, its file (when known) and its cause.</summary>
    public static string Reason(WorkspaceReloadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var fileSuffix = string.IsNullOrEmpty(failure.TriggerFile) ? "" : $" of '{failure.TriggerFile}'";
        return $"A workspace reload ({failure.Trigger}{fileSuffix}) failed: {failure.Cause}. The loaded solution may not match disk.";
    }

    /// <summary>True when the path is a solution or project file, which stays editable while the workspace is stale.</summary>
    public static bool IsStructuralTarget(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Exposes the sticky last workspace reload failure.</summary>
public interface IWorkspaceReloadFailureProvider
{
    /// <summary>The last failed reload, cleared by the next successful full load or reload; null when none is recorded.</summary>
    WorkspaceReloadFailure? ReloadFailure { get; }
}
