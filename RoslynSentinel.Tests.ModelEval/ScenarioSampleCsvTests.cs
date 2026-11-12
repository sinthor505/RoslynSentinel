using System.IO;
using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public sealed class ScenarioSampleCsvTests
{
    private static readonly ScoreResult FullScore = new(
        FirstCallSucceeded: false,
        FirstErrorCode: "ValidationFailed",
        FirstErrorClass: ToolErrorClass.CompileGateRejection,
        Recovered: true,
        CallsToRecover: 3,
        RecoveryUsedExpectedTool: true,
        MarkerPresent: false,
        TurnCount: 12,
        StopReason: AgentStopReason.ModelFinished,
        FailureClass: FailureClass.IdenticalResend,
        Passed: false,
        CallsAfterFirstError: 4,
        TurnsAfterFirstError: 3,
        OutputCharsAfterFirstError: 1500,
        MaxIdenticalCallRepeat: 2,
        IdenticalReasoningBlocks: 1,
        RetryPhraseCount: 2);

    private static readonly ScenarioSample FullSample = new(
        ScenarioId: "rename-after-error",
        ScenarioType: 3,
        FaultKind: "ParameterRename",
        SampleIndex: 2,
        Commit: "bf403f75",
        Dirty: false,
        LiftRenames: "on",
        Score: FullScore,
        SchemaDrift: false,
        ResultDrift: 0,
        InconclusiveReason: "",
        TranscriptPath: "C:\\tmp\\runs\\run \"one\", two\\transcript.json");

    private static readonly ScenarioSample InconclusiveSample = new(
        ScenarioId: "rename-after-error",
        ScenarioType: 4,
        FaultKind: "",
        SampleIndex: 1,
        Commit: "",
        Dirty: null,
        LiftRenames: "unset",
        Score: null,
        SchemaDrift: true,
        ResultDrift: 0,
        InconclusiveReason: "schema changed, tool ReplaceSnippet: parameter \"oldContent\" removed",
        TranscriptPath: "");

    [Test]
    public void FullSampleRoundTripsThroughCsvLine()
    {
        var line = ScenarioSampleCsv.ToCsvLine(FullSample);

        var parsed = ScenarioSampleCsv.Parse(line);

        Assert.That(parsed, Is.EqualTo(FullSample));
        Assert.That(parsed.IsInconclusive, Is.False);
    }

    [Test]
    public void InconclusiveSampleRoundTripsWithNullScore()
    {
        var line = ScenarioSampleCsv.ToCsvLine(InconclusiveSample);

        var parsed = ScenarioSampleCsv.Parse(line);

        Assert.That(parsed, Is.EqualTo(InconclusiveSample));
        Assert.That(parsed.Score, Is.Null);
        Assert.That(parsed.IsInconclusive, Is.True);
    }

    [Test]
    public void RoundTrip_CarriesVerdictAndTurnCapHit()
    {
        var sample = FullSample with
        {
            Score = FullScore with
            {
                Verdict = FinalStateVerdict.Correct,
                TurnCapHit = true
            },
        };

        var parsed = ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(sample));

        Assert.That(parsed, Is.EqualTo(sample));
        Assert.That(parsed.Score!.Verdict, Is.EqualTo(FinalStateVerdict.Correct));
        Assert.That(parsed.Score.TurnCapHit, Is.True);
    }

    [Test]
    public void HeaderColumnCountMatchesEveryRow()
    {
        Assert.That(ScenarioSampleCsv.Header.Split(',').Length, Is.EqualTo(30));

        // Parse rejects any row whose column count differs from Header, so a clean parse proves the count.
        Assert.That(() => ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(FullSample)), Throws.Nothing);
        Assert.That(() => ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(InconclusiveSample)), Throws.Nothing);
    }

    [Test]
    public void Header_NamesVerdictAndTurnCapHitAfterPassed()
    {
        var columns = ScenarioSampleCsv.Header.Split(',');

        Assert.That(columns, Has.Length.EqualTo(30));
        Assert.That(columns[17], Is.EqualTo("passed"));
        Assert.That(columns[18], Is.EqualTo("finalStateVerdict"));
        Assert.That(columns[19], Is.EqualTo("turnCapHit"));
    }

    [Test]
    public void AppendWritesHeaderOnceThenOneLinePerSample()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ScenarioSampleCsvTests_" + System.Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "results.csv");
        try
        {
            ScenarioSampleCsv.Append(path, FullSample);
            var afterFirst = File.ReadAllLines(path);
            Assert.That(afterFirst, Has.Length.EqualTo(2));
            Assert.That(afterFirst[0], Is.EqualTo(ScenarioSampleCsv.Header));
            Assert.That(afterFirst[1], Is.EqualTo(ScenarioSampleCsv.ToCsvLine(FullSample)));

            ScenarioSampleCsv.Append(path, InconclusiveSample);
            var afterSecond = File.ReadAllLines(path);
            Assert.That(afterSecond, Has.Length.EqualTo(3));
            Assert.That(afterSecond[0], Is.EqualTo(ScenarioSampleCsv.Header));
            Assert.That(afterSecond[1], Is.EqualTo(ScenarioSampleCsv.ToCsvLine(FullSample)));
            Assert.That(afterSecond[2], Is.EqualTo(ScenarioSampleCsv.ToCsvLine(InconclusiveSample)));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Test]
    public void ParseThrowsFormatExceptionForTooFewFields()
    {
        Assert.That(
            () => ScenarioSampleCsv.Parse("rename-after-error,3,ParameterRename"),
            Throws.TypeOf<FormatException>());
    }
}
