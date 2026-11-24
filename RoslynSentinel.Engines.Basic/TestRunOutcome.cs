namespace RoslynSentinel.Engines.Basic;

/// <summary>How a RunTest run (or one project's run) ended. Serialized as a string by the shared JSON options (like BuildOutcome).
/// No BuildFailed value: `dotnet test` builds implicitly, so a build failure is not structurally distinguishable from a
/// missing TRX file; both are <see cref="RunError"/>.</summary>
public enum TestRunOutcome
{
    /// <summary>Completed, exit code 0, at least one test ran, none failed.</summary>
    Passed,

    /// <summary>At least one test failed (the run produced a TRX).</summary>
    TestsFailed,

    /// <summary>Completed with exit code 0 but no test ran (a filter that matched nothing, or no test projects).</summary>
    NoTestsMatched,

    /// <summary>The test process exceeded the timeout and was terminated.</summary>
    TimedOut,

    /// <summary>The run did not produce a usable result: blame-hang abort, no TRX file, or a non-zero exit with no failed test (fixture setup/teardown error, test host crash).</summary>
    RunError,
}
