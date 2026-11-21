using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Measures one decision point of a stepped ShippingKind run without replaying the whole run: takes a saved transcript
/// (ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT) from <see cref="ShippingKindSteppedAgentTests"/>, keeps its turns 1..K
/// (_RESUME_KEEP_TURNS), rebuilds the fixture at that point (the golden commit of step N-1, N from
/// ROSLYNSENTINEL_MODELEVAL_RESUME_STEP, then the kept tool calls re-executed), seeds the conversation with those turns and lets
/// the live model generate _RESUME_CONTINUE_TURNS more turns, _RESUME_SAMPLES times. Typical use: keep the turns up to the
/// read of ShippingKinds.cs, continue 1 turn, and count how often the model misspells a constant in that one turn
/// (scripts/Get-ShippingKindTypoCounts.ps1 reads the per-sample transcript.json files).
/// It asserts nothing about what the model does: one results.csv row per sample (<see cref="ResumeSampleRow"/>) plus a
/// per-sample transcript.json and agent.log under sample-NN/. Each sample gets its own fixture, host and MCP client, because a
/// sample that edits would otherwise hand the next one an already-changed tree. The transcript's own system and user prompt
/// are used verbatim.
/// </summary>
[TestFixture]
public class ShippingKindResumeAgentTests
{
    /// <summary>The ShippingKind step (1-6) the transcript came from; selects the golden commit the sample starts from.</summary>
    public const string StepVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_STEP";

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1 -Test ShippingKindResume -ResumeTranscript <path> -ResumeStep <n>")]
    public async Task ResumeShippingKindStep_SamplesTheNextTurns()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        if (!GitTestRepo.IsGitAvailable())
        {
            Assert.Ignore("git is not available - the stepped scenario keeps its golden steps in a git repository.");
        }

        var settings = ResumeSettings.FromEnvironment();
        if (settings.TranscriptPath is null)
        {
            Assert.Ignore($"{ResumeSettings.TranscriptVariable} is not set - point it at a saved ShippingKind transcript.json (or its run folder).");
        }

        if (!int.TryParse(Environment.GetEnvironmentVariable(StepVariable), out var step)
            || step < 1 || step > ShippingKindStepGoldens.LastStep)
        {
            Assert.Ignore($"{StepVariable} must be the ShippingKind step (1-{ShippingKindStepGoldens.LastStep}) the transcript came from.");
        }

        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var source = TranscriptResume.Load(TranscriptResume.ResolveTranscriptPath(settings.TranscriptPath!));
        var keptTurns = TranscriptResume.KeepTurns(source, settings.KeepTurns);

        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        // TODO the run folder is archived only in the finally below, so the eval queue sees no run folder until all samples end (about 4-5 min each on tiel); submit with -NoRunFolderMinutes and -TimeLimitMinutes above Samples x 5, or archive per sample
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");

        TestContext.Out.WriteLine(
            $"Resuming '{settings.TranscriptPath}' (step {step}) after turn {settings.KeepTurns} of {source.Turns.Count}: " +
            $"{settings.Samples} sample(s) x {settings.ContinueTurns} live turn(s).");

        var rows = new List<ResumeSampleRow>();
        try
        {
            for (var sampleIndex = 1; sampleIndex <= settings.Samples; sampleIndex++)
            {
                ResumeSampleRow row;
                try
                {
                    row = await RunSampleAsync(
                        sampleIndex, step, settings, source, keptTurns,
                        Path.Combine(runDirectory, $"sample-{sampleIndex:D2}"), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row = TranscriptResume.BuildHarnessErrorRow(sampleIndex, LoopAssistOptions.Off, ex.Message);
                }

                rows.Add(row);
                TranscriptResume.AppendRow(csvPath, row);
                TestContext.Out.WriteLine($"Sample {sampleIndex}: {row.ToCsvLine()}");
            }
        }
        finally
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);
        }

        Assert.That(
            rows.Any(r => !r.IsHarnessError), Is.True,
            "Every sample failed with a harness error, so nothing was measured. First error: " +
            $"{rows.FirstOrDefault()?.HarnessError}. See {csvPath}.");
    }

    private static async Task<ResumeSampleRow> RunSampleAsync(
        int sampleIndex,
        int step,
        ResumeSettings settings,
        ResumeSource source,
        IReadOnlyList<AgentTranscriptTurn> keptTurns,
        string sampleDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sampleDirectory);
        // restorePackages: the step's golden tree includes ContosoOrders.Tests, which needs restore assets for a solution-scope build.
        await using var environment = await ModelEvalMcpEnvironment.CreateAsync(
            sampleDirectory,
            restorePackages: true,
            beforeLoad: (fixture, _, _) =>
            {
                CheckoutGoldenStep(fixture, step);
                return Task.CompletedTask;
            },
            afterLoad: null,
            cancellationToken);

        var directory = environment.SolutionDirectory;
        var seed = TranscriptResume.RewriteTurns(keptTurns, directory);
        var systemPrompt = TranscriptResume.RewriteFixtureDirectory(source.SystemPrompt, directory)!;
        var userPrompt = TranscriptResume.RewriteFixtureDirectory(source.UserPrompt, directory)!;

        // Same fixture state as at the resume point: the kept calls run again, in order, on the fresh fixture.
        var mismatches = await TranscriptResume.ReplayKeptCallsAsync(
            seed,
            (tool, arguments, token) => TranscriptReplayHelper.ReplayToolCallAsync(environment.Client, tool, arguments, token),
            cancellationToken,
            TestContext.Out.WriteLine);
        if (mismatches > 0)
        {
            TestContext.Out.WriteLine($"Sample {sampleIndex}: {mismatches} replayed call(s) disagreed with the recorded IsError; the fixture may not match the resume point.");
        }

        // turnCap counts the seeded turns, so the live loop gets exactly ContinueTurns more. Loop assist stays off, as in the stepped runs.
        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: settings.KeepTurns + settings.ContinueTurns,
            wallClockCap: TimeSpan.FromMinutes(15),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>(),
            loopAssist: LoopAssistOptions.Off);
        var result = await runner.RunAsync(
            systemPrompt, userPrompt, sampleDirectory, cancellationToken, seedTurns: seed);

        return TranscriptResume.BuildRow(
            sampleIndex, LoopAssistOptions.Off, settings.KeepTurns, settings.ContinueTurns,
            result.Transcript.Turns, result.Transcript.AssistCount ?? 0, result.StopReason.ToString());
    }

    /// <summary>
    /// Checks out the golden commit of step N-1 into the fixture. Runs before the solution is loaded, so the server
    /// never sees a harness write.
    /// </summary>
    private static void CheckoutGoldenStep(RoslynSentinel.Tests.TestSolutionFixture fixture, int step)
    {
        var repository = GoldenStepRepository.Create(
            fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, s => ShippingKindStepGoldens.FilesForStep(s, false));
        repository.CheckoutStep(step - 1, $"run/step-{step}");
    }
}
