using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]

[Category("BuildEngine")] // sentinel:auto-category
[Category("DiagnosticEngine")] // sentinel:auto-category
public class BuildEngineTests
{
    [Test]
    public async Task RunQuickBuildAsync_ZeroProjectScope_ReturnsInvalidInputWithBuildNotRunAsync()
    {
        var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var buildEngine = new BuildEngine(workspaceManager, diagnosticEngine);

        var result = await buildEngine.RunQuickBuildAsync(ToolScope.solution, null, maxDetails: 50, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.InvalidInput));
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Code, Is.EqualTo(EngineErrorCode.BuildNotRun));

        workspaceManager.Dispose();
    }
    [Test]
    public async Task RunFullBuildAsync_ZeroProjectSolution_ReturnsInvalidInputWithBuildNotRunAsync()
    {
        var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateEmptySolution());
        workspaceManager.SolutionPath = "C:/fake/EmptySolution.slnx";
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var buildEngine = new BuildEngine(workspaceManager, diagnosticEngine);

        var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.InvalidInput));
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Code, Is.EqualTo(EngineErrorCode.BuildNotRun));

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
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Core", "LegacyPricingHelper.cs"), obsoleteSource);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        var buildEngine = new BuildEngine(workspaceManager, diagnosticEngine);

        var firstResult = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);
        Assert.That(firstResult.Outcome, Is.EqualTo(EngineOutcome.Success), firstResult.Error?.Message);
        var firstData = firstResult.Data!;
        Assert.That(firstData.ErrorCount, Is.Zero, firstData.StdoutTail);
        Assert.That(firstData.Warnings, Has.Some.Matches<DiagnosticInfo>(w => w.Id == "CS0618"),
            "first RunFullBuildAsync call must report the Obsolete-sourced CS0618 warning.");

        var secondResult = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50);
        Assert.That(secondResult.Outcome, Is.EqualTo(EngineOutcome.Success), secondResult.Error?.Message);
        var secondData = secondResult.Data!;
        Assert.That(secondData.ErrorCount, Is.Zero, secondData.StdoutTail);
        Assert.That(secondData.Warnings, Has.Some.Matches<DiagnosticInfo>(w => w.Id == "CS0618"),
            "second RunFullBuildAsync call (previously an MSBuild incremental-cache no-op) must still report the CS0618 warning, not silently drop it.");
    }
}
