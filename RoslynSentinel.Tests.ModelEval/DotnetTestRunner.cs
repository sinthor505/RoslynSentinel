using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Result of a single `dotnet test` invocation, parsed from the console summary line
/// (e.g. "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 20 ms").
/// </summary>
internal sealed record DotnetTestResult(int Passed, int Failed, int Skipped, int Total, int ExitCode, string RawOutput);

/// <summary>
/// Runs `dotnet test` against a real test project (e.g. ContosoOrders.Tests) and parses its
/// pass/fail/skip counts from the captured console output. Used as a before/after baseline diff
/// to verify unrelated code still behaves correctly after a model's edit — replacing brittle
/// text/whitespace matching (see docs/current/modeleval_fixture_test_suite_redesign.md) with a
/// real test run, the same way a developer would actually check "did I break anything."
///
/// Deliberately a separate process invocation rather than routing through this repo's own
/// RunTest MCP tool, so a bug in RunTest can't mask a real fixture failure — the agent under
/// test already exercises RunTest live during its own run.
/// </summary>
internal static class DotnetTestRunner
{
    private static readonly Regex SummaryLineRegex = new(
        @"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Runs `dotnet test` against <paramref name="testProjectPath"/> and returns the parsed
    /// counts. Does NOT throw on a nonzero exit code — a failing test run is an expected,
    /// meaningful result here, not an infrastructure error. Only throws if the summary line
    /// can't be found at all (e.g. the project failed to build before any test ran) or the
    /// process fails to start.
    /// </summary>
    public static async Task<DotnetTestResult> RunAsync(string testProjectPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet", $"test \"{testProjectPath}\" -c Debug --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("DotnetTestRunner: failed to start dotnet test.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdOutTask + await stdErrTask;

        var match = SummaryLineRegex.Match(output);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"DotnetTestRunner: could not find a test summary line in dotnet test output for " +
                $"'{testProjectPath}' (exit code {process.ExitCode}) — likely a build error before any test ran:\n{output}");
        }

        return new DotnetTestResult(
            Passed: int.Parse(match.Groups["passed"].Value),
            Failed: int.Parse(match.Groups["failed"].Value),
            Skipped: int.Parse(match.Groups["skipped"].Value),
            Total: int.Parse(match.Groups["total"].Value),
            ExitCode: process.ExitCode,
            RawOutput: output);
    }
}
