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
    string? Detail = null);
