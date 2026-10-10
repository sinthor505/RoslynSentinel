// Regression tests for LoadSolution while a solution load is already running (plan Step A5, after A4).
//
// Timing assumption: a real solution load takes seconds (MSBuild opens the fixture solution). The
// mid-load tests start LoadSolutionAsync unawaited and then call the tool while that load is still
// running, so the tool sees SolutionLoadStatus.Loading and waits for it. This holds in practice
// because the load has not finished by the time the synchronous part of the tool call has run.
// The timeoutSeconds out-of-range cases are deterministic: validation runs before any load starts.
// No test here uses timeoutSeconds 1 against a real load, because whether that load finishes
// within one second is not deterministic.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class LoadSolutionWaitTests
{
    [Test]
    public async Task LoadSolution_WhileSameSolutionLoading_WaitsThenReportsAlreadyLoaded()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var toolCall = tool.LoadSolution(new ToolCallReason("waits for a load already running"), fixture.SolutionPath);
        await load;
        var loadedAt = workspaceManager.LoadState.LastLoadedUtc;
        var result = await toolCall;

        Assert.That(result.IsError, Is.False, "the call must wait for the running load and succeed");
        Assert.That(result.SuccessData?.ToString(), Does.Contain("already loaded"));
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.EqualTo(loadedAt), "the same-path call must not start a second load");
    }

    [Test]
    public async Task LoadSolution_ForceReloadMidLoad_WaitsThenReloads()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var toolCall = tool.LoadSolution(new ToolCallReason("forces a reload after a load already running"), fixture.SolutionPath, forceReload: true);
        await load;
        var firstLoadedAt = workspaceManager.LoadState.LastLoadedUtc;
        var result = await toolCall;

        Assert.That(result.IsError, Is.False, "the forced call must wait for the running load and then reload it");
        Assert.That(result.SuccessData?.ToString(), Does.Contain("reloaded"));
        Assert.That(firstLoadedAt, Is.Not.Null);
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.Not.Null);
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.GreaterThan(firstLoadedAt), "the forced reload must produce a later load timestamp");
    }

    [TestCase(0)]
    [TestCase(3601)]
    public async Task LoadSolution_TimeoutSecondsOutOfRange_ReturnsInvalidArgumentNamingTheRange(int timeoutSeconds)
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);

        var result = await tool.LoadSolution(new ToolCallReason("checks timeoutSeconds range"), "fake.sln", timeoutSeconds: timeoutSeconds);

        Assert.That(result.IsError, Is.True, "an out-of-range timeoutSeconds must be refused");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("timeoutSeconds").And.Contain("1 and 3600"));
    }

    private static WorkspaceProjectManagementImpl CreateImpl(PersistentWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        return new WorkspaceProjectManagementImpl(
            workspaceManager,
            new SolutionManagementEngine(workspaceManager),
            new DependencyEngine(workspaceManager),
            new ProjectConsistencyEngine(workspaceManager),
            new StructuralRefinementEngine(workspaceManager, config),
            NullLogger<WorkspaceProjectManagementImpl>.Instance);
    }
}
