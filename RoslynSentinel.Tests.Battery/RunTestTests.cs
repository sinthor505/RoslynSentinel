// Coverage for the RunTest MCP tool (docs/current/plan-runtest-tool-v1.md). Uses TestSolutionFixture
// (the ContosoOrders sample, which ships a real xUnit test project) rather than the in-memory
// TestSolutionBuilder path, since RunTest shells out to a real `dotnet test` subprocess against
// files on disk — same real-process rationale as BuildEngine's fullBuild path.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class RunTestTests
{
    private static SentinelWorkspaceTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(workspaceManager);
        return new SentinelWorkspaceTools(
            workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(workspaceManager));
    }

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

    [Test]
    public async Task RunTest_MixedPassAndFail_ReportsCountsAndFailureMessageAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (TestRunResult)result.Data!;
        Assert.That(data.RunSucceeded, Is.False);
        Assert.That(data.PassedCount, Is.EqualTo(2));
        Assert.That(data.FailedCount, Is.EqualTo(1));
        var failure = data.Results.Single(r => r.Outcome == TestOutcome.Failed);
        Assert.That(failure.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task RunTest_ScopeProjectUnresolvedScopeName_ReturnsTestRunFailedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.project, scopeName: "DoesNotExist");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("TestRunFailed"));
        Assert.That(result.Error!.Message, Does.Contain("DoesNotExist"));
    }

    [Test]
    public async Task RunTest_ScopeFile_ReturnsTestRunFailedExplainingUnsupportedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.file);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("TestRunFailed"));
        Assert.That(result.Error!.Message, Does.Contain("scope=file"));
    }

    [Test]
    public async Task RunTest_FilterNarrowsToOneTest_TotalCountOneAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution, filter: "FullyQualifiedName~AlwaysFails", timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (TestRunResult)result.Data!;
        Assert.That(data.TotalCount, Is.EqualTo(1));
        Assert.That(data.FailedCount, Is.EqualTo(1));
    }

    // Known intermittent failure under a full/parallel Battery run (passes isolated and on rerun):
    // TestSolutionFixture.Dispose()'s Directory.Delete can race this test's own RunTest subprocess
    // not having fully released its file handles yet, throwing IOException on a .csproj file still
    // "in use by another process." Documented as a known, pre-existing, distinct issue in
    // docs/obsolete/blockers/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md's
    // "Secondary symptom" section — not the PersistentWorkspaceManager.Dispose() deadlock fixed in
    // project_dispose_waithandle_deadlock_found.md, which this test also exercises but did not cause.
    [Test]
    public async Task RunTest_FilterMatchesZeroTests_DetailReportsZeroMatchAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution, filter: "FullyQualifiedName~NoSuchTestNameAnywhere", timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (TestRunResult)result.Data!;
        Assert.That(data.TotalCount, Is.EqualTo(0));
        Assert.That(data.Detail, Does.Contain("matched filter"));
    }

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
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "SharedFailureTests.cs"), sharedFailureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution, resultsType: TestResultsFilter.skipped, timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (TestRunResult)result.Data!;
        Assert.That(data.FailedCount, Is.EqualTo(6), "FailureSummary/FailedCount reflect the full run regardless of the resultsType filter applied to Results.");
        Assert.That(data.FailureSummary, Is.Not.Empty);
        Assert.That(data.FailureSummary[0].Count, Is.EqualTo(5), "the shared-message group of 5 must sort first (descending by Count).");
        Assert.That(data.FailureSummary.Sum(g => g.Count), Is.EqualTo(6));
    }

    [Test]
    public async Task RunTest_ResultsTypeFailed_ReturnsOnlyFailedEntriesAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingTests.cs"), FailingTestSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution, resultsType: TestResultsFilter.failed, timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (TestRunResult)result.Data!;
        Assert.That(data.Results, Is.Not.Empty);
        Assert.That(data.Results, Has.All.Matches<TestCaseResult>(r => r.Outcome == TestOutcome.Failed));
    }

    [Test]
    public async Task RunTest_NoSolutionLoaded_ReturnsInvalidArgumentNotExceptionAsync()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tools = BuildTools(workspaceManager);

        var result = await tools.RunTest(reason: "test", ToolScope.solution);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.Not.EqualTo("Exception"));
    }

    [Test]
    public async Task RunTest_RateLimitExceeded_ReturnsCleanErrorAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        string? rateLimitError = null;
        for (var i = 0; i < 15 && rateLimitError is null; i++)
        {
            rateLimitError = workspaceManager.CheckRateLimit("RunTest", 10);
        }

        Assert.That(rateLimitError, Is.Not.Null, "expected CheckRateLimit to start rejecting within 15 calls at a limit of 10.");

        var result = await tools.RunTest(reason: "test", ToolScope.solution);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("TestRunFailed"));
    }

    [Test]
    public async Task RunTest_TrxTempFile_DeletedAfterCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = BuildTools(workspaceManager);

        var before = Directory.EnumerateFiles(Path.GetTempPath(), "roslynsentinel_runtest_*.trx").ToList();

        var result = await tools.RunTest(reason: "test", ToolScope.solution, timeoutSeconds: 120);

        Assert.That(result.Success, Is.True, result.Error?.Message);
        var after = Directory.EnumerateFiles(Path.GetTempPath(), "roslynsentinel_runtest_*.trx").ToList();
        Assert.That(after, Is.EquivalentTo(before), "no roslynsentinel_runtest_*.trx file should remain after RunTest completes.");
    }
}
