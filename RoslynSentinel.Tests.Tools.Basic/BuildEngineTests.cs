using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns child processes through ChildProcessCapture): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: ChildProcessCapture's pool-backed async pipe reads starved under load. Reference fix to copy if confirmed: GitProcess.TryRun dedicated reader threads (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]

[Category("BuildEngine")] // sentinel:auto-category
[Category("DiagnosticEngine")] // sentinel:auto-category
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class BuildEngineTests
{
    [Test]
    public async Task RunQuickBuildAsync_ZeroProjectScope_ThrowsBuildNotRunAsync()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var diagnosticEngine = TestEngines.DiagnosticEngine(workspaceManager);
        var buildEngine = TestEngines.BuildEngine(workspaceManager, diagnosticEngine);

        await Assert.ThrowsAsync<BuildNotRunException>(async () => await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None));

        workspaceManager.Dispose();
    }
    [Test]
    public async Task RunFullBuildAsync_ZeroProjectSolution_ThrowsBuildNotRunAsync()
    {
        var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        workspaceManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var diagnosticEngine = TestEngines.DiagnosticEngine(workspaceManager);
        var buildEngine = TestEngines.BuildEngine(workspaceManager, diagnosticEngine);

        await Assert.ThrowsAsync<BuildNotRunException>(async () => await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50));

        workspaceManager.Dispose();
    }


    // Regression test for docs/current/blockers/blocking_error_build_tool_suppresses_cs0618_warnings.md:
    // RunFullBuildAsync shells out to \"dotnet build\", which uses MSBuild's own incremental
    // \"up-to-date\" check -- a second build against unchanged inputs skips recompilation entirely
    // and reports zero diagnostics, not zero *new* diagnostics. That silently dropped a real,
    // pre-existing CS0618/Obsolete warning while DiagnosticsComplete stayed true. Calling
    // RunFullBuildAsync twice back-to-back against the same on-disk Obsolete call site reproduces
    // the regression shape directly: the first call always saw the warning; the second call is the
    // one that silently lost it before BuildEngine.cs's "--no-incremental" fix.
    [NonParallelizable]
    [Test]
    // TODO hit the 5-minute full-build timeout on a full solution test run (2026-11-09, other sessions building); passes when run alone.
    public async Task RunFullBuildAsync_CalledTwiceWithObsoleteCallSite_BothCallsReportTheWarningAsync()
    {
        const string obsoleteSource = """
            namespace ContosoOrders.Core;

            public static class LegacyPricingHelper
            {
                [Obsolete("Use PricingEngine instead.", error: false)]
                public static decimal ApplyLegacyDiscount(decimal amount) => amount * 0.9m;
            }

            public static class LegacyPricingCaller
            {
                public static decimal CallLegacy(decimal amount) => LegacyPricingHelper.ApplyLegacyDiscount(amount);
            }
            """;

        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Core", "LegacyPricingHelper.cs"), obsoleteSource);
        var diagnosticEngine = TestEngines.DiagnosticEngine(workspaceManager);
        var buildEngine = TestEngines.BuildEngine(workspaceManager, diagnosticEngine);

        var firstResult = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);
        Assert.That(firstResult, Is.Not.Null);
        Assert.That(firstResult.ErrorCount, Is.Zero, firstResult.StdoutTail);
        Assert.That(firstResult.Warnings, Has.Some.Matches<DiagnosticInfo>(w => w.Id == "CS0618"),
            "first RunFullBuildAsync call must report the Obsolete-sourced CS0618 warning.");

        var secondResult = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);
        Assert.That(secondResult, Is.Not.Null);
        Assert.That(secondResult.ErrorCount, Is.Zero, secondResult.StdoutTail);
        Assert.That(secondResult.Warnings, Has.Some.Matches<DiagnosticInfo>(w => w.Id == "CS0618"),
            "second RunFullBuildAsync call (previously an MSBuild incremental-cache no-op) must still report the CS0618 warning, not silently drop it.");
    }
}
