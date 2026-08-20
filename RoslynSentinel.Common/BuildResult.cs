namespace RoslynSentinel.Common;

public record BuildResult(
    bool BuildSucceeded,
    BuildVerifyLevel Level,
    int ExitCode,
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,
    List<DiagnosticInfo> Warnings,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    string? Detail = null
);
