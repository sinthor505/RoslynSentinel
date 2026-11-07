using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

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

    /// <summary>A project file or a build-configuration file (.props, .targets) changed on disk.</summary>
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

    /// <summary>True when the path is a solution, project or build-configuration file, which stays editable while the workspace is stale.</summary>
    public static bool IsStructuralTarget(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || IsBuildConfigFile(path);
    }

    /// <summary>
    /// True for an MSBuild import file (.props or .targets, including Directory.Build.props, Directory.Build.targets and
    /// Directory.Packages.props). A change to one can alter every project's evaluation, so it takes the full reload like a .csproj.
    /// </summary>
    internal static bool IsBuildConfigFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Exposes the sticky last workspace reload failure.</summary>
public interface IWorkspaceReloadFailureProvider
{
    /// <summary>The last failed reload, cleared by the next successful full load or reload; null when none is recorded.</summary>
    WorkspaceReloadFailure? ReloadFailure
    {
        get;
    }
}
/// <summary>
/// Raised when a workspace open completed but one or more projects failed to load (see ProjectLoadFailureCollector),
/// so the resulting solution would not match disk. Its type name is part of the recorded cause. DTO-EXEMPT: exception, not a DTO.
/// </summary>
internal sealed class ProjectLoadFailedException(string message) : Exception(message);
/// <summary>
/// Collects the WorkspaceDiagnosticKind.Failure diagnostics an MSBuildWorkspace raises while opening a solution
/// that name a .NET project file (.csproj, .vbproj, .fsproj): an unparseable or missing project file, or a project
/// MSBuild could not evaluate. MSBuildWorkspace reports those through its WorkspaceFailed event and still returns a
/// solution in which the project is present with no documents, so the open does not throw. Warnings and failures
/// that do not name a .NET project file (for example an unsupported project type) are ignored, so a healthy load
/// stays clean.
/// </summary>
internal sealed class ProjectLoadFailureCollector
{
    private static readonly string[] ProjectFileExtensions = [".csproj", ".vbproj", ".fsproj"];

    private readonly object _sync = new();
    private readonly List<string> _messages = new();

    /// <summary>True when at least one project-load failure was observed.</summary>
    public bool HasFailures
    {
        get
        {
            lock (_sync)
            {
                return _messages.Count > 0;
            }
        }
    }

    /// <summary>Records the diagnostic when it is a Failure that names a .NET project file; other diagnostics are ignored.</summary>
    public void Observe(WorkspaceDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
        {
            return;
        }

        var message = diagnostic.Message;
        // TODO this keys on the project file extension in the (localised) message text because WorkspaceDiagnostic carries no project id; a diagnostic that names no project file is missed rather than falsely flagged.
        if (!ProjectFileExtensions.Any(extension => message.Contains(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        lock (_sync)
        {
            if (!_messages.Contains(message))
            {
                _messages.Add(message);
            }
        }
    }

    /// <summary>The first distinct failure message (it names the project file); empty when nothing was observed.</summary>
    public string Describe()
    {
        lock (_sync)
        {
            return _messages.Count == 0 ? string.Empty : _messages[0];
        }
    }
}
