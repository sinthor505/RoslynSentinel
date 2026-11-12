using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Runs the samples of one targeted scenario one after another, scores each one and appends one results CSV line per sample.
/// Pure loop: no model and no MCP. The live fixture supplies the per-sample run as a delegate.
/// </summary>
public static class ScenarioSampleLoop
{
    /// <summary>
    /// Runs <paramref name="samples"/> samples. <paramref name="runSample"/> runs one sample (sampleIndex is 1-based, the
    /// directory is created by this loop) and returns its run result and the final-state verdict (null when none could be computed).
    /// A thrown exception (other than cancellation) becomes an Inconclusive sample; cancellation propagates.
    /// </summary>
    public static Task<IReadOnlyList<ScenarioSample>> RunAsync(
        ScenarioDefinition scenario,
        int samples,
        string runDirectory,
        string csvPath,
        string liftRenames,
        Func<int, string, CancellationToken, Task<(AgentRunResult Result, FinalStateVerdict? Verdict)>> runSample,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        return RunSamplesAsync(
            scenario,
            samples,
            runDirectory,
            csvPath,
            liftRenames,
            async (i, dir, token) =>
            {
                var o = await runSample(i, dir, token);
                return (o.Result, o.Verdict, false, 0, "");
            },
            cancellationToken,
            log);
    }

    /// <summary>
    /// Runs <paramref name="samples"/> samples and appends one CSV line per sample. <paramref name="runSample"/> runs one sample
    /// (sampleIndex is 1-based, the directory is created by this loop) and returns its run result (null when none was produced),
    /// the final-state verdict (null when none could be computed), the schema and result drift counts, and an inconclusive reason.
    /// A non-empty reason makes the sample Inconclusive with no score, keeping the reason and drift counts; a null result with no
    /// reason becomes a HarnessError. A thrown exception (other than cancellation) becomes an Inconclusive sample; cancellation propagates.
    /// </summary>
    public static async Task<IReadOnlyList<ScenarioSample>> RunSamplesAsync(
        ScenarioDefinition scenario,
        int samples,
        string runDirectory,
        string csvPath,
        string liftRenames,
        Func<int, string, CancellationToken, Task<(AgentRunResult? Result, FinalStateVerdict? Verdict, bool SchemaDrift, int ResultDrift, string InconclusiveReason)>> runSample,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        var ran = new List<ScenarioSample>(samples);
        for (var i = 1; i <= samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleDirectory = Path.Combine(runDirectory, $"sample-{i:D2}");
            Directory.CreateDirectory(sampleDirectory);

            ScenarioSample sample;
            try
            {
                var outcome = await runSample(i, sampleDirectory, cancellationToken);
                var result = outcome.Result;
                var reason = outcome.InconclusiveReason;
                if (result is null && reason.Length == 0)
                {
                    reason = "HarnessError: no run result";
                }

                if (result is null || reason.Length > 0)
                {
                    // The run did not complete cleanly: keep what was recorded (stamp when there is no result, the
                    // partial transcript when there is no result path) and the drift counts beside the CSV line.
                    var stamp = ModelAgentRunner.ProcessStamp;
                    var partialTranscript = Path.Combine(sampleDirectory, "transcript.json");
                    sample = new ScenarioSample(
                        scenario.Id,
                        scenario.ScenarioType,
                        scenario.FaultKind,
                        i,
                        result is null ? (stamp?.CommitHash ?? string.Empty) : (result.Transcript.CommitHash ?? string.Empty),
                        result is null ? stamp?.Dirty : result.Transcript.WorkingTreeDirty,
                        liftRenames,
                        null,
                        outcome.SchemaDrift,
                        outcome.ResultDrift,
                        reason,
                        result is not null ? result.TranscriptPath : (File.Exists(partialTranscript) ? partialTranscript : string.Empty),
                        ErrorPolicyMode: result is null ? (stamp?.ErrorPolicyMode ?? string.Empty) : (result.Transcript.ErrorPolicyMode ?? string.Empty),
                        ErrorPolicyHash: result is null ? (stamp?.ErrorPolicyHash ?? string.Empty) : (result.Transcript.ErrorPolicyHash ?? string.Empty));
                }
                else
                {
                    var score = ScenarioScorer.Score(
                        result.Transcript,
                        scenario.ToolUnderTest,
                        scenario.Expected,
                        scenario.ExpectedRecoveryTool,
                        outcome.Verdict,
                        result.StopReason,
                        seededTurns: scenario.ResumeKeepTurns,
                        idealTurns: scenario.IdealTurns,
                        turnCap: scenario.TurnCap);
                    sample = new ScenarioSample(
                        scenario.Id,
                        scenario.ScenarioType,
                        scenario.FaultKind,
                        i,
                        result.Transcript.CommitHash ?? string.Empty,
                        result.Transcript.WorkingTreeDirty,
                        liftRenames,
                        score,
                        outcome.SchemaDrift,
                        outcome.ResultDrift,
                        InconclusiveReason: string.Empty,
                        result.TranscriptPath,
                        LiveTurnsToTarget: scenario.LiveTurnsFor(score.TurnCount),
                        ErrorPolicyMode: result.Transcript.ErrorPolicyMode ?? string.Empty,
                        ErrorPolicyHash: result.Transcript.ErrorPolicyHash ?? string.Empty);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A harness error still has the process stamp and often a partial transcript the runner wrote
                // before failing; keep both beside the CSV line.
                var stamp = ModelAgentRunner.ProcessStamp;
                var partialTranscript = Path.Combine(sampleDirectory, "transcript.json");
                sample = new ScenarioSample(
                    scenario.Id,
                    scenario.ScenarioType,
                    scenario.FaultKind,
                    i,
                    stamp?.CommitHash ?? string.Empty,
                    stamp?.Dirty,
                    liftRenames,
                    null,
                    SchemaDrift: false,
                    ResultDrift: 0,
                    InconclusiveReason: "HarnessError: " + FirstLine(ex.Message),
                    TranscriptPath: File.Exists(partialTranscript) ? partialTranscript : string.Empty,
                    ErrorPolicyMode: stamp?.ErrorPolicyMode ?? string.Empty,
                    ErrorPolicyHash: stamp?.ErrorPolicyHash ?? string.Empty);
            }

            ScenarioSampleCsv.Append(csvPath, sample);
            log?.Invoke($"Sample {i}: {ScenarioSampleCsv.ToCsvLine(sample)}");
            ran.Add(sample);
        }

        return ran;
    }

    /// <summary>"unset" when ROSLYNSENTINEL_LIFT_RENAMES is null or empty, else "on" when LiftRenamesOptions.Enabled, else "off".</summary>
    public static string LiftRenamesState()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LIFT_RENAMES")))
        {
            return "unset";
        }

        return LiftRenamesOptions.Enabled ? "on" : "off";
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        var line = newline < 0 ? message : message[..newline];
        return line.TrimEnd('\r');
    }
}
