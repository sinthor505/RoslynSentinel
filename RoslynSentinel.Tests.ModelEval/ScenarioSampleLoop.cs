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
    public static async Task<IReadOnlyList<ScenarioSample>> RunAsync(
        ScenarioDefinition scenario,
        int samples,
        string runDirectory,
        string csvPath,
        string liftRenames,
        Func<int, string, CancellationToken, Task<(AgentRunResult Result, FinalStateVerdict? Verdict)>> runSample,
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
                var score = ScenarioScorer.Score(
                    outcome.Result.Transcript,
                    scenario.ToolUnderTest,
                    scenario.Expected,
                    scenario.ExpectedRecoveryTool,
                    outcome.Verdict,
                    outcome.Result.StopReason);
                sample = new ScenarioSample(
                    scenario.Id,
                    scenario.ScenarioType,
                    scenario.FaultKind,
                    i,
                    outcome.Result.Transcript.CommitHash ?? string.Empty,
                    outcome.Result.Transcript.WorkingTreeDirty,
                    liftRenames,
                    score,
                    SchemaDrift: false,
                    ResultDrift: 0,
                    InconclusiveReason: string.Empty,
                    outcome.Result.TranscriptPath);
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
                    TranscriptPath: File.Exists(partialTranscript) ? partialTranscript : string.Empty);
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
