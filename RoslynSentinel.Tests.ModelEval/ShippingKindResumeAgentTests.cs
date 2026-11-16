using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.Fixtures;
using RoslynSentinel.Tools.Advanced;

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
        await using var environment = await ResumeEnvironment.CreateAsync(step, sampleDirectory, cancellationToken);

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

    // TODO extract the shared ModelEval host setup (fourth copy: ShippingKindSteppedAgentTests, TranscriptResumeAgentTests, ...)
    /// <summary>One sample's isolated world: the step's golden tree on disk, in-process MCP server with the chain tool surface, MCP client, LM client.</summary>
    private sealed class ResumeEnvironment : IAsyncDisposable
    {
        private IHost? _host;
        private McpClient? _client;
        private RoslynSentinel.Tests.TestSolutionFixture? _fixture;

        public IHost Host => _host!;
        public McpClient Client => _client!;
        public LmStudioAgentClient AgentClient { get; private set; } = null!;
        public string SolutionDirectory => _fixture!.SolutionDirectory;

        public static async Task<ResumeEnvironment> CreateAsync(int step, string sampleDirectory, CancellationToken cancellationToken)
        {
            var environment = new ResumeEnvironment();
            try
            {
                await environment.InitializeAsync(step, sampleDirectory, cancellationToken);
                return environment;
            }
            catch
            {
                await environment.DisposeAsync();
                throw;
            }
        }

        private async Task InitializeAsync(int step, string sampleDirectory, CancellationToken cancellationToken)
        {
            // restorePackages: ContosoOrders.Tests needs restore assets or a solution-scope build reports CS0246 for every Xunit file
            _fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages: true);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            // Same wiring as ShippingKindSteppedAgentTests.SetUp.
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

            // The checkout happens before LoadSolutionAsync, so the server never sees a harness write.
            var repository = GoldenStepRepository.Create(
                _fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, s => ShippingKindStepGoldens.FilesForStep(s, false));
            repository.CheckoutStep(step - 1, $"run/step-{step}");

            var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
            await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, cancellationToken);

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
