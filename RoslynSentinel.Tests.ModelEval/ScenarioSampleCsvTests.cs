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
        Assert.That(ScenarioSampleCsv.Header.Split(',').Length, Is.EqualTo(33));

        // Parse rejects any row whose column count differs from Header, so a clean parse proves the count.
        Assert.That(() => ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(FullSample)), Throws.Nothing);
        Assert.That(() => ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(InconclusiveSample)), Throws.Nothing);
    }

    [Test]
    public void Header_NamesVerdictAndTurnCapHitAfterPassed()
    {
        var columns = ScenarioSampleCsv.Header.Split(',');

        Assert.That(columns, Has.Length.EqualTo(33));
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

    [Test]
    public void Header_EndsWithErrorPolicyColumnsAndHasThirtyThreeColumns()
    {
        var columns = ScenarioSampleCsv.Header.Split(',');

        Assert.That(ScenarioSampleCsv.Header, Does.EndWith(",liveTurnsToTarget,errorPolicyMode,errorPolicyHash"));
        Assert.That(columns.Length, Is.EqualTo(33));
        Assert.That(columns[30], Is.EqualTo("liveTurnsToTarget"));
        Assert.That(ScenarioSampleCsv.PreErrorPolicyColumnCount, Is.EqualTo(31));
        Assert.That(ScenarioSampleCsv.LegacyColumnCount, Is.EqualTo(30));
    }

    [Test]
    public void LiveTurnsToTarget_RoundTripsThroughCsvLine()
    {
        var sample = FullSample with { LiveTurnsToTarget = 4 };

        var parsed = ScenarioSampleCsv.Parse(ScenarioSampleCsv.ToCsvLine(sample));

        Assert.That(parsed.LiveTurnsToTarget, Is.EqualTo(4));
        Assert.That(parsed, Is.EqualTo(sample));
    }

    [Test]
    public void LiveTurnsToTarget_NullRoundTripsWithEmptyField()
    {
        var line = ScenarioSampleCsv.ToCsvLine(FullSample with { LiveTurnsToTarget = null });

        var parsed = ScenarioSampleCsv.Parse(line);

        Assert.That(parsed.LiveTurnsToTarget, Is.Null);
        Assert.That(ScenarioSampleCsv.SplitFields(line)[30], Is.Empty);
    }

    [Test]
    public void LegacyThirtyColumnRow_ParsesWithNullLiveTurnsToTargetAndEmptyErrorPolicy()
    {
        // Drop the last three fields (liveTurnsToTarget, errorPolicyMode, errorPolicyHash) to produce a row written before they existed.
        var line = ScenarioSampleCsv.ToCsvLine(FullSample with { LiveTurnsToTarget = 4, ErrorPolicyMode = "pointer", ErrorPolicyHash = "0123456789ab" });
        var fields = ScenarioSampleCsv.SplitFields(line);
        var legacy = string.Join(',', fields.Take(30).Select(f => f.IndexOfAny([',', '"']) < 0 ? f : "\"" + f.Replace("\"", "\"\"") + "\""));
        Assert.That(ScenarioSampleCsv.SplitFields(legacy), Has.Count.EqualTo(ScenarioSampleCsv.LegacyColumnCount));

        var parsed = ScenarioSampleCsv.Parse(legacy);

        Assert.That(parsed.LiveTurnsToTarget, Is.Null);
        Assert.That(parsed.ErrorPolicyMode, Is.Empty);
        Assert.That(parsed.ErrorPolicyHash, Is.Empty);
        Assert.That(parsed, Is.EqualTo(FullSample));
    }

    [Test]
    public void ErrorPolicyColumns_RoundTripThroughCsvLine()
    {
        var sample = FullSample with { LiveTurnsToTarget = 4, ErrorPolicyMode = "inline", ErrorPolicyHash = "0123456789ab" };

        var line = ScenarioSampleCsv.ToCsvLine(sample);
        var parsed = ScenarioSampleCsv.Parse(line);

        Assert.That(ScenarioSampleCsv.SplitFields(line), Has.Count.EqualTo(33));
        Assert.That(parsed.ErrorPolicyMode, Is.EqualTo("inline"));
        Assert.That(parsed.ErrorPolicyHash, Is.EqualTo("0123456789ab"));
        Assert.That(parsed, Is.EqualTo(sample));
    }

    [Test]
    public void ErrorPolicyColumns_WithCommaAndQuote_AreQuotedAndRoundTrip()
    {
        var sample = FullSample with { ErrorPolicyMode = "pointer, \"x\"", ErrorPolicyHash = "a,b\"c" };

        var line = ScenarioSampleCsv.ToCsvLine(sample);
        var parsed = ScenarioSampleCsv.Parse(line);

        Assert.That(ScenarioSampleCsv.SplitFields(line), Has.Count.EqualTo(33));
        Assert.That(parsed.ErrorPolicyMode, Is.EqualTo("pointer, \"x\""));
        Assert.That(parsed.ErrorPolicyHash, Is.EqualTo("a,b\"c"));
        Assert.That(parsed, Is.EqualTo(sample));
    }

    [Test]
    public void PreviousThirtyOneColumnRow_ParsesWithEmptyErrorPolicyAndReserializesWithThirtyThreeColumns()
    {
        // Drop the last two fields (errorPolicyMode, errorPolicyHash) to produce a row written before they existed.
        var line = ScenarioSampleCsv.ToCsvLine(FullSample with { LiveTurnsToTarget = 4 });
        var previous = line[..line.LastIndexOf(',')];
        previous = previous[..previous.LastIndexOf(',')];
        Assert.That(ScenarioSampleCsv.SplitFields(previous), Has.Count.EqualTo(ScenarioSampleCsv.PreErrorPolicyColumnCount));

        var parsed = ScenarioSampleCsv.Parse(previous);
        var rewritten = ScenarioSampleCsv.ToCsvLine(parsed);

        Assert.That(parsed.LiveTurnsToTarget, Is.EqualTo(4));
        Assert.That(parsed.ErrorPolicyMode, Is.Empty);
        Assert.That(parsed.ErrorPolicyHash, Is.Empty);
        Assert.That(ScenarioSampleCsv.SplitFields(rewritten), Has.Count.EqualTo(33));
        Assert.That(ScenarioSampleCsv.Parse(rewritten), Is.EqualTo(parsed));
    }

    [Test]
    public void ParseThrowsFormatExceptionForTwentyNineColumns()
    {
        // 28 commas make 29 empty fields: none of the 30-, 31- or 33-column layouts.
        Assert.That(
            () => ScenarioSampleCsv.Parse(new string(',', 28)),
            Throws.TypeOf<FormatException>().With.Message.Contains("Expected 30, 31 or 33 CSV columns, found 29."));
    }

    [Test]
    public void LiveTurnsFor_ColdStartCountsEveryTurn()
    {
        Assert.That(FaultScenarios.RenameSymbolNoSuchMethod.LiveTurnsFor(5), Is.EqualTo(5));
    }

    [Test]
    public void LiveTurnsFor_ResumeScenarioExcludesKeptTurns()
    {
        Assert.That(ResumeScenarios.StaleCallerEditAfterRename.LiveTurnsFor(14), Is.EqualTo(3));
    }

    [Test]
    public void LiveTurnsFor_SeededResumeScenarioExcludesKeptTurnsAndFaultTurn()
    {
        Assert.That(ResumeScenarios.FailedLastCallStaleOldContent.LiveTurnsFor(14), Is.EqualTo(2));
    }

    [Test]
    public void LiveTurnsFor_AuthoredOnlySeedExcludesFaultTurn()
    {
        Assert.That(FaultScenarios.RenameSymbolTypo.LiveTurnsFor(4), Is.EqualTo(3));
    }

    [Test]
    public void LiveTurnsFor_ClampsAtZero()
    {
        Assert.That(FaultScenarios.RenameSymbolTypo.LiveTurnsFor(0), Is.EqualTo(0));
    }
}
