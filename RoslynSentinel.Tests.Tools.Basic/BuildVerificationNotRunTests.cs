using System;
using System.Threading.Tasks;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// WorkspaceBuildTestImpl.RunVerificationBuildAsync: a verification build that did not run (BuildNotRunException)
/// becomes one Caution finding with no build result; every other exception propagates to the caller.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")]
public class BuildVerificationNotRunTests
{
    private static BuildResult MinimalBuild() => new(
        Outcome: BuildOutcome.Succeeded,
        Level: BuildVerifyLevel.fullBuild,
        ProjectsCompiled: ["Core"],
        DiagnosticsComplete: true,
        ErrorCount: 0,
        WarningCount: 0,
        Errors: [],
        Warnings: [],
        ErrorSummary: [],
        WarningSummary: [],
        StdoutTail: null,
        StderrTail: null,
        Duration: TimeSpan.Zero);

    [Test]
    public async Task RunVerificationBuildAsync_BuildNotRun_ReturnsNullResultAndFinding()
    {
        var (result, finding) = await WorkspaceBuildTestImpl.RunVerificationBuildAsync(
            () => Task.FromException<BuildResult>(new BuildNotRunException("another build is running")));

        Assert.That(result, Is.Null);
        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Message, Does.Contain("another build is running"));
        Assert.That(finding.Message, Does.Contain("did not run"));
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Caution));
    }

    [Test]
    public void RunVerificationBuildAsync_OtherToolException_Propagates()
    {
        Assert.ThrowsAsync<ToolInvalidArgumentException>(
            () => WorkspaceBuildTestImpl.RunVerificationBuildAsync(
                () => Task.FromException<BuildResult>(new ToolInvalidArgumentException("bad"))));
    }

    [Test]
    public async Task RunVerificationBuildAsync_Success_ReturnsResultAndNoFinding()
    {
        var build = MinimalBuild();

        var (result, finding) = await WorkspaceBuildTestImpl.RunVerificationBuildAsync(() => Task.FromResult(build));

        Assert.That(result, Is.SameAs(build));
        Assert.That(finding, Is.Null);
    }

    [Test]
    public async Task BuildNotRunFinding_Text_HasNoPolicyWords()
    {
        var (_, finding) = await WorkspaceBuildTestImpl.RunVerificationBuildAsync(
            () => Task.FromException<BuildResult>(new BuildNotRunException("another build is running")));

        Assert.That(finding, Is.Not.Null);
        var text = finding!.Message.ToLowerInvariant();
        Assert.That(text, Does.Not.Contain(" must "));
        Assert.That(text, Does.Not.Contain("do not"));
        Assert.That(text, Does.Not.Contain("stop"));
    }
}
