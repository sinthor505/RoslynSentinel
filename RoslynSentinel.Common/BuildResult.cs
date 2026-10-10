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
    [property: JsonIgnore] BuildFullDiagnostics? FullDiagnostics = null);

/// <summary>Error counts grouped by project and by file, plus the projects that are root causes.</summary>
public record BuildFailureBreakdown(List<BuildProjectErrorCount> ByProject, List<BuildFileErrorCount> ByFile);

/// <summary>Error count for one project in a failed build.</summary>
public record BuildProjectErrorCount(string Project, int ErrorCount, bool IsRootCause);

/// <summary>Error count for one file in a failed build.</summary>
public record BuildFileErrorCount(string File, int ErrorCount);

/// <summary>Uncapped diagnostic lists carried from the engine to the tool boundary; never serialised.</summary>
public record BuildFullDiagnostics(List<DiagnosticInfo> Errors, List<DiagnosticInfo> Warnings);
