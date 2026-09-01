namespace RoslynSentinel.Common;

public record BuildResult(
    bool BuildSucceeded,
    BuildVerifyLevel Level,
    int ExitCode,
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,
    List<DiagnosticInfo> Warnings,
    List<DiagnosticGroupSummary> ErrorSummary,
    List<DiagnosticGroupSummary> WarningSummary,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    string? Detail = null
);
