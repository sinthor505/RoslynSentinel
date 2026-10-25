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

    [Test]
    public void DriftHaltMessage_NamesCompleteExternalFileDriftCalls()
    {
        var message = DriftMessages.DriftHaltMessage;

        Assert.That(message, Does.Contain("ExternalFileDrift(reason:"));
        Assert.That(message, Does.Contain("operation: List"));
        Assert.That(message, Does.Contain("operation: Acknowledge"));
    }

    [Test]
    public void OrientationCircuitBreaker_StateMessage_NamesCompleteCalls()
    {
        var breaker = new OrientationCircuitBreaker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, tripThreshold: 2);
        breaker.RecordSearchOutcome(0);
        breaker.RecordSearchOutcome(0);

        var message = breaker.StateMessage();

        Assert.That(message, Is.Not.Null);
        Assert.That(message, Does.Contain("ListAll(reason:"));
        Assert.That(message, Does.Contain("ReadFile(reason:"));
    }
}
