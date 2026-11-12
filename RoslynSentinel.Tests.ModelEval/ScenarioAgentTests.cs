using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tests.ModelEval.Fixtures;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Runs one targeted scenario (ROSLYNSENTINEL_MODELEVAL_SCENARIO, looked up in <see cref="ScenarioRegistry"/>) against the
/// loaded model, ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES times (default 10). Each sample gets its own fixture, host and
/// MCP client with the scenario's staged files, is scored by <see cref="ScenarioSampleLoop"/> and appends one results.csv line.
/// Resume scenarios (ResumeTranscript set) replay their named capture's kept turns on a fresh fixture first and are gated
/// on schema and replay drift; cold-start scenarios run from the staged files only.
/// Measurement only: it asserts that at least one sample ran, not what the model did.
/// </summary>
// TODO ResumeEnvironment in TranscriptResumeAgentTests duplicates this wiring; extract one shared environment once both settle.
[TestFixture]
public class ScenarioAgentTests
{
    public const string ScenarioVariable = "ROSLYNSENTINEL_MODELEVAL_SCENARIO";
    public const string SamplesVariable = "ROSLYNSENTINEL_MODELEVAL_SCENARIO_SAMPLES";

    private const int DefaultSamples = 10;

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1 -Test Scenario -Scenario <id>; resume scenarios use the capture they name.")]
    public async Task RunScenario_SamplesAndScores()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        var scenarioId = Environment.GetEnvironmentVariable(ScenarioVariable);
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
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");
        var liftRenames = ScenarioSampleLoop.LiftRenamesState();

        TestContext.Out.WriteLine(
            $"Scenario {scenario.Id} (type {scenario.ScenarioType}): {samples} sample(s), lift renames {liftRenames}.");

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
        if (scenario.ResumeTranscript is not null)
        {
            return await RunResumeSampleAsync(scenario, sampleIndex, sampleDirectory, cancellationToken);
        }

        await using var environment = await ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: scenario.TurnCap,
            wallClockCap: TimeSpan.FromMinutes(10),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(
            scenario.PromptTemplate,
            Path.Combine(environment.SolutionDirectory, "ContosoOrders.Core"));
        var result = await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, sampleDirectory, cancellationToken);

        var verdict = await ComputeFinalStateAsync(scenario, sampleIndex, environment.SolutionDirectory, sampleDirectory, cancellationToken);
        return (result, verdict, false, 0, "");
    }

    // Resume sample: replays the kept turns of the named capture on a fresh fixture, gates on schema and replay drift, then lets the model continue.
    private static async Task<(AgentRunResult? Result, FinalStateVerdict? Verdict, bool SchemaDrift, int ResultDrift, string InconclusiveReason)> RunResumeSampleAsync(
        ScenarioDefinition scenario,
        int sampleIndex,
        string sampleDirectory,
        CancellationToken cancellationToken)
    {
        var source = TranscriptResume.Load(ResumeScenarios.CapturePath(scenario.ResumeTranscript!));
        var kept = TranscriptResume.KeepTurns(source, scenario.ResumeKeepTurns);

        await using var environment = await ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

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

        var (schemaReason, schemaDrift) = ResumeScenarioGate.CheckSchemas(source, kept, liveSchemas, liveDescriptions);
        if (schemaReason.Length > 0)
        {
            TestContext.Out.WriteLine($"Sample {sampleIndex}: {schemaReason}");
            return (null, null, schemaDrift, 0, schemaReason);
        }

        var directory = environment.SolutionDirectory;
        var seed = TranscriptResume.RewriteTurns(kept, directory).ToList();
        var systemPrompt = TranscriptResume.RewriteFixtureDirectory(source.SystemPrompt, directory)!;
        var userPrompt = TranscriptResume.RewriteFixtureDirectory(source.UserPrompt, directory)!;

        // Same fixture state as at the resume point: the kept calls run again, in order, on the fresh fixture.
        var replayed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
            seed,
            (tool, arguments, token) => TranscriptReplayHelper.ReplayToolCallAsync(environment.Client, tool, arguments, token),
            cancellationToken,
            TestContext.Out.WriteLine);
        var (replayReason, resultDrift) = ResumeScenarioGate.CheckReplay(replayed);
        if (replayReason.Length > 0)
        {
            TestContext.Out.WriteLine($"Sample {sampleIndex}: {replayReason}");
            return (null, null, schemaDrift, resultDrift, replayReason);
        }

        // Faulted resume: ONE real call on the fresh fixture. Unseeded, it is not added to seed, so the model never sees it (stale workspace).
        if (scenario.ResumeFaultTool is not null)
        {
            var (faultIsError, faultText, faultArguments) = await ApplyResumeFaultAsync(environment.Client, scenario, directory, cancellationToken);
            if (scenario.ResumeFaultSeeded)
            {
                // Seeded: the failed result is the point, so IsError is expected here. The authored call is appended as the
                // scenario's first error. Scoring stays at ResumeKeepTurns on purpose (ScenarioSampleLoop passes only that as
                // seededTurns), so the authored failed call counts as the scenario's first error.
                seed.Add(TranscriptResume.BuildAuthoredFaultTurn(seed[^1].TurnNumber + 1, scenario.ResumeFaultTool!, faultArguments, faultText, faultIsError));
                TestContext.Out.WriteLine($"Sample {sampleIndex}: authored fault call seeded ({scenario.ResumeFaultTool}, isError={faultIsError}).");
            }
            else if (faultIsError)
            {
                var excerpt = faultText.Length > 200 ? faultText[..200] : faultText;
                return (null, null, schemaDrift, resultDrift, "FaultCallFailed: " + excerpt);
            }
            else
            {
                TestContext.Out.WriteLine($"Sample {sampleIndex}: resume fault applied ({scenario.ResumeFaultTool}); not shown to the model.");
            }
        }

        // turnCap counts the seeded turns (the authored fault turn included), so the live loop gets ContinueTurns more on top of them.
        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: scenario.ResumeKeepTurns + (scenario.ResumeFaultSeeded ? 1 : 0) + scenario.TurnCap,
            wallClockCap: TimeSpan.FromMinutes(10),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var result = await runner.RunAsync(systemPrompt, userPrompt, sampleDirectory, cancellationToken, seedTurns: seed);

        var verdict = await ComputeFinalStateAsync(scenario, sampleIndex, environment.SolutionDirectory, sampleDirectory, cancellationToken);
        return (result, verdict, schemaDrift, resultDrift, "");
    }

    // Runs the ONE resume fault call on the fresh fixture; its result is never added to the seeded turns.
    private static async Task<(bool IsError, string ResultText, string ArgumentsJson)> ApplyResumeFaultAsync(
        McpClient client,
        ScenarioDefinition scenario,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var arguments = scenario.ResumeFaultArguments!.Replace("{SOLUTION}", solutionDirectory.Replace('\\', '/'));
        var (resultText, isError) = await TranscriptReplayHelper.ReplayToolCallAsync(client, scenario.ResumeFaultTool!, arguments, cancellationToken);
        return (isError, resultText, arguments);
    }

    // Computes the final-state verdict and snapshots the files it was computed from. Must run while the environment is alive.
    private static async Task<FinalStateVerdict?> ComputeFinalStateAsync(
        ScenarioDefinition scenario,
        int sampleIndex,
        string solutionDirectory,
        string sampleDirectory,
        CancellationToken cancellationToken)
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

    /// <summary>One sample's isolated world: fixture on disk with the scenario's staged files, in-process MCP server, MCP client, LM client.</summary>
    internal sealed class ScenarioEnvironment : IAsyncDisposable
    {
        private IHost? _host;
        private McpClient? _client;
        private RoslynSentinel.Tests.TestSolutionFixture? _fixture;

        public IHost Host => _host!;
        public McpClient Client => _client!;
        public LmStudioAgentClient AgentClient { get; private set; } = null!;
        public string SolutionDirectory => _fixture!.SolutionDirectory;

        public static async Task<ScenarioEnvironment> CreateAsync(
            ScenarioDefinition scenario,
            string sampleDirectory,
            CancellationToken cancellationToken)
        {
            var environment = new ScenarioEnvironment();
            try
            {
                await environment.InitializeAsync(scenario, sampleDirectory, cancellationToken);
                return environment;
            }
            catch
            {
                await environment.DisposeAsync();
                throw;
            }
        }

        private async Task InitializeAsync(ScenarioDefinition scenario, string sampleDirectory, CancellationToken cancellationToken)
        {
            _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            // Same wiring as TranscriptResumeAgentTests.ResumeEnvironment (default ReplaceSnippet arm).
            var services = new ServiceCollection();
            services.AddRoslynSentinelEnginesAdvanced();
            var mcpBuilder = services.AddMcpServer();
            mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
            mcpBuilder.WithTasks(
                new InMemoryMcpTaskStore(),
                o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
            mcpBuilder.AddRoslynSentinelToolsAdvanced(
                services, ChainEvalToolSurface.NewModes(),
                hideTools: ChainEvalToolSurface.NewHiddenTools());

            var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            hostBuilder.Services.AddLmStudioAgentClient(client =>
            {
                client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
            });
            foreach (var descriptor in services)
            {
                hostBuilder.Services.Add(descriptor);
            }

            hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(sampleDirectory, "agent.log")));

            _host = hostBuilder.Build();
            _ = _host.RunAsync(CancellationToken.None); // stopped by DisposeAsync, not by the test's token

            var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
            await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, cancellationToken);

            var stagedFiles = scenario.StagedFiles.ToList();
            for (var i = 0; i < stagedFiles.Count; i++)
            {
                var entry = stagedFiles[i];
                await _fixture.AddFileToSolution(
                    workspaceManager,
                    entry.Key.Replace('/', Path.DirectorySeparatorChar),
                    entry.Value,
                    reloadSolution: i == stagedFiles.Count - 1,
                    cancellationToken: cancellationToken);
            }

            var clientTransport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);
            _client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);
            AgentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            _fixture?.Dispose();
        }
    }
}
