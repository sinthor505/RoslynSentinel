// Coverage for the RunTestTool MCP tool (docs/current/plan-runtest-tool-v1.md). Uses TestSolutionFixture
// (the ContosoOrders sample, which ships a real xUnit test project) rather than the in-memory
// TestSolutionBuilder path, since RunTestTool shells out to a real `dotnet test` subprocess against
// files on disk -> same real-process rationale as BuildEngine's fullBuild path.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns child processes through ChildProcessCapture): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: ChildProcessCapture's pool-backed async pipe reads starved under load. Reference fix to copy if confirmed: GitProcess.TryRun dedicated reader threads (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("TestRunResult")] // sentinel:auto-category
[Category("WorkspaceTools")] // sentinel:auto-category
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class RunTestTests
{
    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    private const string FailingTestSource = """
        using ContosoOrders.Core;

        using Xunit;

        namespace ContosoOrders.Tests;

        public class FailingTests
        {
            [Fact]
            public void AlwaysFails()
            {
                Assert.Fail("distinct solitary failure");
            }
        }
        """;

    private const string FailingTestWithOutputSource = """
        using Xunit;
        using Xunit.Abstractions;

        namespace ContosoOrders.Tests;

        public class FailingOutputTests
        {
            private readonly ITestOutputHelper _output;

            public FailingOutputTests(ITestOutputHelper output)
            {
                _output = output;
            }

            [Fact]
            public void WritesOutputThenFails()
            {
                _output.WriteLine("RSMARKER-FAILED-OUTPUT-42");
                Assert.Fail("boom");
            }

            [Fact]
            public void Passes()
            {
                _output.WriteLine("RSMARKER-PASSED");
                Assert.True(true);
            }
        }
        """;

    [Test]
    [Category("TestCaseResult")] // sentinel:auto-category
    public async Task RunTest_FailedTestWithCapturedOutput_SurfacesOutputAndNothingForPassedTestsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingOutputTests.cs"), FailingTestWithOutputSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, resultsType: TestResultsFilter.all, timeoutSeconds: 120, includeOutput: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.RunSucceeded, Is.False);
        Assert.That(data.FailedCount, Is.EqualTo(1));
        
        var failedTest = data.Results.Single(r => r.Outcome == TestOutcome.Failed);
        Assert.That(failedTest.Output, Is.Not.Null.And.Contains("RSMARKER-FAILED-OUTPUT-42"), "Failed test should have captured output with the marker");
        
        var passedTests = data.Results.Where(r => r.Outcome == TestOutcome.Passed).ToList();
        Assert.That(passedTests, Is.Not.Empty, "Should have at least one passed test");
        foreach (var passed in passedTests)
        {
            Assert.That(passed.Output, Is.Null, "Passed tests should never have Output");
        }
    }

    [Test]
    [Category("TestCaseResult")] // sentinel:auto-category
    public async Task RunTest_MixedPassAndFail_ReportsCountsAndFailureMessageAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120, includeOutput: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.RunSucceeded, Is.False);
        Assert.That(data.PassedCount, Is.EqualTo(2));
        Assert.That(data.FailedCount, Is.EqualTo(1));
        var failure = data.Results.Single(r => r.Outcome == TestOutcome.Failed);
        Assert.That(failure.ErrorMessage, Is.Not.Null.And.Not.Empty);
        // Tails are diagnostic material for a run that went wrong -> kept when the run is not clean.
        Assert.That(data.StdoutTail, Is.Not.Null.And.Not.Empty);
        Assert.That(result.StatusMessage, Does.Contain("Tests FAILED").And.Contain("1 failed"));
    }

    // A clean run drops the raw stdout/stderr tails at the tool boundary (the engine still returns
    // them) and carries the totals in StatusMessage, so the response stays small enough to inline.
    [Test]
    public async Task RunTest_CleanRun_DropsTailsAndSetsStatusMessageAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.RunSucceeded, Is.True);
        Assert.That(data.StdoutTail, Is.Null);
        Assert.That(data.StderrTail, Is.Null);
        Assert.That(result.StatusMessage, Does.Contain("Tests passed").And.Contain($"{data.TotalCount} tests"));
    }

    [Test]
    public async Task RunTest_ScopeProjectUnresolvedScopeName_ReturnsTestRunFailedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.project, scopeName: "DoesNotExist");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("TestRunFailed"));
        Assert.That(result.ErrorData!.Message, Does.Contain("DoesNotExist"));
    }

    [Test]
    public async Task RunTest_ScopeFile_ReturnsTestRunFailedExplainingUnsupportedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.file);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("TestRunFailed"));
        Assert.That(result.ErrorData!.Message, Does.Contain("scope=file"));
    }

    // Known intermittent failure under a full/parallel Battery run (passes isolated and on rerun) ->
    // same TestSolutionFixture.Dispose()/RunTestTool-subprocess file-handle race as the comment on
    // RunTest_FilterMatchesZeroTests_DetailReportsZeroMatchAsync below; not specific to this test.
    [Test]
    public async Task RunTest_FilterNarrowsToOneTest_TotalCountOneAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, filter: "FullyQualifiedName~AlwaysFails", timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.TotalCount, Is.EqualTo(1));
        Assert.That(data.FailedCount, Is.EqualTo(1));
    }

    // Known intermittent failure under a full/parallel Battery run (passes isolated and on rerun):
    // TestSolutionFixture.Dispose()'s Directory.Delete can race this test's own RunTestTool subprocess
    // not having fully released its file handles yet, throwing IOException on a .csproj file still
    // "in use by another process." Documented as a known, pre-existing, distinct issue in
    // docs/current/blockers/closed/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md's
    // "Secondary symptom" section -> not the PersistentWorkspaceManager.Dispose() deadlock fixed in
    // finding_dispose_waithandle_deadlock.md, which this test also exercises but did not cause.
    [Test]
    public async Task RunTest_FilterMatchesZeroTests_DetailReportsZeroMatchAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, filter: "FullyQualifiedName~NoSuchTestNameAnywhere", timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.TotalCount, Is.Zero);
        Assert.That(data.Detail, Does.Contain("matched filter"));
    }

    // Known intermittent failure under a full/parallel Battery run (passes isolated and on rerun) ->
    // same TestSolutionFixture.Dispose()/RunTestTool-subprocess file-handle race as the comment on
    // RunTest_FilterMatchesZeroTests_DetailReportsZeroMatchAsync below; not specific to this test.
    [Test]
    public async Task RunTest_FailureSummary_GroupsBySignatureDescendingByCountAsync()
    {
        const string sharedFailureSource = """
            using Xunit;

            namespace ContosoOrders.Tests;

            public class SharedFailureTests
            {
                [Fact]
                public void Fails1() => Assert.Fail("shared repeated failure");

                [Fact]
                public void Fails2() => Assert.Fail("shared repeated failure");

                [Fact]
                public void Fails3() => Assert.Fail("shared repeated failure");

                [Fact]
                public void Fails4() => Assert.Fail("shared repeated failure");

                [Fact]
                public void Fails5() => Assert.Fail("shared repeated failure");
            }
            """;

        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "SharedFailureTests.cs"), sharedFailureSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, resultsType: TestResultsFilter.skipped, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.FailedCount, Is.EqualTo(6), "FailureSummary/FailedCount reflect the full run regardless of the resultsType filter applied to Results.");
        Assert.That(data.FailureSummary, Is.Not.Empty);
        Assert.That(data.FailureSummary[0].Count, Is.EqualTo(5), "the shared-message group of 5 must sort first (descending by Count).");
        Assert.That(data.FailureSummary.Sum(g => g.Count), Is.EqualTo(6));
    }

    [Test]
    [Category("TestCaseResult")] // sentinel:auto-category
    public async Task RunTest_ResultsTypeFailed_ReturnsOnlyFailedEntriesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, resultsType: TestResultsFilter.failed, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.Results, Is.Not.Empty);
        Assert.That(data.Results, Has.All.Matches<TestCaseResult>(r => r?.Outcome == TestOutcome.Failed));
    }

    [Test]
    public async Task RunTest_SummaryTrue_OmitsResultsRegardlessOfResultsTypeAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, resultsType: TestResultsFilter.all, summary: true, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.Results, Is.Empty);
        Assert.That(data.FailedCount, Is.EqualTo(1));
        Assert.That(data.FailureSummary, Is.Not.Empty);
    }

    [Test]
    public async Task RunTest_SummaryTrue_ReportsNoOmittedResultsAndDetachesNoFullResultsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, summary: true, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.FailedCount, Is.EqualTo(1));
        Assert.That(data.OmittedResultCount, Is.Zero, "summary=true omits Results by request, so nothing is counted as omitted.");
        Assert.That(result.Detached, Is.Empty, "summary=true stores no full-results payload.");
    }

    [Test]
    public async Task RunTest_MaxDetailsBelowFailureCount_ReportsOmittedResultCountAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingOutputTests.cs"), FailingTestWithOutputSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, maxDetails: 1, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.FailedCount, Is.EqualTo(2));
        Assert.That(data.Results, Has.Count.EqualTo(1), "maxDetails caps the inline Results list.");
        Assert.That(data.OmittedResultCount, Is.EqualTo(1), "the one failed row beyond maxDetails is counted as omitted.");
    }

    [Test]
    public async Task RunTest_IncludeOutputTrue_KeepsStdoutTailOnFailingRunAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120, includeOutput: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.RunSucceeded, Is.False);
        Assert.That(data.StdoutTail, Is.Not.Null.And.Not.Empty, "includeOutput: true keeps the stdout tail on a completed run with tests.");
    }

    // The unprojected rows are declared stored (Detach) under successData.fullResults; the marshaller stores them and
    // leaves a largeResult pointer (ToolResultMarshallerDetachedTests covers that half, with a RunTest-shaped result).
    [Test]
    public async Task RunTest_FailingRun_DetachesTheUnprojectedRowsAsFullResultsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var detached = result.Detached.Single();
        Assert.That(detached.Name, Is.EqualTo("fullResults"));
        var rows = (List<TestCaseResult>)detached.Payload;
        Assert.That(rows, Has.Count.EqualTo(1), "the payload holds the one failed row.");
        Assert.That(rows[0].Outcome, Is.EqualTo(TestOutcome.Failed));
        Assert.That(rows[0].ErrorMessage, Does.Contain("distinct solitary failure"), "the stored row keeps the untruncated message.");
    }

    // The projected digest keeps the failed test's name inline and caps its message at 300 chars; the untruncated
    // unprojected rows are detached as fullResults (stored by the marshaller, read with GetLargeResult).
    [Test]
    public async Task RunTest_FailingRun_ListsFailedTestNameInlineWithCappedMessageAndDetachesFullResultsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        var failed = data.Results.Single(r => r.Outcome == TestOutcome.Failed);
        Assert.That(failed.TestName, Does.Contain("AlwaysFails"), "the failed test name must be listed inline.");
        Assert.That(failed.ErrorMessage?.Length ?? 0, Is.LessThanOrEqualTo(300), "the inline ErrorMessage is capped at 300 chars.");
        Assert.That(result.Detached.Select(d => d.Name), Is.EqualTo(new[] { "fullResults" }), "a failing run detaches the unprojected rows.");
    }

    [Test]
    public async Task RunTest_NoSolutionLoaded_ReturnsInvalidArgumentNotExceptionAsync()
    {
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.Not.EqualTo("Exception"));
    }

    [Test]
    public async Task RunTest_RateLimitExceeded_ReturnsCleanErrorAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        string? rateLimitError = null;
        for (var i = 0; i < 15 && rateLimitError is null; i++)
        {
            rateLimitError = workspaceManager.CheckRateLimit("RunTest", 10);
        }

        Assert.That(rateLimitError, Is.Not.Null, "expected CheckRateLimit to start rejecting within 15 calls at a limit of 10.");

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.RateLimited));
    }

    // Diffs the shared temp dir before/after, so a sibling test's in-flight .trx would read as a
    // leftover: must not overlap the other (parallel) RunTestTool tests in this fixture.
    [Test]
    [NonParallelizable]
    public async Task RunTest_TrxTempFile_DeletedAfterCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var workspaceTools = BuildTools(workspaceManager);

        // Diffs against a "before" snapshot (rather than asserting the directory is empty) so this
        // test tolerates other RunTestTool calls racing concurrently elsewhere in the suite -> each uses
        // its own GUID-suffixed filename, so only a leftover from *this* call would show up as new.
        var before = new HashSet<string>(Directory.EnumerateFiles(Path.GetTempPath(), "roslynsentinel_runtest_*.trx"));

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var after = Directory.EnumerateFiles(Path.GetTempPath(), "roslynsentinel_runtest_*.trx");
        var newLeftovers = after.Where(f => !before.Contains(f)).ToList();
        Assert.That(newLeftovers, Is.Empty, "no new roslynsentinel_runtest_*.trx file should remain after RunTest completes.");
    }

    // When a test project does not compile, `dotnet test` produces no TRX. The failing project and the
    // cause must be in StatusMessage (which the large-result offload envelope relays), not only in the
    // payload's Detail, so a caller can see what broke without a GetLargeResultTool round trip.
    [Test]
    public async Task RunTest_ProjectFailsToBuild_StatusMessageNamesProjectAndCauseAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "BrokenTests.cs"),
            "namespace ContosoOrders.Tests; public class BrokenTests { this does not compile }");
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.project, scopeName: "ContosoOrders.Tests", timeoutSeconds: 120);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.StatusMessage, Does.Contain("ContosoOrders.Tests").And.Contain("TRX"));
    }

    // A project can exit non-zero (build or adapter error) with zero failed tests; the headline must name it.
    [Test]
    public void SummarizeTestRun_NonZeroExitWithNoFailedTests_NamesTheProject()
    {
        var run = new TestRunResult(
            RunSucceeded: false, ExitCode: 1, TotalCount: 0, PassedCount: 0, FailedCount: 0, SkippedCount: 0,
            FailureSummary: [], Results: [], StdoutTail: null, StderrTail: null, Duration: TimeSpan.FromSeconds(1),
            ProjectSummaries: [new ProjectTestSummary("Some.Tests", RunSucceeded: false, TotalCount: 0, PassedCount: 0, FailedCount: 0, SkippedCount: 0, Detail: null)]);

        var text = WorkspaceBuildTestImpl.SummarizeTestRun(run);

        Assert.That(text, Does.Contain("Some.Tests").And.Contain("exited non-zero"));
    }

    // A failed run ends with one re-run of its first failing test; a passing run carries no next step.
    [Test]
    public void SummarizeTestRun_FailedTests_EndsWithRerunNextStep()
    {
        const string testName = "RoslynSentinel.Tests.Battery.Basic.SampleTests.Fails_Here";
        var failed = new TestRunResult(
            RunSucceeded: false, ExitCode: 1, TotalCount: 1, PassedCount: 0, FailedCount: 1, SkippedCount: 0,
            FailureSummary: [], Results: [new TestCaseResult(testName, TestOutcome.Failed, TimeSpan.FromSeconds(1), "boom", null)],
            StdoutTail: null, StderrTail: null, Duration: TimeSpan.FromSeconds(1),
            ProjectSummaries: [new ProjectTestSummary("Some.Tests", RunSucceeded: false, TotalCount: 1, PassedCount: 0, FailedCount: 1, SkippedCount: 0, Detail: null)]);
        var passed = new TestRunResult(
            RunSucceeded: true, ExitCode: 0, TotalCount: 1, PassedCount: 1, FailedCount: 0, SkippedCount: 0,
            FailureSummary: [], Results: [], StdoutTail: null, StderrTail: null, Duration: TimeSpan.FromSeconds(1),
            ProjectSummaries: []);

        var text = WorkspaceBuildTestImpl.SummarizeTestRun(failed);

        Assert.That(text, Does.Contain("Next: RunTest(").And.Contain(testName).And.EndWith("first failing test."));
        Assert.That(WorkspaceBuildTestImpl.SummarizeTestRun(passed), Does.Not.Contain("Next:"));
    }

    [Test]
    public async Task RunTest_EnvironmentParameter_ReachesTestProcessAsync()
    {
        const string envProbeSource = """
            using System;
            using Xunit;

            namespace ContosoOrders.Tests;

            public class EnvPassthroughTests
            {
                [Fact]
                public void EnvironmentVariableReachesTestProcess()
                {
                    Assert.Equal("passthrough-ok", Environment.GetEnvironmentVariable("ROSLYNSENTINEL_TEST_ENV_PASSTHROUGH"));
                }
            }
            """;
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "EnvPassthroughTests.cs"), envProbeSource);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.RunTestTool(reason: "test message", ToolScope.solution, filter: "FullyQualifiedName~EnvironmentVariableReachesTestProcess", timeoutSeconds: 120, environment: new Dictionary<string, string> { ["ROSLYNSENTINEL_TEST_ENV_PASSTHROUGH"] = "passthrough-ok" });

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = (TestRunResult)result.SuccessData!;
        Assert.That(data.PassedCount, Is.EqualTo(1));
        Assert.That(data.FailedCount, Is.EqualTo(0));
    }
}
