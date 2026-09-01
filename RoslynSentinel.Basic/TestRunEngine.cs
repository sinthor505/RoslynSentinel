using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace RoslynSentinel.Basic;

public record TestCaseResult(
    string TestName,
    TestOutcome Outcome,
    TimeSpan Duration,
    string? ErrorMessage,
    string? ErrorStackTrace
);

public record TestRunResult(
    bool RunSucceeded,
    int ExitCode,
    int TotalCount,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    List<GroupedCountSummary> FailureSummary,
    List<TestCaseResult> Results,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    string? Detail = null
);

public class TestRunEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public TestRunEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<EngineResultWrapper<TestRunResult>> RunAsync(
        ToolScope scope,
        string? scopeName,
        string? filter,
        TestResultsFilter resultsType,
        int maxDetails,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;

        if (scope == ToolScope.file)
        {
            return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                error: new EngineError("scope=file is not supported by RunTest — there is no per-file test-execution unit in `dotnet test`. Use scope=project or scope=solution, optionally narrowed with filter."));
        }

        string targetPath;
        if (scope == ToolScope.project)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                    error: new EngineError("scopeName (projectName) is required when scope=project."));
            }

            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var project = solution.Projects.FirstOrDefault(p => p.Name == scopeName);
            if (project?.FilePath is null)
            {
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                    error: new EngineError($"Project '{scopeName}' was not found in the loaded solution."));
            }

            targetPath = project.FilePath;
        }
        else
        {
            var solutionPath = _workspaceManager.CurrentSolution?.FilePath ?? _workspaceManager.SolutionPath;
            if (string.IsNullOrEmpty(solutionPath))
            {
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                    error: new EngineError("No solution is loaded. Call LoadSolution before running RunTest."));
            }

            targetPath = solutionPath;
        }

        var trxPath = Path.Combine(Path.GetTempPath(), $"roslynsentinel_runtest_{Guid.NewGuid():n}.trx");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(targetPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("test");
            process.StartInfo.ArgumentList.Add(targetPath);
            process.StartInfo.ArgumentList.Add("--nologo");
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("quiet");
            process.StartInfo.ArgumentList.Add("--logger");
            process.StartInfo.ArgumentList.Add($"trx;LogFileName={trxPath}");
            if (!string.IsNullOrEmpty(filter))
            {
                process.StartInfo.ArgumentList.Add("--filter");
                process.StartInfo.ArgumentList.Add(filter);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            string? timeoutDetail = null;
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort — process may have already exited between the timeout firing and the kill.
                }
                timeoutDetail = $"Test run exceeded {timeoutSeconds}s and was terminated.";
            }

            var stdoutText = stdout.ToString();
            var stderrText = stderr.ToString();

            string? lockDetail = null;
            if (stderrText.Contains("MSB3027") || stdoutText.Contains("MSB3027") ||
                stderrText.Contains("MSB3021") || stdoutText.Contains("MSB3021"))
            {
                lockDetail = "Build failed to copy the output file — it is likely locked by a running process (e.g. this MCP server or an IDE holding the binary). Close the process holding the file and retry.";
            }

            const int TailLines = 40;
            static string Tail(string text) => string.Join(Environment.NewLine, text.Split(Environment.NewLine).TakeLast(TailLines));

            if (timeoutDetail is not null)
            {
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.Success, new TestRunResult(
                    RunSucceeded: false,
                    ExitCode: -1,
                    TotalCount: 0,
                    PassedCount: 0,
                    FailedCount: 0,
                    SkippedCount: 0,
                    FailureSummary: [],
                    Results: [],
                    StdoutTail: Tail(stdoutText),
                    StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
                    Duration: DateTime.UtcNow - start,
                    Detail: timeoutDetail
                ));
            }

            if (!File.Exists(trxPath))
            {
                var noTrxDetail = lockDetail ?? "No TRX result file was produced — no test projects were found under the resolved scope, or the run failed before any test adapter reported results.";
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.Success, new TestRunResult(
                    RunSucceeded: false,
                    ExitCode: process.ExitCode,
                    TotalCount: 0,
                    PassedCount: 0,
                    FailedCount: 0,
                    SkippedCount: 0,
                    FailureSummary: [],
                    Results: [],
                    StdoutTail: Tail(stdoutText),
                    StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
                    Duration: DateTime.UtcNow - start,
                    Detail: noTrxDetail
                ));
            }

            var allResults = ParseTrx(trxPath);

            var totalCount = allResults.Count;
            var passedCount = allResults.Count(r => r.Outcome == TestOutcome.Passed);
            var failedResults = allResults.Where(r => r.Outcome == TestOutcome.Failed).ToList();
            var failedCount = failedResults.Count;
            var skippedCount = allResults.Count(r => r.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted);

            string? detail = lockDetail;
            if (detail is null && totalCount == 0)
            {
                detail = !string.IsNullOrEmpty(filter)
                    ? $"0 tests matched filter '{filter}'."
                    : "No test projects found under the resolved scope.";
            }

            var failureSummary = failedResults
                .GroupBy(r => Signature(r.ErrorMessage))
                .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().TestName))
                .OrderByDescending(g => g.Count)
                .ToList();

            IEnumerable<TestCaseResult> filtered = resultsType switch
            {
                TestResultsFilter.failed => allResults.Where(r => r.Outcome == TestOutcome.Failed),
                TestResultsFilter.skipped => allResults.Where(r => r.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted),
                _ => allResults,
            };

            var ordered = filtered
                .OrderBy(r => r.Outcome switch { TestOutcome.Failed => 0, TestOutcome.Skipped or TestOutcome.NotExecuted => 1, _ => 2 })
                .Take(maxDetails)
                .ToList();

            return new EngineResultWrapper<TestRunResult>(EngineOutcome.Success, new TestRunResult(
                RunSucceeded: process.ExitCode == 0 && failedCount == 0,
                ExitCode: process.ExitCode,
                TotalCount: totalCount,
                PassedCount: passedCount,
                FailedCount: failedCount,
                SkippedCount: skippedCount,
                FailureSummary: failureSummary,
                Results: ordered,
                StdoutTail: Tail(stdoutText),
                StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
                Duration: DateTime.UtcNow - start,
                Detail: detail
            ));
        }
        finally
        {
            try
            {
                if (File.Exists(trxPath))
                {
                    File.Delete(trxPath);
                }
            }
            catch
            {
                // Best-effort cleanup — a leftover temp file is not worth failing the call over.
            }
        }
    }

    /// <summary>Derives a grouping key from a test failure message: uses the message's own
    /// first line as the signature, capped to keep it short.</summary>
    private static string Signature(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "(no message)";
        }

        var firstLine = errorMessage.Split('\n', 2)[0].TrimEnd('\r');
        return firstLine.Length > 120 ? firstLine[..120] : firstLine;
    }

    private static List<TestCaseResult> ParseTrx(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        XNamespace ns = doc.Root?.Name.Namespace ?? "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        var results = new List<TestCaseResult>();
        foreach (var unitTestResult in doc.Descendants(ns + "UnitTestResult"))
        {
            var testName = unitTestResult.Attribute("testName")?.Value ?? "(unknown test)";
            var outcomeText = unitTestResult.Attribute("outcome")?.Value ?? "NotExecuted";
            var outcome = outcomeText switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" => TestOutcome.Failed,
                "NotExecuted" => TestOutcome.NotExecuted,
                _ => TestOutcome.Skipped,
            };

            var durationText = unitTestResult.Attribute("duration")?.Value;
            var duration = TimeSpan.TryParse(durationText, out var d) ? d : TimeSpan.Zero;

            var errorInfo = unitTestResult.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            var message = errorInfo?.Element(ns + "Message")?.Value;
            var stackTrace = errorInfo?.Element(ns + "StackTrace")?.Value;

            results.Add(new TestCaseResult(
                TestName: testName,
                Outcome: outcome,
                Duration: duration,
                ErrorMessage: message,
                ErrorStackTrace: stackTrace
            ));
        }

        return results;
    }
}
