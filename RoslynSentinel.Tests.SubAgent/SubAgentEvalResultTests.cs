using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.SubAgent;

/// <summary>
/// <see cref="SubAgentEvalResult.Build"/> against synthetic agent runs and child-server tool results.
/// Pure unit tests: no process, no model.
/// </summary>
[TestFixture]
public class SubAgentEvalResultTests
{
    private const string BuildSucceededJson =
        """{"isSuccess":true,"successData":{"outcome":"Succeeded","errorCount":0,"warningCount":3}}""";

    private const string BuildFailedJson =
        """{"isSuccess":true,"successData":{"outcome":"Failed","errorCount":4,"warningCount":0}}""";

    private const string TestsJson =
        """{"isSuccess":true,"successData":{"runCompleted":true,"totalCount":12,"passedCount":10,"failedCount":2,"skippedCount":0}}""";

    private static AgentRunResult Run(
        AgentStopReason stopReason = AgentStopReason.ModelFinished,
        string? lastContent = "All done.",
        RepeatedFailureDetail? repeatedFailure = null)
    {
        var transcript = new AgentTranscript();
        if (lastContent is not null)
        {
            transcript.Turns.Add(new AgentTranscriptTurn
            {
                TurnNumber = 1,
                ModelMessage = new AgentChatMessage { Role = "assistant", Content = lastContent },
                ModelLatency = TimeSpan.FromSeconds(1),
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        }

        return new AgentRunResult
        {
            StopReason = stopReason,
            Transcript = transcript,
            TranscriptPath = @"C:\runs\abc\transcript.json",
            TurnCount = 7,
            RepeatedFailure = repeatedFailure,
        };
    }

    [Test]
    public void CleanRun_ReportsCountsAndNoExcerpt()
    {
        var result = SubAgentEvalResult.Build(Run(), BuildSucceededJson, TestsJson, ["a/b.cs"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Converged, Is.True);
            Assert.That(result.StopReason, Is.EqualTo("ModelFinished"));
            Assert.That(result.TurnCount, Is.EqualTo(7));
            Assert.That(result.BuildSucceeded, Is.True);
            Assert.That(result.BuildErrorCount, Is.EqualTo(0));
            Assert.That(result.TestPassedCount, Is.EqualTo(10));
            Assert.That(result.TestFailedCount, Is.EqualTo(2));
            Assert.That(result.TouchedPaths, Is.EqualTo(new[] { "a/b.cs" }));
            Assert.That(result.FailureExcerpt, Is.Null);
            Assert.That(result.TranscriptPath, Is.EqualTo(@"C:\runs\abc\transcript.json"));
            Assert.That(result.RepeatedFailureSignature, Is.Null);
            Assert.That(result.Notes, Is.Empty);
        });
    }

    [Test]
    public void FailedBuild_ReportsErrorCount()
    {
        var result = SubAgentEvalResult.Build(Run(), BuildFailedJson, testResultJson: null, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.BuildSucceeded, Is.False);
            Assert.That(result.BuildErrorCount, Is.EqualTo(4));
            Assert.That(result.Notes, Has.Some.Contains("build did not succeed"));
        });
    }

    [Test]
    public void NonConvergedRun_KeepsLastMessageAsExcerpt_TruncatedToTheCap()
    {
        var longMessage = new string('x', SubAgentEvalResult.MaxFailureExcerptChars + 500);
        var result = SubAgentEvalResult.Build(
            Run(AgentStopReason.TurnCapExceeded, longMessage), BuildSucceededJson, TestsJson, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Converged, Is.False);
            Assert.That(result.StopReason, Is.EqualTo("TurnCapExceeded"));
            Assert.That(result.FailureExcerpt, Is.Not.Null);
            Assert.That(result.FailureExcerpt!.Length, Is.EqualTo(SubAgentEvalResult.MaxFailureExcerptChars + 3));
            Assert.That(result.FailureExcerpt, Does.EndWith("..."));
        });
    }

    [Test]
    public void NonConvergedRunWithNoTurns_HasEmptyExcerpt_NotNull()
    {
        var result = SubAgentEvalResult.Build(
            Run(AgentStopReason.WallClockCapExceeded, lastContent: null), BuildSucceededJson, TestsJson, []);

        Assert.That(result.FailureExcerpt, Is.EqualTo(""));
    }

    [Test]
    public void RepeatedFailure_SurfacesItsSignature()
    {
        var detail = new RepeatedFailureDetail("ReplaceSnippet", "ReplaceSnippet|NoMatches|a.cs", 3, 4, 6, "{}", "{}");
        var result = SubAgentEvalResult.Build(
            Run(AgentStopReason.RepeatedToolFailure, "stuck", detail), BuildSucceededJson, TestsJson, []);

        Assert.That(result.RepeatedFailureSignature, Is.EqualTo("ReplaceSnippet|NoMatches|a.cs"));
    }

    [Test]
    public void OffloadedBuildResult_IsUnreadable_AndSaysSo()
    {
        const string offloaded = """{"isSuccess":true,"largeResult":{"resultId":"abc","writtenToFile":true}}""";
        var result = SubAgentEvalResult.Build(Run(), offloaded, testResultJson: null, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.BuildSucceeded, Is.False);
            Assert.That(result.Notes, Has.Some.Contains("could not be read"));
        });
    }

    [Test]
    public void MissingBuildResult_IsNoted()
    {
        var result = SubAgentEvalResult.Build(Run(), buildResultJson: null, testResultJson: null, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.BuildSucceeded, Is.False);
            Assert.That(result.Notes, Has.Some.Contains("not obtained"));
        });
    }

    [Test]
    public void ErrorEnvelopeForTests_IsNotedAndCountsStayZero()
    {
        const string errorEnvelope = """{"isSuccess":false,"errorData":{"errorCode":"TestRunFailed","message":"x"}}""";
        var result = SubAgentEvalResult.Build(Run(), BuildSucceededJson, errorEnvelope, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.TestPassedCount, Is.EqualTo(0));
            Assert.That(result.TestFailedCount, Is.EqualTo(0));
            Assert.That(result.Notes, Has.Some.Contains("test result could not be read"));
        });
    }

    [Test]
    public void NonJsonText_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            SubAgentEvalResult.Build(Run(), "not json at all", "also not json", []));
    }
}
