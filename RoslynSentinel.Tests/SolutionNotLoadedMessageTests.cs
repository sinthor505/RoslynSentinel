// Coverage for the "No solution is loaded" wording: a server process that has not loaded any
// solution yet (typically one just restarted after an update) says so, so an agent mid-chat is
// not left wondering why the solution it was just editing is gone.

using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class SolutionNotLoadedMessageTests
{
    private static readonly DateTime Started = new(2026, 10, 4, 14, 2, 0, DateTimeKind.Utc);

    [Test]
    public void Build_FreshStartup_SaysServerWasFreshlyStarted()
    {
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, null), Started.AddMinutes(3));

        Assert.That(message, Does.StartWith("No solution is loaded."));
        Assert.That(message, Does.Contain("freshly (re)started at 14:02:00 UTC (3 min ago)"));
        Assert.That(message, Does.Contain("Files on disk are unaffected."));
        Assert.That(message, Does.Contain("LoadSolution"));
    }

    [Test]
    public void Build_AfterASuccessfulLoad_IsThePlainMessage()
    {
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, Started.AddMinutes(1)), Started.AddMinutes(3));

        Assert.That(message, Is.EqualTo(SolutionNotLoadedMessage.Plain));
    }

    [Test]
    public void Build_NoState_IsThePlainMessage()
    {
        Assert.That(SolutionNotLoadedMessage.Build(null), Is.EqualTo(SolutionNotLoadedMessage.Plain));
    }

    [Test]
    public void Build_LoadInProgressOnFreshStart_SaysRetryNotLoadSolution()
    {
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, null, LoadInProgress: true), Started.AddSeconds(5));

        Assert.That(message, Does.Contain("still in progress"));
        Assert.That(message, Does.Not.Contain("Call LoadSolution"));
        Assert.That(message, Does.Not.Contain("freshly (re)started"));
    }

    [Test]
    public void Build_LoadInProgressAfterEarlierLoad_IsStillPlain()
    {
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, Started.AddMinutes(1), LoadInProgress: true));

        Assert.That(message, Is.EqualTo(SolutionNotLoadedMessage.Plain));
    }

    [Test]
    public void ForFilePath_LoadInProgress_ComposesRetryMessage()
    {
        var message = SolutionNotLoadedMessage.ForFilePath("ReplaceSnippet", new SolutionLoadState(Started, null, LoadInProgress: true));

        Assert.That(message, Does.StartWith("ReplaceSnippet: 'filePath' could not be resolved."));
        Assert.That(message, Does.Contain("still in progress"));
        Assert.That(message, Does.EndWith("Then retry with the same filePath."));
    }

    [TestCase(5, "5s")]
    [TestCase(59, "59s")]
    [TestCase(60, "1 min")]
    [TestCase(3599, "59 min")]
    [TestCase(3600, "1h 0 min")]
    [TestCase(5400, "1h 30 min")]
    public void Build_FreshStartup_FormatsAge(int seconds, string expected)
    {
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, null), Started.AddSeconds(seconds));

        Assert.That(message, Does.Contain($"({expected} ago)"));
    }

    [Test]
    public void ForFilePath_NamesToolAndKeepsFreshStartExplanation()
    {
        var message = SolutionNotLoadedMessage.ForFilePath("ReplaceSnippet", new SolutionLoadState(Started, null));

        Assert.That(message, Does.StartWith("ReplaceSnippet: 'filePath' could not be resolved."));
        Assert.That(message, Does.Contain("freshly (re)started"));
        Assert.That(message, Does.EndWith("Then retry with the same filePath."));
    }

    [Test]
    public void LoadState_IsFreshStartup_IsInverseOfHasLoadedSolutionSinceStart()
    {
        var fresh = new SolutionLoadState(Started, null);
        var loaded = new SolutionLoadState(Started, Started.AddSeconds(1));

        Assert.That(fresh.IsFreshStartup, Is.True);
        Assert.That(fresh.HasLoadedSolutionSinceStart, Is.False);
        Assert.That(loaded.IsFreshStartup, Is.False);
        Assert.That(loaded.HasLoadedSolutionSinceStart, Is.True);
    }

    [Test]
    public void ProcessStartedUtc_IsInThePast()
    {
        Assert.That(SolutionLoadState.ProcessStartedUtc, Is.LessThanOrEqualTo(DateTime.UtcNow));
    }

    [Test]
    public async Task GetCurrentSolutionAsync_WithNoSolution_ThrowsFreshStartMessage()
    {
        using var manager = new FakeWorkspaceManager();

        var ex = await Assert.ThrowsAsync<SolutionNotLoadedException>(async () => await manager.GetCurrentSolutionAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("freshly (re)started"));
    }

    [Test]
    public async Task GetCurrentSolutionAsync_AfterLoadStateMarkedLoaded_ThrowsPlainMessage()
    {
        using var manager = new FakeWorkspaceManager
        {
            LoadState = new SolutionLoadState(Started, Started.AddMinutes(1)),
        };

        var ex = await Assert.ThrowsAsync<SolutionNotLoadedException>(async () => await manager.GetCurrentSolutionAsync(CancellationToken.None));

        Assert.That(ex!.Message, Is.EqualTo(SolutionNotLoadedMessage.Plain));
    }

    [Test]
    public void Build_WithLastLoadFailureAndNothingLoaded_AppendsFailure()
    {
        var failure = "timeout after 30 s";
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, null, LastLoadFailure: failure), Started.AddMinutes(1));

        Assert.That(message, Does.Contain("The last load attempt failed: timeout after 30 s"));
    }

    [Test]
    public void Build_LoadInProgress_IgnoresLastLoadFailure()
    {
        var failure = "timeout after 30 s";
        var message = SolutionNotLoadedMessage.Build(new SolutionLoadState(Started, null, LoadInProgress: true, LastLoadFailure: failure));

        Assert.That(message, Does.Not.Contain("load attempt failed"));
        Assert.That(message, Does.Contain("still in progress"));
    }

    [Test]
    public void LoadWaitTimedOut_NamesSolutionLoadStatusAndThatCallDidNotRun()
    {
        var message = SolutionNotLoadedMessage.LoadWaitTimedOut(TimeSpan.FromSeconds(30));

        Assert.That(message, Does.Contain("30 s"));
        Assert.That(message, Does.Contain("solutionLoadStatus"));
        Assert.That(message, Does.Contain("call did not run"));
    }

    [Test]
    public void LoadCancelledAfterTimeout_NamesTimeoutSecondsAndAValue()
    {
        var message = SolutionNotLoadedMessage.LoadCancelledAfterTimeout(TimeSpan.FromSeconds(60));

        Assert.That(message, Does.Contain("timeoutSeconds"));
        Assert.That(message, Does.Contain("60"));
    }
}
