using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class ScenarioSampleLoopTests
{
    private const string Tool = "ReplaceSnippet";

    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_ScoresEachSampleAndWritesOneCsvLineEach()
    {
        var csv = Path.Combine(_dir, "results.csv");
        var seenDirectories = new List<string>();

        var samples = await ScenarioSampleLoop.RunAsync(
            Scenario(),
            2,
            _dir,
            csv,
            "off",
            (i, dir, ct) =>
            {
                seenDirectories.Add(dir);
                return Task.FromResult((FakeRun(), (FinalStateVerdict?)FinalStateVerdict.Correct));
            },
            CancellationToken.None);

        Assert.That(samples.Select(s => s.SampleIndex).ToArray(), Is.EqualTo(new[] { 1, 2 }));

        var lines = File.ReadAllLines(csv);
        Assert.That(lines.Length, Is.EqualTo(3));
        Assert.That(lines[0], Is.EqualTo(ScenarioSampleCsv.Header));

        var parsed = ScenarioSampleCsv.Parse(lines[1]);
        Assert.That(parsed.ScenarioId, Is.EqualTo("loop-test"));
        Assert.That(parsed.Commit, Is.EqualTo("abc123"));
        Assert.That(parsed.Dirty, Is.True);
        Assert.That(parsed.LiftRenames, Is.EqualTo("off"));
        Assert.That(parsed.Score, Is.Not.Null);
        Assert.That(parsed.Score!.FirstCallSucceeded, Is.True);

        Assert.That(seenDirectories.Count, Is.EqualTo(2));
        Assert.That(seenDirectories[0], Does.EndWith("sample-01"));
        Assert.That(seenDirectories[1], Does.EndWith("sample-02"));
    }

    [Test]
    public async Task RunAsync_HarnessErrorBecomesInconclusiveSampleAndLoopContinues()
    {
        var csv = Path.Combine(_dir, "results.csv");

        var samples = await ScenarioSampleLoop.RunAsync(
            Scenario(),
            2,
            _dir,
            csv,
            "off",
            async (i, dir, ct) =>
            {
                if (i == 1)
                {
                    throw new InvalidOperationException("boom\nsecond line");
                }

                return (FakeRun(), (FinalStateVerdict?)FinalStateVerdict.Correct);
            },
            CancellationToken.None);

        Assert.That(samples.Count, Is.EqualTo(2));
        Assert.That(samples[0].IsInconclusive, Is.True);
        Assert.That(samples[0].InconclusiveReason, Is.EqualTo("HarnessError: boom"));
        Assert.That(samples[0].Score, Is.Null);
        Assert.That(samples[1].Score, Is.Not.Null);

        var lines = File.ReadAllLines(csv);
        Assert.That(lines.Length, Is.EqualTo(3));
    }

    [Test]
    public async Task RunAsync_HarnessError_KeepsStampAndPartialTranscriptPath()
    {
        var csv = Path.Combine(_dir, "results.csv");
        var previousStamp = ModelAgentRunner.ProcessStamp;
        ModelAgentRunner.ProcessStamp = ("abc123", true);
        try
        {
            var samples = await ScenarioSampleLoop.RunAsync(
                Scenario(),
                2,
                _dir,
                csv,
                "off",
                async (i, dir, ct) =>
                {
                    if (i == 1)
                    {
                        await File.WriteAllTextAsync(Path.Combine(dir, "transcript.json"), "{}", ct);
                        throw new InvalidOperationException("Context size has been exceeded");
                    }

                    throw new InvalidOperationException("no transcript written");
                },
                CancellationToken.None);

            Assert.That(samples[0].IsInconclusive, Is.True);
            Assert.That(samples[0].Commit, Is.EqualTo("abc123"));
            Assert.That(samples[0].Dirty, Is.EqualTo(true));
            Assert.That(samples[0].TranscriptPath, Is.EqualTo(Path.Combine(_dir, "sample-01", "transcript.json")));
            Assert.That(samples[1].IsInconclusive, Is.True);
            Assert.That(samples[1].TranscriptPath, Is.EqualTo(string.Empty));
        }
        finally
        {
            ModelAgentRunner.ProcessStamp = previousStamp;
        }
    }

    [Test]
    public async Task RunAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ScenarioSampleLoop.RunAsync(
            Scenario(),
            2,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult((FakeRun(), (FinalStateVerdict?)FinalStateVerdict.Correct)),
            cts.Token));
    }

    [Test]
    public async Task RunSamplesAsync_InconclusiveReason_BecomesInconclusiveSampleWithDriftKept()
    {
        var csv = Path.Combine(_dir, "results.csv");
        const string reason = "SchemaChanged: ReadFile input changed";

        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            Scenario(),
            1,
            _dir,
            csv,
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((FakeRun(), null, true, 0, reason)),
            CancellationToken.None);

        Assert.That(samples.Count, Is.EqualTo(1));
        Assert.That(samples[0].IsInconclusive, Is.True);
        Assert.That(samples[0].Score, Is.Null);
        Assert.That(samples[0].InconclusiveReason, Is.EqualTo(reason));
        Assert.That(samples[0].SchemaDrift, Is.True);

        var lines = File.ReadAllLines(csv);
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(ScenarioSampleCsv.Parse(lines[1]).InconclusiveReason, Is.EqualTo(reason));
    }

    [Test]
    public async Task RunSamplesAsync_DriftFlags_ArePassedToTheSample()
    {
        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            Scenario(),
            1,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((FakeRun(), (FinalStateVerdict?)FinalStateVerdict.Correct, true, 3, string.Empty)),
            CancellationToken.None);

        Assert.That(samples[0].IsInconclusive, Is.False);
        Assert.That(samples[0].Score, Is.Not.Null);
        Assert.That(samples[0].SchemaDrift, Is.True);
        Assert.That(samples[0].ResultDrift, Is.EqualTo(3));
    }

    [Test]
    public async Task RunSamplesAsync_NullResultWithoutReason_IsHarnessError()
    {
        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            Scenario(),
            1,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((null, null, false, 0, string.Empty)),
            CancellationToken.None);

        Assert.That(samples[0].IsInconclusive, Is.True);
        Assert.That(samples[0].Score, Is.Null);
        Assert.That(samples[0].InconclusiveReason, Is.EqualTo("HarnessError: no run result"));
    }

    [Test]
    public async Task RunSamplesAsync_ColdStart_LiveTurnsToTargetIsTheTranscriptTurnCount()
    {
        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            Scenario(),
            1,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((FakeRunWithTurns(4), (FinalStateVerdict?)FinalStateVerdict.Correct, false, 0, string.Empty)),
            CancellationToken.None);

        Assert.That(samples[0].Score, Is.Not.Null);
        Assert.That(samples[0].LiveTurnsToTarget, Is.EqualTo(4));
    }

    [Test]
    public async Task RunSamplesAsync_ResumeScenario_LiveTurnsToTargetExcludesKeptTurns()
    {
        var scenario = Scenario() with { ResumeKeepTurns = 2 };

        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            scenario,
            1,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((FakeRunWithTurns(scenario.ResumeKeepTurns + 3), (FinalStateVerdict?)FinalStateVerdict.Correct, false, 0, string.Empty)),
            CancellationToken.None);

        Assert.That(samples[0].Score, Is.Not.Null);
        Assert.That(samples[0].LiveTurnsToTarget, Is.EqualTo(3));
    }

    [Test]
    public async Task RunSamplesAsync_InconclusiveSample_LiveTurnsToTargetIsNull()
    {
        var samples = await ScenarioSampleLoop.RunSamplesAsync(
            Scenario(),
            1,
            _dir,
            Path.Combine(_dir, "results.csv"),
            "off",
            (i, dir, ct) => Task.FromResult<(AgentRunResult?, FinalStateVerdict?, bool, int, string)>((FakeRunWithTurns(4), null, false, 0, "SchemaChanged: ReadFile input changed")),
            CancellationToken.None);

        Assert.That(samples[0].IsInconclusive, Is.True);
        Assert.That(samples[0].LiveTurnsToTarget, Is.Null);
    }

    private static ScenarioDefinition Scenario() => new(
        Id: "loop-test",
        ScenarioType: 1,
        FaultKind: string.Empty,
        ToolUnderTest: Tool,
        StagedFiles: new Dictionary<string, string>(),
        PromptTemplate: "Fix the fixture at {0}.",
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: (_, _) => Task.FromResult(FinalStateVerdict.Correct),
        SourceRun: string.Empty,
        ReproducesAt: "not measured",
        FixedBy: string.Empty);

    private static AgentRunResult FakeRun()
    {
        var transcript = Transcript(
            Turn(1, null, null, Call(Tool)),
            Turn(2, "Done.", null));
        transcript.CommitHash = "abc123";
        transcript.WorkingTreeDirty = true;

        return new AgentRunResult
        {
            StopReason = AgentStopReason.ModelFinished,
            Transcript = transcript,
            TranscriptPath = "x/transcript.json",
            TurnCount = 2,
        };
    }

    private static AgentRunResult FakeRunWithTurns(int turnCount)
    {
        var turns = new List<AgentTranscriptTurn> { Turn(1, null, null, Call(Tool)) };
        for (var n = 2; n <= turnCount; n++)
        {
            turns.Add(Turn(n, "Done.", null));
        }

        var transcript = Transcript(turns.ToArray());
        transcript.CommitHash = "abc123";
        transcript.WorkingTreeDirty = true;

        return new AgentRunResult
        {
            StopReason = AgentStopReason.ModelFinished,
            Transcript = transcript,
            TranscriptPath = "x/transcript.json",
            TurnCount = turnCount,
        };
    }

    private static AgentToolCallRecord Call(string toolName, string argumentsJson = "{}", bool isError = false, string? resultJson = null) => new()
    {
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
        ResultJson = resultJson ?? (isError ? "{\"isError\":true}" : "{\"isError\":false}"),
        IsError = isError,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };

    private static AgentTranscriptTurn Turn(int turnNumber, string? content, string? reasoning, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = content, ReasoningContent = reasoning },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = calls.ToList(),
    };

    private static AgentTranscript Transcript(params AgentTranscriptTurn[] turns)
    {
        var transcript = new AgentTranscript();
        transcript.Turns.AddRange(turns);
        return transcript;
    }
}
