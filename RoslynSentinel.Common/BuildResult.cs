using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

public enum BuildOutcome { Succeeded, Failed, NotRun }

public record BuildResult(
    BuildOutcome Outcome,
    BuildVerifyLevel Level,
    List<string> ProjectsCompiled,
    bool DiagnosticsComplete,
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,
    List<DiagnosticInfo> Warnings,
    List<DiagnosticGroupSummary> ErrorSummary,
    List<DiagnosticGroupSummary> WarningSummary,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    int? ExitCode = null,
    string? Detail = null,
    BuildFailureBreakdown? Breakdown = null,
    int OmittedErrorCount = 0,
    int SuppressedDownstreamErrorCount = 0,
    string? FullDiagnosticsResultId = null,
    [property: JsonIgnore] BuildFullDiagnostics? FullDiagnostics = null) : IResultStatus
{
    /// <summary>True unless the build succeeded; a NotRun build gives no verification, so it is an error.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Outcome != BuildOutcome.Succeeded;

    /// <summary>Status derived from <see cref="IsError"/> and <see cref="Code"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    /// <summary>BuildFailed when the build failed; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => Outcome switch
    {
        BuildOutcome.Succeeded => null,
        BuildOutcome.Failed => ToolErrorCode.BuildFailed,
        _ => ToolErrorCode.Exception,
    };

    /// <summary>The failure detail when the build failed; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Outcome == BuildOutcome.Succeeded
        ? null
        : Detail ?? (Outcome == BuildOutcome.Failed ? $"Build failed with {ErrorCount} error(s)" : "Build was not run");
}

/// <summary>Error counts grouped by project and by file, plus the projects that are root causes.</summary>
public record BuildFailureBreakdown(List<BuildProjectErrorCount> ByProject, List<BuildFileErrorCount> ByFile);

/// <summary>Error count for one project in a failed build.</summary>
public record BuildProjectErrorCount(string Project, int ErrorCount, bool IsRootCause);

/// <summary>Error count for one file in a failed build.</summary>
public record BuildFileErrorCount(string File, int ErrorCount);

/// <summary>Uncapped diagnostic lists carried from the engine to the tool boundary; never serialised.</summary>
public record BuildFullDiagnostics(List<DiagnosticInfo> Errors, List<DiagnosticInfo> Warnings);
