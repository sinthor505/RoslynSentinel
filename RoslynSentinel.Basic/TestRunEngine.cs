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

public record ProjectTestSummary(
    string ProjectName,
    bool RunSucceeded,
    int TotalCount,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    string? Detail
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
    string? Detail = null,
    bool RunCompleted = true,
    List<ProjectTestSummary>? ProjectSummaries = null
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
        bool summary = false,
        CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;

        if (scope == ToolScope.file)
        {
            return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                error: new EngineError("scope=file is not supported by RunTest — there is no per-file test-execution unit in `dotnet test`. Use scope=project or scope=solution, optionally narrowed with filter."));
        }

        List<(string Name, string Path)> targets;
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

            targets = [(project.Name, project.FilePath)];
        }
        else
        {
            if (_workspaceManager.CurrentSolution is null && _workspaceManager.SolutionPath is null)
            {
                return new EngineResultWrapper<TestRunResult>(EngineOutcome.InvalidInput,
                    error: new EngineError("No solution is loaded. Call LoadSolution before running RunTest."));
            }

            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            targets = solution.Projects
                .Where(p => p.FilePath is not null && IsTestProject(p))
                .Select(p => (p.Name, p.FilePath!))
                .OrderBy(t => t.Name)
                .ToList();

            if (targets.Count == 0)
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
                    StdoutTail: null,
                    StderrTail: null,
                    Duration: DateTime.UtcNow - start,
                    Detail: "No test projects (referencing Microsoft.NET.Test.Sdk) were found in the loaded solution.",
                    RunCompleted: false
                ));
            }
        }

        // dotnet test run once per test project rather than once against the whole solution/target
        // list: `dotnet test <solution>` fans out internally into one vstest invocation per test
        // project, and every one of those sub-invocations was writing to the *same* shared
        // `--logger trx;LogFileName=...` path, so only the last project to finish survived in the
        // parsed result — every other project's counts were silently discarded. Giving each project
        // its own process and TRX file, then aggregating here, is what makes counts trustworthy for
        // scope=solution.
        var projectResults = new List<(TestRunResult Result, string ProjectName)>();
        foreach (var (name, path) in targets)
        {
            var result = await RunOneProjectAsync(name, path, filter, timeoutSeconds, cancellationToken);
            projectResults.Add((result, name));
        }

        var totalCount = projectResults.Sum(p => p.Result.TotalCount);
        var passedCount = projectResults.Sum(p => p.Result.PassedCount);
        var failedCount = projectResults.Sum(p => p.Result.FailedCount);
        var skippedCount = projectResults.Sum(p => p.Result.SkippedCount);
        var allRunCompleted = projectResults.All(p => p.Result.RunCompleted);
        var allExitZero = projectResults.All(p => p.Result.ExitCode == 0);

        var allCaseResults = projectResults.SelectMany(p => p.Result.Results).ToList();
        var failedCaseResults = allCaseResults.Where(r => r.Outcome == TestOutcome.Failed).ToList();

        var failureSummary = failedCaseResults
            .GroupBy(r => Signature(r.ErrorMessage))
            .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().TestName))
            .OrderByDescending(g => g.Count)
            .ToList();

        IEnumerable<TestCaseResult> filtered = resultsType switch
        {
            TestResultsFilter.failed => allCaseResults.Where(r => r.Outcome == TestOutcome.Failed),
            TestResultsFilter.skipped => allCaseResults.Where(r => r.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted),
            _ => allCaseResults,
        };

        var ordered = summary
            ? []
            : filtered
                .OrderBy(r => r.Outcome switch { TestOutcome.Failed => 0, TestOutcome.Skipped or TestOutcome.NotExecuted => 1, _ => 2 })
                .Take(maxDetails)
                .ToList();

        var projectSummaries = projectResults
            .Select(p => new ProjectTestSummary(
                ProjectName: p.ProjectName,
                RunSucceeded: p.Result.RunSucceeded,
                TotalCount: p.Result.TotalCount,
                PassedCount: p.Result.PassedCount,
                FailedCount: p.Result.FailedCount,
                SkippedCount: p.Result.SkippedCount,
                Detail: p.Result.Detail))
            .ToList();

        var combinedStdoutTail = string.Join(
            Environment.NewLine + Environment.NewLine,
            projectResults.Where(p => p.Result.StdoutTail is not null).Select(p => $"── {p.ProjectName} ──{Environment.NewLine}{p.Result.StdoutTail}"));
        var combinedStderrTail = string.Join(
            Environment.NewLine + Environment.NewLine,
            projectResults.Where(p => p.Result.StderrTail is not null).Select(p => $"── {p.ProjectName} ──{Environment.NewLine}{p.Result.StderrTail}"));

        string? overallDetail = null;
        if (!allRunCompleted)
        {
            overallDetail = "One or more projects did not complete their run: " +
                string.Join("; ", projectResults.Where(p => !p.Result.RunCompleted).Select(p => $"{p.ProjectName}: {p.Result.Detail}"));
        }
        else if (totalCount == 0)
        {
            overallDetail = !string.IsNullOrEmpty(filter)
                ? $"0 tests matched filter '{filter}' across {targets.Count} project(s)."
                : "No test projects found under the resolved scope.";
        }

        return new EngineResultWrapper<TestRunResult>(EngineOutcome.Success, new TestRunResult(
            RunSucceeded: allRunCompleted && allExitZero && failedCount == 0,
            ExitCode: allExitZero ? 0 : 1,
            TotalCount: totalCount,
            PassedCount: passedCount,
            FailedCount: failedCount,
            SkippedCount: skippedCount,
            FailureSummary: failureSummary,
            Results: ordered,
            StdoutTail: string.IsNullOrEmpty(combinedStdoutTail) ? null : combinedStdoutTail,
            StderrTail: string.IsNullOrEmpty(combinedStderrTail) ? null : combinedStderrTail,
            Duration: DateTime.UtcNow - start,
            Detail: overallDetail,
            RunCompleted: allRunCompleted,
            ProjectSummaries: projectSummaries
        ));
    }

    /// <summary>"Is this a test project" — checked via the project file referencing
    /// Microsoft.NET.Test.Sdk (the SDK package that actually makes `dotnet test` runnable), not via
    /// referencing nunit.framework: a project can pull in NUnit's assertion library transitively
    /// through a ProjectReference to a real test project (e.g. a benchmark/tool project referencing
    /// a Tests project for shared fixtures) without being a test project itself — `dotnet test`
    /// against such a project fails outright rather than running zero tests.</summary>
    private static bool IsTestProject(Microsoft.CodeAnalysis.Project project) =>
        project.FilePath is not null && File.Exists(project.FilePath) &&
        File.ReadAllText(project.FilePath).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs `dotnet test` against a single project with its own dedicated TRX file, and
    /// parses the result. Never throws — timeouts, missing TRX, and process failures are all
    /// reported via <see cref="TestRunResult.RunCompleted"/> and <see cref="TestRunResult.Detail"/>
    /// so a single failing project can't take down the aggregate result for the rest.</summary>
    private static async Task<TestRunResult> RunOneProjectAsync(
        string projectName,
        string projectPath,
        string? filter,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        var trxPath = Path.Combine(Path.GetTempPath(), $"roslynsentinel_runtest_{projectName}_{Guid.NewGuid():n}.trx");

        const int TailLines = 40;
        static string Tail(string text) => string.Join(Environment.NewLine, text.Split(Environment.NewLine).TakeLast(TailLines));

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(projectPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("test");
            process.StartInfo.ArgumentList.Add(projectPath);
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

            if (timeoutDetail is not null)
            {
                return new TestRunResult(
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
                    Detail: timeoutDetail,
                    RunCompleted: false
                );
            }

            if (!File.Exists(trxPath))
            {
                var noTrxDetail = lockDetail ?? "No TRX result file was produced — the run failed before any test adapter reported results.";
                return new TestRunResult(
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
                    Detail: noTrxDetail,
                    RunCompleted: false
                );
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
                    : "Project produced no test results.";
            }

            var failureSummary = failedResults
                .GroupBy(r => Signature(r.ErrorMessage))
                .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().TestName))
                .OrderByDescending(g => g.Count)
                .ToList();

            return new TestRunResult(
                RunSucceeded: process.ExitCode == 0 && failedCount == 0,
                ExitCode: process.ExitCode,
                TotalCount: totalCount,
                PassedCount: passedCount,
                FailedCount: failedCount,
                SkippedCount: skippedCount,
                FailureSummary: failureSummary,
                Results: allResults,
                StdoutTail: Tail(stdoutText),
                StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
                Duration: DateTime.UtcNow - start,
                Detail: detail
            );
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
