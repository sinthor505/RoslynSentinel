using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Runs one targeted scenario (ROSLYNSENTINEL_MODELEVAL_SCENARIO, looked up in <see cref="ScenarioRegistry"/>) against the
/// loaded model, ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES times (default 10). Each sample gets its own fixture, host and
/// MCP client with the scenario's staged files, is scored by <see cref="ScenarioSampleLoop"/> and appends one results.csv line.
/// Resume scenarios (ResumeTranscript set) replay their named capture's kept turns on a fresh fixture first and are gated
/// on schema and replay drift; cold-start scenarios run from the staged files only. The resume path also covers
/// seeded-fault scenarios with no transcript (authored-only seed, no gates).
/// Measurement only: it asserts that at least one sample ran, not what the model did.
/// </summary>
[TestFixture]
public class ScenarioAgentTests
{
    public const string ScenarioVariable = "ROSLYNSENTINEL_MODELEVAL_SCENARIO";
    public const string SamplesVariable = "ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES";

    private const int DefaultSamples = 10;

    public async Task RunScenario_SamplesAndScores()
    {
        await RunScenarioAsync(Environment.GetEnvironmentVariable(ScenarioVariable));
    }

    [Test]
    [Explicit("Requires loaded model. Run one stop-family definition: dotnet test RoslynSentinel.Tests.ModelEval --filter \"TestCategory=Family:StopAdherence\" (or TestCategory=Axis:<Name>); ROSLYNSENTINEL_LLM_MODEL must be set.")]
    [TestCaseSource(nameof(StopFamilyCases))]
    public async Task RunStopFamily_SamplesAndScores(string scenarioId)
    {
        await RunScenarioAsync(scenarioId);
    }

    /// <summary>NUnit category shared by every case of <see cref="RunStopFamily_SamplesAndScores"/>.</summary>
    public const string StopFamilyCategory = "Family:StopAdherence";

    // One case per registry definition that declares a PrimaryAxis. Both categories are generated, never hand-written per case:
    // the axis one from the property (same as ImplementationScenarioAgentTests.StepCases), the family one from the constant.
    public static IEnumerable<TestCaseData> StopFamilyCases()
    {
        foreach (var definition in ScenarioRegistry.All)
        {
            if (definition.PrimaryAxis == EvalAxis.Unassigned)
            {
                continue;
            }

            var testCase = new TestCaseData(definition.Id);
            testCase.SetCategory(EvalAxisCategory.Name(definition.PrimaryAxis));
            testCase.SetCategory(StopFamilyCategory);
            yield return testCase;
        }
    }

    // Model-free guard: every definition with a PrimaryAxis has exactly one stop-family case, carrying both categories.
    [Test]
    public void EveryAxisDefinition_HasACase()
    {
        var cases = StopFamilyCases().ToList();
        var caseIds = cases.Select(c => (string)c.Arguments[0]!).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var expectedIds = ScenarioRegistry.All
            .Where(d => d.PrimaryAxis != EvalAxis.Unassigned)
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.That(caseIds, Is.EqualTo(expectedIds), "StopFamilyCases must have exactly one case per definition that declares a PrimaryAxis.");

        foreach (var testCase in cases)
        {
            var id = (string)testCase.Arguments[0]!;
            var definition = ScenarioRegistry.Find(id)!;
            var categories = testCase.Properties["Category"].Cast<object>().Select(c => c.ToString()).ToList();
            Assert.That(categories, Does.Contain(EvalAxisCategory.Name(definition.PrimaryAxis)), $"{id} is missing its axis category.");
            Assert.That(categories, Does.Contain(StopFamilyCategory), $"{id} is missing {StopFamilyCategory}.");
        }
    }

    // A TestCaseData name such as RunStopFamily_SamplesAndScores("Id") holds parentheses and quotes; keep only characters valid in a directory name.
    internal static string SafeDirectoryName(string testName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(testName.Select(c => invalid.Contains(c) || c == '"' || c == '(' || c == ')' || c == ' ' ? '_' : c).ToArray());
        return cleaned.Trim('_');
    }

    // Shared body of RunScenario_SamplesAndScores (id from the env var) and RunStopFamily_SamplesAndScores (id from the case).
    private static async Task RunScenarioAsync(string? scenarioId)
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        if (string.IsNullOrEmpty(scenarioId))
        {
            Assert.Ignore($"{ScenarioVariable} is not set - pass -Scenario <id>.");
        }

        var scenario = ScenarioRegistry.Find(scenarioId);
        if (scenario is null)
        {
            var known = ScenarioRegistry.All.Count == 0
                ? "none registered"
                : string.Join(", ", ScenarioRegistry.All.Select(s => s.Id));
            Assert.Fail($"Unknown scenario '{scenarioId}'. Known scenarios: {known}.");
            return;
        }

        // Validate the cap override up front so a typo fails the run instead of quietly using the scenario default.
        try
        {
            _ = ScenarioDefinition.ParseTurnCapOverride(Environment.GetEnvironmentVariable(ScenarioDefinition.TurnCapVariable));
        }
        catch (FormatException ex)
        {
            Assert.Fail(ex.Message);
        }

        var samples = DefaultSamples;
        var samplesText = Environment.GetEnvironmentVariable(SamplesVariable);
        if (!string.IsNullOrEmpty(samplesText) && (!int.TryParse(samplesText, out samples) || samples <= 0))
        {
            Assert.Fail($"{SamplesVariable} must be a positive integer, got '{samplesText}'.");
        }

        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            SafeDirectoryName(TestContext.CurrentContext.Test.Name),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");
        var liftRenames = ScenarioSampleLoop.LiftRenamesState();

        TestContext.Out.WriteLine(
            $"Scenario {scenario.Id} (type {scenario.ScenarioType}, {EvalAxisCategory.Name(scenario.PrimaryAxis)}): {samples} sample(s), lift renames {liftRenames}, turn cap {scenario.EffectiveTurnCap} (scenario default {scenario.TurnCap}).");

        IReadOnlyList<ScenarioSample> ran;
        try
        {
            ran = await ScenarioSampleLoop.RunSamplesAsync(
                scenario,
                samples,
                runDirectory,
                csvPath,
                liftRenames,
                (sampleIndex, sampleDirectory, token) => RunSampleAsync(scenario, sampleIndex, sampleDirectory, token),
                cancellationToken,
                TestContext.Out.WriteLine);
        }
        finally
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);
        }

        Assert.That(
            ran.Any(s => !s.IsInconclusive), Is.True,
            "Every sample was inconclusive, so nothing was measured. First reason: " +
            $"{ran.FirstOrDefault(s => s.IsInconclusive)?.InconclusiveReason}. See {csvPath}.");
    }

    private static async Task<(AgentRunResult? Result, FinalStateVerdict? Verdict, bool SchemaDrift, int ResultDrift, string InconclusiveReason)> RunSampleAsync(
        ScenarioDefinition scenario,
        int sampleIndex,
        string sampleDirectory,
        CancellationToken cancellationToken)
    {
        if (scenario.IsResumeSample)
        {
            return await RunResumeSampleAsync(scenario, sampleIndex, sampleDirectory, cancellationToken);
        }

        await using var environment = await ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

        // Staging is done: a scenario whose whole tree must stay unchanged records it now and diffs it after the run.
        IReadOnlyDictionary<string, string>? treeBefore = scenario.WholeTreeMustStayUnchanged
            ? TreeSnapshot.Capture(environment.SolutionDirectory)
            : null;

        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: scenario.EffectiveTurnCap,
            wallClockCap: TimeSpan.FromMinutes(10),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(
            scenario.PromptTemplate,
            Path.Combine(environment.SolutionDirectory, "ContosoOrders.Core").Replace('\\', '/'));
        var result = await runner.RunAsync(EvalContractOptions.ComposeSystemPrompt(), userPrompt, sampleDirectory, cancellationToken);

        var verdict = await ComputeFinalStateAsync(scenario, sampleIndex, environment.SolutionDirectory, sampleDirectory, cancellationToken, treeBefore);
        return (result, verdict, false, 0, "");
    }

    /// <summary>
    /// Turn number for the next authored turn appended to a seed. An empty seed (authored-only scenario) starts at turn 1;
    /// otherwise the authored turn follows the last seeded turn.
    /// </summary>
    internal static int NextSeedTurnNumber(IReadOnlyList<RoslynSentinel.Common.AgentLoop.AgentTranscriptTurn> seed) =>
        seed.Count == 0 ? 1 : seed[^1].TurnNumber + 1;

    // Resume sample: replays the kept turns of the named capture on a fresh fixture, gates on schema and replay drift, then lets the model continue.
    private static async Task<(AgentRunResult? Result, FinalStateVerdict? Verdict, bool SchemaDrift, int ResultDrift, string InconclusiveReason)> RunResumeSampleAsync(
        ScenarioDefinition scenario,
        int sampleIndex,
        string sampleDirectory,
        CancellationToken cancellationToken)
    {
        await using var environment = await ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

        var directory = environment.SolutionDirectory;
        string systemPrompt;
        string userPrompt;
        List<RoslynSentinel.Common.AgentLoop.AgentTranscriptTurn> seed;
        var schemaDrift = false;
        var resultDrift = 0;
        if (scenario.ResumeTranscript is not null)
        {
            var source = TranscriptResume.Load(ResumeScenarios.CapturePath(scenario.ResumeTranscript));
            var kept = TranscriptResume.KeepTurns(source, scenario.ResumeKeepTurns);

            // Schema gate: the kept calls must still be offered with the schemas the transcript recorded.
            var liveTools = await environment.Client.ListToolsAsync(cancellationToken: cancellationToken);
            var liveSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var liveDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tool in liveTools)
            {
                if (tool.JsonSchema.ValueKind != JsonValueKind.Undefined)
                {
                    liveSchemas[tool.Name] = tool.JsonSchema;
                }

                liveDescriptions[tool.Name] = tool.Description ?? "";
            }

            (var schemaReason, schemaDrift) = ResumeScenarioGate.CheckSchemas(source, kept, liveSchemas, liveDescriptions);
            if (schemaReason.Length > 0)
            {
                TestContext.Out.WriteLine($"Sample {sampleIndex}: {schemaReason}");
                return (null, null, schemaDrift, 0, schemaReason);
            }

            seed = TranscriptResume.RewriteTurns(kept, directory).ToList();
            systemPrompt = TranscriptResume.RewriteFixtureDirectory(source.SystemPrompt, directory)!;
            userPrompt = TranscriptResume.RewriteFixtureDirectory(source.UserPrompt, directory)!;

            // Same fixture state as at the resume point: the kept calls run again, in order, on the fresh fixture.
            var replayed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
                seed,
                (tool, arguments, token) => TranscriptReplayHelper.ReplayToolCallAsync(environment.Client, tool, arguments, token),
                cancellationToken,
                TestContext.Out.WriteLine);
            (var replayReason, resultDrift) = ResumeScenarioGate.CheckReplay(replayed);
            if (replayReason.Length > 0)
            {
                TestContext.Out.WriteLine($"Sample {sampleIndex}: {replayReason}");
                return (null, null, schemaDrift, resultDrift, replayReason);
            }
        }
        else
        {
            // Authored-only seed: nothing to replay, so no schema or replay gate. The prompts are the cold-start prompts.
            seed = [];
            systemPrompt = EvalContractOptions.ComposeSystemPrompt();
            userPrompt = string.Format(scenario.PromptTemplate, Path.Combine(environment.SolutionDirectory, "ContosoOrders.Core"));
        }

        // Faulted resume: real calls on the fresh fixture. Unseeded, the ONE call is not added to seed, so the model never sees it (stale workspace).
        if (scenario.ResumeFaultTool is not null)
        {
            if (scenario.ResumeFaultSeeded)
            {
                // Seeded: the failed result is the point, so IsError is expected here. The authored call(s) are appended as the
                // scenario's first error. Scoring stays at ResumeKeepTurns on purpose (ScenarioSampleLoop passes only that as
                // seededTurns), so the authored failed call counts as the scenario's first error. For an authored-only seed the
                // authored turn is turn 1 (NextSeedTurnNumber on an empty seed). With ResumeFaultExtraCalls the single authored turn
                // holds several calls, executed in order on the fixture.
                var calls = await ApplyResumeFaultCallsAsync(environment.Client, scenario, directory, cancellationToken);
                seed.Add(TranscriptResume.BuildAuthoredFaultTurn(NextSeedTurnNumber(seed), calls));
                TestContext.Out.WriteLine($"Sample {sampleIndex}: authored fault call(s) seeded ({calls.Count} call(s) in one turn; first {scenario.ResumeFaultTool}, isError={calls[0].IsError}).");
            }
            else
            {
                var (faultIsError, faultText, _) = await ApplyResumeFaultAsync(environment.Client, scenario, directory, cancellationToken);
                if (faultIsError)
                {
                    var excerpt = faultText.Length > 200 ? faultText[..200] : faultText;
                    return (null, null, schemaDrift, resultDrift, "FaultCallFailed: " + excerpt);
                }
                else
                {
                    TestContext.Out.WriteLine($"Sample {sampleIndex}: resume fault applied ({scenario.ResumeFaultTool}); not shown to the model.");
                }
            }
        }

        // turnCap counts the seeded turns (the authored fault turn included), so the live loop gets ContinueTurns more on top of them.
        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: scenario.ResumeTurnCap,
            wallClockCap: TimeSpan.FromMinutes(10),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        // Early exit: scenarios flagged EndOnCorrectFinalState stop the live loop once CheckFinalState is Correct (null = run to finish or cap).
        var result = await runner.RunAsync(systemPrompt, userPrompt, sampleDirectory, cancellationToken, seedTurns: seed, targetReached: scenario.BuildTargetReached(directory));

        var verdict = await ComputeFinalStateAsync(scenario, sampleIndex, environment.SolutionDirectory, sampleDirectory, cancellationToken);
        return (result, verdict, schemaDrift, resultDrift, "");
    }

    // Runs the ONE resume fault call on the fresh fixture; its result is never added to the seeded turns.
    internal static async Task<(bool IsError, string ResultText, string ArgumentsJson)> ApplyResumeFaultAsync(
        McpClient client,
        ScenarioDefinition scenario,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var arguments = scenario.ResumeFaultArguments!.Replace("{SOLUTION}", solutionDirectory.Replace('\\', '/'));
        var (resultText, isError) = await TranscriptReplayHelper.ReplayToolCallAsync(client, scenario.ResumeFaultTool!, arguments, cancellationToken);
        return (isError, resultText, arguments);
    }

    // Runs the primary resume fault call, then each ResumeFaultExtraCalls entry in order, on the fresh fixture. Returns one entry per
    // executed call, each with its REAL result text and IsError. Null or empty extras = only the primary call. The caller uses this for
    // seeded faults only; extras are never run for an unseeded fault.
    internal static async Task<IReadOnlyList<(string ToolName, string ArgumentsJson, string ResultText, bool IsError)>> ApplyResumeFaultCallsAsync(
        McpClient client,
        ScenarioDefinition scenario,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var executed = new List<(string ToolName, string ArgumentsJson, string ResultText, bool IsError)>();
        var (primaryIsError, primaryText, primaryArguments) = await ApplyResumeFaultAsync(client, scenario, solutionDirectory, cancellationToken);
        executed.Add((scenario.ResumeFaultTool!, primaryArguments, primaryText, primaryIsError));

        if (scenario.ResumeFaultExtraCalls is not null)
        {
            foreach (var (tool, rawArguments) in scenario.ResumeFaultExtraCalls)
            {
                var arguments = rawArguments.Replace("{SOLUTION}", solutionDirectory.Replace('\\', '/'));
                var (resultText, isError) = await TranscriptReplayHelper.ReplayToolCallAsync(client, tool, arguments, cancellationToken);
                executed.Add((tool, arguments, resultText, isError));
            }
        }

        return executed;
    }

    // Computes the final-state verdict and snapshots the files it was computed from. Must run while the environment is alive.
    private static async Task<FinalStateVerdict?> ComputeFinalStateAsync(
        ScenarioDefinition scenario,
        int sampleIndex,
        string solutionDirectory,
        string sampleDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? treeBefore = null)
    {
        // The verdict reads the fixture, so it must be computed before the environment is disposed.
        FinalStateVerdict? verdict = null;
        try
        {
            verdict = await scenario.CheckFinalState(solutionDirectory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestContext.Out.WriteLine($"Sample {sampleIndex}: final-state check failed: {ex.Message}");
        }

        // Whole-tree scenarios (WholeTreeMustStayUnchanged): any added, changed or removed file turns an Unchanged verdict into Destructive.
        // Correct, Wrong and Destructive verdicts are kept. The verdict has no detail field, so the diff goes to the log and tree-diff.txt.
        if (treeBefore is not null)
        {
            try
            {
                var diff = TreeSnapshot.Diff(treeBefore, TreeSnapshot.Capture(solutionDirectory));
                if (diff.Count > 0)
                {
                    if (verdict == FinalStateVerdict.Unchanged)
                    {
                        verdict = FinalStateVerdict.Destructive;
                    }

                    TestContext.Out.WriteLine($"Sample {sampleIndex}: tree changed ({diff.Count} path(s)): {string.Join("; ", diff)}");
                    Directory.CreateDirectory(sampleDirectory);
                    File.WriteAllLines(Path.Combine(sampleDirectory, "tree-diff.txt"), diff);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TestContext.Out.WriteLine($"Sample {sampleIndex}: tree snapshot comparison failed: {ex.Message}");
            }
        }

        // The fixture directory is deleted when the environment is disposed, so the snapshot is the only record of the files the verdict was computed from.
        try
        {
            SnapshotFinalState(scenario, solutionDirectory, sampleDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestContext.Out.WriteLine($"Sample {sampleIndex}: final-state snapshot failed: {ex.Message}");
        }

        return verdict;
    }

    // Snapshots the final state for the verdict: the staged files plus every .cs directly in
    // ContosoOrders.Core/FixtureHelpers, because a rename can move or add files. Staged-key copies
    // are written first and win; a helper file whose name is already present is skipped.
    private static void SnapshotFinalState(ScenarioDefinition scenario, string solutionDirectory, string sampleDirectory)
    {
        var finalStateDirectory = Path.Combine(sampleDirectory, "final-state");
        var keys = scenario.StagedFiles.Keys.ToList();
        var duplicateNames = keys
            .GroupBy(k => Path.GetFileName(k))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var key in keys)
        {
            var source = Path.Combine(solutionDirectory, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                TestContext.Out.WriteLine($"final-state: {key} is missing");
                continue;
            }

            Directory.CreateDirectory(finalStateDirectory);
            var fileName = Path.GetFileName(key);
            var segments = key.Split('/');
            if (duplicateNames.Contains(fileName) && segments.Length > 1)
            {
                fileName = string.Join("_", segments[..^1]) + "_" + fileName;
            }

            File.Copy(source, Path.Combine(finalStateDirectory, fileName), overwrite: true);
        }

        var helperDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Core", "FixtureHelpers");
        if (!Directory.Exists(helperDirectory))
        {
            TestContext.Out.WriteLine("final-state: FixtureHelpers folder is missing");
            return;
        }

        foreach (var helperFile in Directory.GetFiles(helperDirectory, "*.cs"))
        {
            Directory.CreateDirectory(finalStateDirectory);
            var targetPath = Path.Combine(finalStateDirectory, Path.GetFileName(helperFile));
            if (File.Exists(targetPath))
            {
                continue;
            }

            File.Copy(helperFile, targetPath);
        }
    }

    /// <summary>
    /// Builds one sample's isolated world (a <see cref="ModelEvalMcpEnvironment"/>) with the scenario's staged files. Kept as a
    /// nested static factory so the other ModelEval tests that call <c>ScenarioAgentTests.ScenarioEnvironment.CreateAsync</c>
    /// are unchanged.
    /// </summary>
    internal static class ScenarioEnvironment
    {
        public static Task<ModelEvalMcpEnvironment> CreateAsync(
            ScenarioDefinition scenario,
            string sampleDirectory,
            CancellationToken cancellationToken) =>
            ModelEvalMcpEnvironment.CreateAsync(
                sampleDirectory,
                restorePackages: scenario.RestorePackages,
                beforeLoad: null,
                afterLoad: (fixture, workspaceManager, token) => StageScenarioFilesAsync(scenario, fixture, workspaceManager, token),
                cancellationToken,
                fullToolSchema: scenario.OfferFullToolSchema);

        // The solution reloads once, on the last staged file (no reload when the scenario stages nothing).
        private static async Task StageScenarioFilesAsync(
            ScenarioDefinition scenario,
            RoslynSentinel.Tests.TestSolutionFixture fixture,
            IWorkspaceManager workspaceManager,
            CancellationToken cancellationToken)
        {
            var stagedFiles = scenario.StagedFiles.ToList();
            for (var i = 0; i < stagedFiles.Count; i++)
            {
                var entry = stagedFiles[i];
                await fixture.AddFileToSolution(
                    workspaceManager,
                    entry.Key.Replace('/', Path.DirectorySeparatorChar),
                    entry.Value,
                    reloadSolution: i == stagedFiles.Count - 1,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
