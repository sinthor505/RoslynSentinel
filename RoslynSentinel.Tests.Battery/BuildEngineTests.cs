using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Basic;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class BuildEngineTests
{
    // Added by AddMember (expected - used for diagnostics)
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
}
