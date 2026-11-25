namespace RoslynSentinel.Common;

/// <summary>How a <see cref="GitProcess"/> run ended. Caller cancellation is not an outcome: it throws <see cref="OperationCanceledException"/>.</summary>
internal enum GitRunOutcome
{
    /// <summary>git ran to completion and its output was read; the exit code may still be non-zero.</summary>
    Completed,

    /// <summary>The timeout elapsed before git exited; the process tree was killed.</summary>
    TimedOut,

    /// <summary>git exited but stdout/stderr did not reach EOF within the drain grace (a grandchild holds the pipe).</summary>
    OutputNotDrained,

    /// <summary>git could not be started or its pipes broke (missing exe, bad working directory, I/O error).</summary>
    Failed,
}

/// <summary>
/// Result of one <see cref="GitProcess.Run"/> / <see cref="GitProcess.RunAsync"/>. ExitCode, StdOut, StdOutBytes and
/// StdErr are only meaningful when <see cref="Outcome"/> is <see cref="GitRunOutcome.Completed"/>; otherwise
/// <see cref="FailureDetail"/> says why. StdOut is filled in text mode; StdOutBytes only when
/// <see cref="GitRunOptions.StdOutAsBytes"/> was set (StdOut is then empty).
/// </summary>
internal readonly record struct GitRunResult(
    GitRunOutcome Outcome,
    int ExitCode,
    string StdOut,
    byte[] StdOutBytes,
    string StdErr,
    string? FailureDetail)
{
    internal bool Completed => Outcome == GitRunOutcome.Completed;

    internal static GitRunResult Failure(GitRunOutcome outcome, string detail) =>
        new(outcome, -1, string.Empty, [], string.Empty, detail);
}
