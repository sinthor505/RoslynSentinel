using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using RoslynSentinel.Git;

namespace RoslynSentinel.Engines.Basic;

public record TestCaseResult(
    string TestName,
    TestOutcome Outcome,
    TimeSpan Duration,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? Output = null
);

public record ProjectTestSummary(
    string ProjectName,
    TestRunOutcome Outcome,
    int TotalCount,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    string? Detail
) : IOperationResult
{
    /// <summary>True when the project's run failed a test, timed out or errored; NoTestsMatched is not an error.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Outcome is not (TestRunOutcome.Passed or TestRunOutcome.NoTestsMatched);

    /// <summary>Failed when IsError; otherwise Success.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    /// <summary>ToolErrorCode.TestRunFailed for TestsFailed; ToolErrorCode.TestRunIncomplete for TimedOut and RunError; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => Outcome switch
    {
        TestRunOutcome.TestsFailed => ToolErrorCode.TestRunFailed,
        TestRunOutcome.TimedOut or TestRunOutcome.RunError => ToolErrorCode.TestRunIncomplete,
        _ => null,
    };

    /// <summary>The detail, or a fallback text, when IsError; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => IsError ? (Detail ?? "Test run failed") : null;
}

public record TestRunResult(
    TestRunOutcome Outcome,
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
    List<ProjectTestSummary>? ProjectSummaries = null,
    int OmittedResultCount = 0,
    // The suite the run used (fast skips the OutOfProcess fixtures); set by the RunTest tool, null for an engine-level run.
    TestSuite? Suite = null,
    // Set by the RunTest tool on a completed fast run when the full suite is stale; never on a full run.
    FullSuiteStale? FullSuiteStale = null,
    // Full sha of the worktree's HEAD; set by the RunTest tool only when repoPath was supplied.
    string? WorktreeCommit = null
) : IOperationResult
{
    /// <summary>True unless the outcome is Passed or NoTestsMatched (a zero-match run stays a non-error, decision 2026-11-11).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Outcome is not (TestRunOutcome.Passed or TestRunOutcome.NoTestsMatched);

    /// <summary>False when the run timed out or errored (no usable result); true for Passed, TestsFailed and NoTestsMatched.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RanToCompletion => Outcome is not (TestRunOutcome.TimedOut or TestRunOutcome.RunError);

    /// <summary>Failed when IsError; otherwise Success.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    /// <summary>ToolErrorCode.TestRunIncomplete for TimedOut and RunError; ToolErrorCode.TestRunFailed for TestsFailed; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => Outcome switch
    {
        TestRunOutcome.TimedOut or TestRunOutcome.RunError => ToolErrorCode.TestRunIncomplete,
        TestRunOutcome.TestsFailed => ToolErrorCode.TestRunFailed,
        _ => null,
    };

    /// <summary>Detail, or a count or incomplete-run text, when IsError; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => !IsError ? null : (Detail ?? (Outcome == TestRunOutcome.TestsFailed ? $"{FailedCount} of {TotalCount} tests failed" : "Test run did not complete"));
}

public class TestRunEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private const int MaxTestOutputChars = 2000;

    public TestRunEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>Prefix of the stderr line `dotnet test` prints when a `[SetUpFixture]` `[OneTimeTearDown]` throws.</summary>
    public const string TearDownFailureSignaturePrefix = "TearDown failed for test fixture ";

    /// <summary>Adapter message fragment that NUnit's test executor writes when a `--filter` expression is malformed.</summary>
    public const string InvalidFilterSignature = "Incorrect format for TestCaseFilter";

    /// <summary>Runs `dotnet test` per test project into this server instance's .roslynsentinel/build/&lt;instance&gt;/
    /// artifacts directory (always; the projects' own bin/obj are not written) while holding the server-wide
    /// <see cref="BuildGate"/> for the whole run. <paramref name="useScratchDir"/> is obsolete and ignored; it stays
    /// so existing callers compile. (No [Obsolete] attribute: it would raise CS0618 at every caller for a no-op.)</summary>
    /// <param name="repoRoot">A validated git worktree root (see <see cref="RepoWorktrees.TryResolve"/>) of the loaded solution's repository, or null for the loaded tree. When set, each test project runs from the worktree's copy of its project file (same repository-relative path) and the artifacts directory is the worktree's own .roslynsentinel/build/&lt;instance&gt;/, so the loaded tree's bin/obj and output are untouched. scopeName still names a loaded-solution project.</param>
    public async Task<TestRunResult> RunAsync(
        ToolScope scope,
        string? scopeName,
        string? filter,
        TestResultsFilter resultsType,
        int maxDetails,
        int timeoutSeconds,
        bool summary = false,
        bool useScratchDir = false,
        IReadOnlyDictionary<string, string>? environment = null,
        TestSuite suite = TestSuite.fast,
        string? repoRoot = null,
        CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;

        if (scope == ToolScope.file)
        {
            throw new ToolInvalidArgumentException("scope=file is not supported by RunTest - there is no per-file test-execution unit in `dotnet test`. Use scope=project or scope=solution, optionally narrowed with filter.");
        }

        List<(string Name, string Path)> targets;
        if (scope == ToolScope.project)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                throw new ToolInvalidArgumentException("scopeName (projectName) is required when scope=project.");
            }

            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var project = ProjectLookup.FindProject(solution, scopeName);
            if (project is null)
            {
                throw new ToolNotFoundException(ProjectNotFoundDetail.Build(scopeName, solution.Projects.Select(p => p.Name).ToList(), "scopeName"), missingTargetKind: MissingTargetKinds.Project, missingTargetName: scopeName);
            }

            if (project.FilePath is null)
            {
                throw new TestRunNotRunException($"Project '{project.Name}' is loaded but has no project file path (it is an in-memory project), so `dotnet test` has no project file to run.");
            }

            targets = [(project.Name, project.FilePath)];
        }
        else
        {
            Microsoft.CodeAnalysis.Solution solution;
            try
            {
                solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            }
            catch (SolutionNotLoadedException ex)
            {
                throw new SolutionNotLoadedException($"{ex.Message} RunTest needs a loaded solution.");
            }

            targets = solution.Projects
                .Where(p => p.FilePath is not null && TestProjectDetector.IsTestProject(p))
                .Select(p => (p.Name, p.FilePath!))
                .OrderBy(t => t.Name)
                .ToList();

            if (targets.Count == 0)
            {
                throw new NoProjectsException("No test projects (referencing Microsoft.NET.Test.Sdk) were found in the loaded solution.");
            }
        }

        // dotnet test run once per test project rather than once against the whole solution/target
        // list: `dotnet test <solution>` fans out internally into one vstest invocation per test
        // project, and every one of those sub-invocations was writing to the *same* shared
        // `--logger trx;LogFileName=...` path, so only the last project to finish survived in the
        // parsed result -> every other project's counts were silently discarded. Giving each project
        // its own process and TRX file, then aggregating here, is what makes counts trustworthy for
        // scope=solution.
        var solutionPath = _workspaceManager.SolutionPath;
        if (repoRoot is not null)
        {
            // Worktree run: the loaded tree's project files become the worktree's copies, and the artifacts directory follows the
            // worktree's solution directory (when the loaded solution has no counterpart there, the first test project's directory).
            var worktreeTargets = new List<(string Name, string Path)>();
            foreach (var (name, path) in targets)
            {
                if (!RepoWorktrees.TryMapExisting(path, repoRoot, out var worktreeProjectPath, out var mapError))
                {
                    throw new TestRunNotRunException($"Test project '{name}': {mapError}");
                }

                worktreeTargets.Add((name, worktreeProjectPath));
            }

            targets = worktreeTargets;
            var worktreeSolutionPath = string.IsNullOrEmpty(solutionPath) ? null : RepoWorktrees.MapToWorktree(solutionPath, repoRoot);
            solutionPath = worktreeSolutionPath is not null && File.Exists(worktreeSolutionPath) ? worktreeSolutionPath : null;
        }

        var artifactsSolutionDirectory = Path.GetDirectoryName(string.IsNullOrEmpty(solutionPath) ? targets[0].Path : solutionPath)!;

        // The gate is taken once for the whole run (not per project); RunOneProjectAsync never acquires it,
        // so there is no nested acquisition. No competing-build probe: the run writes only its own
        // instance directory, and in-server runs are serialised by the gate.
        BuildGateLease gateLease;
        try
        {
            gateLease = await BuildGate.Shared.AcquireAsync("RunTest", BuildGate.DefaultMaxWait, cancellationToken);
        }
        catch (BuildGateBusyException busy)
        {
            throw new TestRunNotRunException(busy.Message);
        }

        var projectResults = new List<(TestRunResult Result, string ProjectName)>();
        using (gateLease)
        {
            foreach (var (name, path) in targets)
            {
                var result = await RunOneProjectAsync(name, path, filter, suite, timeoutSeconds, artifactsSolutionDirectory, environment, cancellationToken);
                projectResults.Add((result, name));
            }
        }

        var totalCount = projectResults.Sum(p => p.Result.TotalCount);
        var passedCount = projectResults.Sum(p => p.Result.PassedCount);
        var failedCount = projectResults.Sum(p => p.Result.FailedCount);
        var skippedCount = projectResults.Sum(p => p.Result.SkippedCount);
        var allRunCompleted = projectResults.All(p => p.Result.RanToCompletion);
        var overallOutcome = AggregateOutcome(projectResults.Select(p => p.Result.Outcome), totalCount);
        var allExitZero = projectResults.All(p => p.Result.ExitCode == 0);

        var allCaseResults = projectResults.SelectMany(p => p.Result.Results).ToList();
        var failedCaseResults = allCaseResults.Where(r => r.Outcome == TestOutcome.Failed).ToList();

        var testGroups = failedCaseResults
            .GroupBy(r => Signature(r.ErrorMessage))
            .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().TestName))
            .OrderByDescending(g => g.Count)
            .ToList();
        // Per-project test failures are already counted above from failedCaseResults; the fixture
        // TearDown entries (absent from the TRX) are taken from the per-project summaries and prepended.
        var teardownEntries = projectResults
            .SelectMany(p => p.Result.FailureSummary)
            .Where(s => s.Signature.StartsWith(TearDownFailureSignaturePrefix, StringComparison.Ordinal))
            .ToList();
        var failureSummary = PrependTearDownFailures(testGroups, teardownEntries);

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
                Outcome: p.Result.Outcome,
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
                string.Join("; ", projectResults.Where(p => !p.Result.RanToCompletion).Select(p => $"{p.ProjectName}: {p.Result.Detail}"));
        }
        else if (totalCount == 0)
        {
            overallDetail = !string.IsNullOrEmpty(filter)
                ? $"0 tests matched filter '{filter}' across {targets.Count} project(s)."
                    + (TestSuiteFilter.ZeroMatchHint(suite, filter, totalCount, scope, scopeName) is { } zeroMatchHint ? " " + zeroMatchHint : "")
                : "No test projects found under the resolved scope.";
        }

        return new TestRunResult(
            Outcome: overallOutcome,
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
            ProjectSummaries: projectSummaries
        );
    }

    /// <summary>Classifies one project's run. Precedence: TimedOut, then RunError (blame hang, no TRX, or a non-zero exit with no failed test),
    /// then TestsFailed (failedCount above 0), then NoTestsMatched (totalCount 0), then Passed.</summary>
    public static TestRunOutcome ClassifyOutcome(int exitCode, int failedCount, int totalCount, bool hung, bool timedOut, bool trxExists)
    {
        if (timedOut)
        {
            return TestRunOutcome.TimedOut;
        }

        if (hung || !trxExists || (exitCode != 0 && failedCount == 0))
        {
            return TestRunOutcome.RunError;
        }

        if (failedCount > 0)
        {
            return TestRunOutcome.TestsFailed;
        }

        return totalCount == 0 ? TestRunOutcome.NoTestsMatched : TestRunOutcome.Passed;
    }

    /// <summary>Combines per-project outcomes: TimedOut if any project timed out, else RunError if any errored, else TestsFailed if any project
    /// failed a test, else NoTestsMatched when no test ran in total, else Passed.</summary>
    public static TestRunOutcome AggregateOutcome(IEnumerable<TestRunOutcome> projectOutcomes, int totalCount)
    {
        var outcomes = projectOutcomes.ToList();
        if (outcomes.Contains(TestRunOutcome.TimedOut))
        {
            return TestRunOutcome.TimedOut;
        }

        if (outcomes.Contains(TestRunOutcome.RunError))
        {
            return TestRunOutcome.RunError;
        }

        if (outcomes.Contains(TestRunOutcome.TestsFailed))
        {
            return TestRunOutcome.TestsFailed;
        }

        return totalCount == 0 ? TestRunOutcome.NoTestsMatched : TestRunOutcome.Passed;
    }

    /// <summary>Detects a build-output file lock (MSB3021/MSB3026/MSB3027) in `dotnet test` output.
    /// Returns an actionable detail message, or null when no lock error is present.</summary>
    public static string? DetectFileLock(string stdout, string stderr)
    {
        foreach (var code in new[] { "MSB3021", "MSB3026", "MSB3027" })
        {
            if (stdout.Contains(code, StringComparison.Ordinal) || stderr.Contains(code, StringComparison.Ordinal))
            {
                return "Build failed to copy the output file - it is likely locked by a running process (e.g. this MCP server or an IDE holding the binary). Close the process holding the file and retry. A running Visual Studio Test Explorer testhost is a common holder.";
            }
        }

        return null;
    }

    /// <summary>Arguments that make `dotnet test` abort a hung test host after a per-test timeout
    /// (`--blame-hang`), so one hung test cannot consume the whole per-project timeout. The per-test
    /// timeout is kept slightly under the per-project <paramref name="timeoutSeconds"/> so the abort
    /// fires first, with a 30s floor. No dump is written (`--blame-hang-dump-type none`).</summary>
    public static IReadOnlyList<string> BlameHangArguments(int timeoutSeconds)
    {
        return
        [
            "--blame-hang",
            "--blame-hang-dump-type",
            "none",
            "--blame-hang-timeout",
            $"{Math.Max(30, timeoutSeconds - 5)}s",
        ];
    }

    /// <summary>Extracts the name of the test that was running when `dotnet test --blame-hang` aborted a
    /// hung test host. The name is taken from the marker line or, when the marker line ends without
    /// one, from the next non-blank line. Returns null when the marker is absent or no name follows.</summary>
    public static string? ParseHungTestName(string stdout)
    {
        const string Marker = "The test running when the crash occurred:";
        var lines = stdout.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf(Marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var sameLine = lines[i][(index + Marker.Length)..].Trim();
            if (sameLine.Length > 0)
            {
                return sameLine;
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (next.Length > 0)
                {
                    return next;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Parses `[OneTimeTearDown]` failures of a `[SetUpFixture]` out of `dotnet test` stderr. Those failures
    /// never reach the TRX, so each one becomes a grouped summary entry (merged by fixture and message).</summary>
    public static List<GroupedCountSummary> ParseFixtureTearDownFailures(string stderr)
    {
        var entries = new List<(string Signature, string Fixture)>();
        if (string.IsNullOrEmpty(stderr))
        {
            return [];
        }

        var lines = stderr.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith(TearDownFailureSignaturePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fixture = line[TearDownFailureSignaturePrefix.Length..].Trim();
            var messageLines = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (next.Length == 0 || next.StartsWith("StackTrace:", StringComparison.Ordinal))
                {
                    break;
                }

                messageLines.Add(next);
            }

            var signature = $"{TearDownFailureSignaturePrefix}{fixture}: {string.Join(" ", messageLines)}";
            if (signature.Length > 200)
            {
                signature = signature[..197] + "...";
            }

            entries.Add((signature, fixture));
        }

        return entries
            .GroupBy(e => e.Signature)
            .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().Fixture))
            .ToList();
    }

    /// <summary>Fixture teardown entries go first so TestRunResultProjector.MaxFailureGroups cannot drop them when there are many failing-test groups.</summary>
    public static List<GroupedCountSummary> PrependTearDownFailures(List<GroupedCountSummary> testFailureGroups, IEnumerable<GroupedCountSummary> tearDownFailures)
    {
        var merged = new List<GroupedCountSummary>(tearDownFailures);
        merged.AddRange(testFailureGroups);
        return merged;
    }

    /// <summary>Returns the detail text for a run whose `--filter` the test adapter rejected (non-zero exit, adapter
    /// signature in stderr or stdout), or null when the filter was not rejected. Names the adapter line only, never
    /// a stack trace.</summary>
    public static string? InvalidFilterDetail(string? filter, int exitCode, string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(filter) || exitCode == 0)
        {
            return null;
        }

        static string? SignatureLine(string text) => text
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains(InvalidFilterSignature, StringComparison.Ordinal));

        var line = SignatureLine(stderr) ?? SignatureLine(stdout);
        if (line is null)
        {
            return null;
        }

        if (line.Length > 300)
        {
            line = line[..300];
        }

        return $"The test adapter rejected the filter '{filter}' (dotnet test exited with code {exitCode}); no test ran. Adapter message: {line} The filter is a `dotnet test --filter` expression such as FullyQualifiedName~ClassName or FullyQualifiedName~Namespace.Class.Method.";
    }

    /// <summary>The detail text for a run that exited without writing a TRX (and had no file lock or rejected filter to name): the exit code,
    /// and when stdout and stderr were both empty the known cause of a silent exit plus the ways to confirm it. Facts and options only.</summary>
    public static string NoTrxDetail(int exitCode, string stdout, string stderr)
    {
        var detail = $"No TRX result file was produced - the run failed before any test adapter reported results. dotnet test exited with code {exitCode}.";
        if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
        {
            detail += " It printed nothing to stdout or stderr. One known cause of a silent exit is an MSBuild worker node handshake failure when the SDK dotnet runs differs from the SDK its MSBuild nodes start from"
                + " (for example a worktree without global.json, which runs the newest installed SDK). Options: `dotnet --version` in the project directory shows the SDK dotnet selects, and global.json in the repo or worktree decides it;"
                + " MSBUILDDEBUGCOMM=1 plus MSBUILDDEBUGPATH=<folder> in the environment parameter of this tool writes each node's handshake trace into that folder.";
        }

        return detail;
    }

    /// <summary>Runs `dotnet test` against a single project with its own dedicated TRX file, and
    /// parses the result. Never throws -> timeouts, missing TRX, and process failures are all
    /// reported via <see cref="TestRunResult.Outcome"/> and <see cref="TestRunResult.Detail"/>
    /// so a single failing project can't take down the aggregate result for the rest.</summary>
    private static async Task<TestRunResult> RunOneProjectAsync(
        string projectName,
        string projectPath,
        string? filter,
        TestSuite suite,
        int timeoutSeconds,
        string artifactsSolutionDirectory,
        IReadOnlyDictionary<string, string>? environment,
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
            foreach (var blameArgument in BlameHangArguments(timeoutSeconds))
            {
                process.StartInfo.ArgumentList.Add(blameArgument);
            }
            // The server's MSBuildLocator pins (MSBUILD_EXE_PATH and friends) must not reach dotnet test: they send its MSBuild worker
            // nodes to the server's SDK while dotnet runs the SDK global.json selects (the newest installed one in a worktree that
            // lacks global.json), and the node handshake then fails with no output and no TRX.
            BuildIsolation.RemoveInheritedMsBuildPins(process.StartInfo);
            // Caller env is applied after the pin removal (so a caller can set one on purpose) and before ArtifactsPath, which still wins.
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    if (string.IsNullOrEmpty(key)) { continue; }
                    process.StartInfo.Environment[key] = value;
                }
            }
            BuildIsolation.ApplyArtifactsPath(process.StartInfo, artifactsSolutionDirectory);
            // The fast suite adds the OutOfProcess exclusion only for NUnit projects: another adapter (xunit) rejects the TestCategory property.
            // `filter` stays the caller's text, which the zero-match messages below quote.
            var effectiveFilter = TestProjectDetector.UsesNUnitAdapter(projectPath) ? TestSuiteFilter.Compose(filter, suite) : filter;
            if (!string.IsNullOrEmpty(effectiveFilter))
            {
                process.StartInfo.ArgumentList.Add("--filter");
                process.StartInfo.ArgumentList.Add(effectiveFilter);
            }

            process.Start();

            // Waits for process exit, not pipe EOF: a grandchild holding the pipes (a reused MSBuild node, a
            // test host child) must not keep the run alive after dotnet exited. See ChildProcessCapture.
            var capture = await ChildProcessCapture.RunAsync(process, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

            string? timeoutDetail = null;
            if (capture.TimedOut)
            {
                var exitState = capture.RootExited
                    ? $"the test process had exited, exit code {capture.ExitCode}"
                    : "the test process had not exited";
                timeoutDetail = $"Test run exceeded {timeoutSeconds}s and was terminated ({exitState}).";
            }

            var stdoutText = capture.StdOut;
            var hungTestName = ParseHungTestName(stdoutText);
            string? hungDetail = hungTestName is null ? null : $"Test host aborted after the per-test hang timeout; the test running was: {hungTestName}.";
            var stderrText = capture.StdErr;

            var lockDetail = DetectFileLock(stdoutText, stderrText);

            if (timeoutDetail is not null)
            {
                return new TestRunResult(
                    Outcome: ClassifyOutcome(exitCode: -1, failedCount: 0, totalCount: 0, hung: hungTestName is not null, timedOut: true, trxExists: File.Exists(trxPath)),
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
                    Detail: hungTestName is null ? timeoutDetail : $"{timeoutDetail} The test running when it hung: {hungTestName}."
                );
            }

            if (!File.Exists(trxPath))
            {
                var noTrxDetail = lockDetail ?? InvalidFilterDetail(filter, process.ExitCode, stdoutText, stderrText) ?? NoTrxDetail(process.ExitCode, stdoutText, stderrText);
                return new TestRunResult(
                    Outcome: ClassifyOutcome(exitCode: process.ExitCode, failedCount: 0, totalCount: 0, hung: hungTestName is not null, timedOut: false, trxExists: false),
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
                    Detail: hungDetail ?? noTrxDetail
                );
            }

            var allResults = ParseTrx(trxPath);

            var totalCount = allResults.Count;
            var passedCount = allResults.Count(r => r.Outcome == TestOutcome.Passed);
            var failedResults = allResults.Where(r => r.Outcome == TestOutcome.Failed).ToList();
            var failedCount = failedResults.Count;
            var skippedCount = allResults.Count(r => r.Outcome is TestOutcome.Skipped or TestOutcome.NotExecuted);

            // TODO no automated test drives this branch end to end: the TestSolutionFixture project uses xunit, which reports a malformed filter as 0 matched, so only InvalidFilterDetail is unit tested (NUnit cause verified by hand 2026-11-24 on Tests.Asyncify)
            var invalidFilterDetail = totalCount == 0 ? InvalidFilterDetail(filter, process.ExitCode, stdoutText, stderrText) : null;
            string? detail = lockDetail ?? invalidFilterDetail;
            if (detail is null && totalCount == 0)
            {
                detail = !string.IsNullOrEmpty(filter)
                    ? $"0 tests matched filter '{filter}'."
                    : "Project produced no test results.";
            }

            var outcome = ClassifyOutcome(process.ExitCode, failedCount, totalCount, hung: hungTestName is not null, timedOut: false, trxExists: true);
            if (outcome == TestRunOutcome.RunError && hungDetail is null && invalidFilterDetail is null)
            {
                // Non-zero exit with no failed test: facts only, appended to any 0-matched or lock text already in detail.
                var exitDetail = $"dotnet test exited with code {process.ExitCode} but no test failed (fixture setup/teardown error or test host crash); see StderrTail/StdoutTail.";
                detail = detail is null ? exitDetail : $"{detail} {exitDetail}";
            }

            var failureSummary = failedResults
                .GroupBy(r => Signature(r.ErrorMessage))
                .Select(g => new GroupedCountSummary(Signature: g.Key, Count: g.Count(), ExampleRef: g.First().TestName))
                .OrderByDescending(g => g.Count)
                .ToList();
            return new TestRunResult(
                Outcome: outcome,
                ExitCode: process.ExitCode,
                TotalCount: totalCount,
                PassedCount: passedCount,
                FailedCount: failedCount,
                SkippedCount: skippedCount,
                FailureSummary: PrependTearDownFailures(failureSummary, ParseFixtureTearDownFailures(stderrText)),
                Results: allResults,
                StdoutTail: Tail(stdoutText),
                StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
                Duration: DateTime.UtcNow - start,
                Detail: hungDetail ?? detail
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
                // Best-effort cleanup -> a leftover temp file is not worth failing the call over.
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

    public static List<TestCaseResult> ParseTrx(string trxPath)
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

            var output = unitTestResult.Element(ns + "Output");
            var errorInfo = output?.Element(ns + "ErrorInfo");
            var message = errorInfo?.Element(ns + "Message")?.Value;
            var stackTrace = errorInfo?.Element(ns + "StackTrace")?.Value;

            var stdOut = output?.Element(ns + "StdOut")?.Value;
            var testOutput = outcome == TestOutcome.Failed && !string.IsNullOrWhiteSpace(stdOut) ? CapOutput(stdOut) : null;

            results.Add(new TestCaseResult(
                TestName: testName,
                Outcome: outcome,
                Duration: duration,
                ErrorMessage: message,
                ErrorStackTrace: stackTrace,
                Output: testOutput
            ));
        }

        return results;
    }

    private static string CapOutput(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length <= MaxTestOutputChars)
        {
            return trimmed;
        }

        return "[truncated] " + trimmed[^MaxTestOutputChars..];
    }
}
