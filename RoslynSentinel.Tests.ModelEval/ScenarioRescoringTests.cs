using System.Text.Json;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class ScenarioRescoringTests
{
    private const string Tool = "ReplaceSnippet";

    // 28-column results.csv layout (no finalStateVerdict or turnCapHit columns).
    private const string OldHeader =
        "scenarioId,scenarioType,faultKind,sampleIndex,commit,dirty,liftRenames,firstCallSucceeded,firstErrorCode,firstErrorClass,recovered,callsToRecover,recoveryUsedExpectedTool,markerPresent,turnCount,stopReason,failureClass,passed,callsAfterFirstError,turnsAfterFirstError,outputCharsAfterFirstError,maxIdenticalCallRepeat,identicalReasoningBlocks,retryPhraseCount,schemaDrift,resultDrift,inconclusiveReason,transcriptPath";

    // Current 30-column layout: Header minus liveTurnsToTarget, the column added last.
    private static readonly string LegacyHeader = string.Join(',', ScenarioSampleCsv.Header.Split(',').SkipLast(1));

    // Cold-start row by column name, for LegacyHeader and Header files; the transcript is written by WriteRun.
    private static readonly (string Name, string Value)[] ColdRowValues =
    [
        ("scenarioId", "S1"),
        ("scenarioType", "3"),
        ("sampleIndex", "1"),
        ("commit", "abc"),
        ("dirty", "false"),
        ("liftRenames", "off"),
        ("schemaDrift", "false"),
        ("resultDrift", "0"),
        ("transcriptPath", "x"),
    ];

    private string _folder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "rescore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Test]
    public void TargetPath_UsesStagedKeyWhenFileNameMatches()
    {
        var keys = new[] { "ContosoOrders.Core/Other.cs", "ContosoOrders.Core/Moved/Thing.cs" };

        Assert.That(ScenarioRescoring.TargetPath(keys, "thing.cs"), Is.EqualTo("ContosoOrders.Core/Moved/Thing.cs"));
    }

    [Test]
    public void TargetPath_FallsBackToFixtureHelpers()
    {
        var keys = new[] { "ContosoOrders.Core/Other.cs" };

        Assert.That(
            ScenarioRescoring.TargetPath(keys, "New.cs"),
            Is.EqualTo("ContosoOrders.Core/FixtureHelpers/New.cs"));
    }

    [Test]
    public async Task RescoreRunFolder_UsesTheVerdictAndScoresTurnCapWithCorrectStateAsPassed()
    {
        WriteResults(Row(
            ("scenarioId", "S1"),
            ("scenarioType", "3"),
            ("sampleIndex", "1"),
            ("commit", "abc"),
            ("dirty", "false"),
            ("liftRenames", "off"),
            ("firstCallSucceeded", "false"),
            ("firstErrorCode", "NotFound"),
            ("recovered", "false"),
            ("markerPresent", "false"),
            ("turnCount", "2"),
            ("stopReason", "TurnCapExceeded"),
            ("failureClass", "BudgetOut"),
            ("passed", "false"),
            ("callsAfterFirstError", "1"),
            ("turnsAfterFirstError", "1"),
            ("maxIdenticalCallRepeat", "1"),
            ("schemaDrift", "false"),
            ("resultDrift", "0"),
            ("transcriptPath", "x")));

        var sampleFolder = Path.Combine(_folder, "sample-01");
        var finalState = Path.Combine(sampleFolder, "final-state");
        Directory.CreateDirectory(finalState);
        File.WriteAllText(Path.Combine(finalState, "Dummy.cs"), "// dummy");

        var transcript = new AgentTranscript();
        transcript.Turns.Add(Turn(1, Call(Tool, "{\"a\":1}")));
        File.WriteAllText(Path.Combine(sampleFolder, "transcript.json"), JsonSerializer.Serialize(transcript));

        var scenario = Scenario("S1");
        var outputCsv = Path.Combine(_folder, "rescored.csv");

        var samples = await ScenarioRescoring.RescoreRunFolderAsync(
            _folder,
            id => id == "S1" ? scenario : null,
            (s, folder, ct) => Task.FromResult<FinalStateVerdict?>(FinalStateVerdict.Correct),
            outputCsv,
            CancellationToken.None);

        Assert.That(samples, Has.Count.EqualTo(1));
        var score = samples[0].Score;
        Assert.That(score, Is.Not.Null);
        Assert.That(score!.Passed, Is.True);
        Assert.That(score.FailureClass, Is.EqualTo(FailureClass.None));
        Assert.That(score.TurnCapHit, Is.True);
        Assert.That(score.Verdict, Is.EqualTo(FinalStateVerdict.Correct));
        Assert.That(File.ReadAllLines(outputCsv), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task RescoreRunFolder_ResumeScenario_IgnoresSeededTurnErrors()
    {
        WriteResults(Row(
            ("scenarioId", "S1"),
            ("scenarioType", "3"),
            ("sampleIndex", "1"),
            ("commit", "abc"),
            ("dirty", "false"),
            ("liftRenames", "off"),
            ("schemaDrift", "false"),
            ("resultDrift", "0"),
            ("transcriptPath", "x")));

        var sampleFolder = Path.Combine(_folder, "sample-01");
        Directory.CreateDirectory(sampleFolder);

        var failedCall = new AgentToolCallRecord
        {
            ToolName = Tool,
            ArgumentsJson = "{}",
            ResultJson = "{\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\"}}",
            IsError = true,
            Latency = TimeSpan.Zero,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            ToolCallId = "seeded",
        };
        var transcript = new AgentTranscript();
        transcript.Turns.Add(Turn(1, failedCall));
        transcript.Turns.Add(Turn(2, Call(Tool, "{\"a\":2}")));
        File.WriteAllText(Path.Combine(sampleFolder, "transcript.json"), JsonSerializer.Serialize(transcript));

        var scenario = Scenario("S1") with { ResumeTranscript = "x.json", ResumeKeepTurns = 1 };
        var outputCsv = Path.Combine(_folder, "rescored.csv");

        var samples = await ScenarioRescoring.RescoreRunFolderAsync(
            _folder,
            id => id == "S1" ? scenario : null,
            (s, folder, ct) => Task.FromResult<FinalStateVerdict?>(FinalStateVerdict.Correct),
            outputCsv,
            CancellationToken.None);

        Assert.That(samples, Has.Count.EqualTo(1));
        var score = samples[0].Score;
        Assert.That(score, Is.Not.Null);
        Assert.That(score!.FirstErrorCode, Is.Null);
        Assert.That(score.Recovered, Is.False);
        Assert.That(score.FirstCallSucceeded, Is.True);
    }

    [Test]
    public async Task RescoreRunFolder_KeepsInconclusiveRowsInconclusive()
    {
        WriteResults(Row(
            ("scenarioId", "S1"),
            ("scenarioType", "3"),
            ("sampleIndex", "1"),
            ("commit", "abc"),
            ("dirty", "false"),
            ("liftRenames", "off"),
            ("schemaDrift", "false"),
            ("resultDrift", "0"),
            ("inconclusiveReason", "HarnessError: x"),
            ("transcriptPath", "x")));
        var verdictCalls = 0;

        var samples = await ScenarioRescoring.RescoreRunFolderAsync(
            _folder,
            id => null,
            (s, folder, ct) =>
            {
                verdictCalls++;
                return Task.FromResult<FinalStateVerdict?>(FinalStateVerdict.Correct);
            },
            Path.Combine(_folder, "rescored.csv"),
            CancellationToken.None);

        Assert.That(samples, Has.Count.EqualTo(1));
        Assert.That(samples[0].IsInconclusive, Is.True);
        Assert.That(samples[0].InconclusiveReason, Is.EqualTo("HarnessError: x"));
        Assert.That(samples[0].Score, Is.Null);
        Assert.That(verdictCalls, Is.Zero);
    }

    [Test]
    public async Task RescoreRunFolder_ThrowsForUnknownScenario()
    {
        WriteResults(Row(
            ("scenarioId", "Missing"),
            ("scenarioType", "3"),
            ("sampleIndex", "1"),
            ("commit", "abc"),
            ("dirty", "false"),
            ("liftRenames", "off"),
            ("stopReason", "TurnCapExceeded"),
            ("schemaDrift", "false"),
            ("resultDrift", "0"),
            ("transcriptPath", "x")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ScenarioRescoring.RescoreRunFolderAsync(
            _folder,
            id => null,
            (s, folder, ct) => Task.FromResult<FinalStateVerdict?>(FinalStateVerdict.Correct),
            Path.Combine(_folder, "rescored.csv"),
            CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("Missing"));
    }

    [Test]
    public async Task RescoreRunFolder_LegacyThirtyColumnResults_SetsLiveTurnsToTargetFromTheSavedTranscript()
    {
        Assert.That(LegacyHeader.Split(',').Length, Is.EqualTo(ScenarioSampleCsv.LegacyColumnCount));
        var transcript = TwoTurnTranscript();
        WriteRun(LegacyHeader, transcript, ColdRowValues);
        var scenario = Scenario("S1");

        var samples = await RescoreAsync(scenario, Path.Combine(_folder, "rescored.csv"));

        Assert.That(samples, Has.Count.EqualTo(1));
        Assert.That(samples[0].Score, Is.Not.Null);
        Assert.That(samples[0].LiveTurnsToTarget, Is.EqualTo(scenario.LiveTurnsFor(transcript.Turns.Count)));
    }

    [Test]
    public async Task RescoreRunFolder_OutputCsvCarriesLiveTurnsToTarget()
    {
        var transcript = TwoTurnTranscript();
        WriteRun(LegacyHeader, transcript, ColdRowValues);
        var scenario = Scenario("S1");
        var outputCsv = Path.Combine(_folder, "rescored.csv");

        await RescoreAsync(scenario, outputCsv);

        var lines = File.ReadAllLines(outputCsv);
        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(ScenarioSampleCsv.Parse(lines[1]).LiveTurnsToTarget, Is.EqualTo(scenario.LiveTurnsFor(transcript.Turns.Count)));
    }

    [Test]
    public async Task RescoreRunFolder_ResumeScenario_LiveTurnsToTargetExcludesTheKeptTurn()
    {
        // Two turns, one kept: one live turn.
        WriteRun(ScenarioSampleCsv.Header, TwoTurnTranscript(), ColdRowValues);
        var scenario = Scenario("S1") with { ResumeTranscript = "x.json", ResumeKeepTurns = 1 };

        var samples = await RescoreAsync(scenario, Path.Combine(_folder, "rescored.csv"));

        Assert.That(samples, Has.Count.EqualTo(1));
        Assert.That(samples[0].LiveTurnsToTarget, Is.EqualTo(1));
    }

    private Task<IReadOnlyList<ScenarioSample>> RescoreAsync(ScenarioDefinition scenario, string outputCsv) =>
        ScenarioRescoring.RescoreRunFolderAsync(
            _folder,
            id => id == "S1" ? scenario : null,
            (s, folder, ct) => Task.FromResult<FinalStateVerdict?>(FinalStateVerdict.Correct),
            outputCsv,
            CancellationToken.None);

    // Writes results.csv with one row under header and the sample-01 transcript.
    private void WriteRun(string header, AgentTranscript transcript, params (string Name, string Value)[] values)
    {
        File.WriteAllLines(Path.Combine(_folder, "results.csv"), new[] { header, RowFor(header, values) });
        var sampleFolder = Path.Combine(_folder, "sample-01");
        Directory.CreateDirectory(sampleFolder);
        File.WriteAllText(Path.Combine(sampleFolder, "transcript.json"), JsonSerializer.Serialize(transcript));
    }

    private static AgentTranscript TwoTurnTranscript()
    {
        var transcript = new AgentTranscript();
        transcript.Turns.Add(Turn(1, Call(Tool, "{\"a\":1}")));
        transcript.Turns.Add(Turn(2, Call(Tool, "{\"a\":2}")));
        return transcript;
    }

    private void WriteResults(params string[] rows) =>
        File.WriteAllLines(Path.Combine(_folder, "results.csv"), new[] { OldHeader }.Concat(rows));

    // Builds one OldHeader row by column name; columns not given are empty.
    private static string Row(params (string Name, string Value)[] values) => RowFor(OldHeader, values);

    // Builds one row under header by column name; columns not given are empty.
    private static string RowFor(string header, params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return string.Join(",", header.Split(',').Select(name => map.GetValueOrDefault(name, string.Empty)));
    }

    private static ScenarioDefinition Scenario(string id) => new(
        Id: id,
        ScenarioType: 3,
        FaultKind: string.Empty,
        ToolUnderTest: Tool,
        StagedFiles: new Dictionary<string, string>(),
        PromptTemplate: string.Empty,
        TurnCap: 8,
        Expected: ExpectedOutcome.Recover,
        ExpectedRecoveryTool: null,
        CheckFinalState: (dir, ct) => Task.FromResult(FinalStateVerdict.Correct),
        SourceRun: string.Empty,
        ReproducesAt: string.Empty,
        FixedBy: string.Empty);

    private static AgentToolCallRecord Call(string toolName, string argumentsJson = "{}") => new()
    {
        ToolName = toolName,
        ArgumentsJson = argumentsJson,
        ResultJson = "{\"isError\":false}",
        IsError = false,
        Latency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCallId = "test",
    };

    private static AgentTranscriptTurn Turn(int turnNumber, params AgentToolCallRecord[] calls) => new()
    {
        TurnNumber = turnNumber,
        ModelMessage = new AgentChatMessage { Role = "assistant", Content = null, ReasoningContent = null },
        ModelLatency = TimeSpan.Zero,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        ToolCalls = calls.ToList(),
    };
}
