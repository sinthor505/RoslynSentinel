using System;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Asserts that converted server-emitted hints are complete tool calls; add one test per converted message group.
/// </summary>
[TestFixture]
public class ConvertedToolHintTests
{
    [Test]
    public void SolutionNotLoadedMessage_Plain_NamesACompleteLoadSolutionCall()
    {
        var message = SolutionNotLoadedMessage.Plain;

        Assert.That(message, Does.Contain("LoadSolution(reason:"));
        Assert.That(message, Does.Contain("solutionPath:"));
    }

    [Test]
    public void SolutionNotLoadedMessage_FreshStart_NamesACompleteLoadSolutionCall()
    {
        var state = new SolutionLoadState(DateTime.UtcNow.AddMinutes(-3), null);

        var message = SolutionNotLoadedMessage.Build(state);

        Assert.That(message, Does.Contain("LoadSolution(reason:"));
        Assert.That(message, Does.Contain("solutionPath:"));
    }

    [Test]
    public void SolutionNotLoadedMessage_LoadCancelledAfterTimeout_NamesTimeoutSecondsInTheCall()
    {
        var message = SolutionNotLoadedMessage.LoadCancelledAfterTimeout(TimeSpan.FromSeconds(60));

        Assert.That(message, Does.Contain("LoadSolution(reason:"));
        Assert.That(message, Does.Contain("timeoutSeconds: 120"));
    }
}
