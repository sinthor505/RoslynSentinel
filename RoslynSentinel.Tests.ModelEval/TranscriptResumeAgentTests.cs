using System.IO.Pipelines;

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
/// Measures the loop-assist feature at the exact moment a loop starts, without waiting for a 1-in-10 loop in a full
/// batch: takes a saved transcript (ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT), keeps its turns 1..K
/// (_RESUME_KEEP_TURNS), rebuilds the fixture state at that point by re-executing the kept tool calls on a fresh
/// ContosoOrders fixture, seeds the conversation with those turns, and lets the live model generate
/// _RESUME_CONTINUE_TURNS more turns, _RESUME_SAMPLES times. Loop assist follows ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST
/// and _ROLE as in every other run. It asserts nothing about what the model does: it is a measurement, one results.csv
/// row per sample (<see cref="ResumeSampleRow"/>), plus a per-sample transcript.json and agent.log under
/// sample-NN/. Each sample gets its own fixture, host and MCP client, because a sample that succeeds in editing
/// would otherwise hand the next one an already-refactored file.
///
/// Fixture and tool surface are those of <see cref="OrderPricingRefactorChainAgentTests.Model_AppliesTenChainedRefactors"/>
/// (Advanced engines, modes Refactor/Workspace, Features and McpServerStatus hidden, the acceptance-test file added),
/// so the transcript must come from that test. The transcript's own SystemPrompt and UserPrompt are used verbatim
/// except that its fixture directory (a fresh GUID folder per run) is pointed at the new fixture.
/// </summary>
[TestFixture]
public class TranscriptResumeAgentTests
{
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace"
    };

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1 -Test ResumeTranscript")]
    public async Task ResumeFromTranscript_SamplesTheNextTurns()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        var settings = ResumeSettings.FromEnvironment();
        if (settings.TranscriptPath is null)
        {
            Assert.Ignore($"{ResumeSettings.TranscriptVariable} is not set - point it at a saved transcript.json (or its run folder) to resume from.");
        }

        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var source = TranscriptResume.Load(TranscriptResume.ResolveTranscriptPath(settings.TranscriptPath!));
        var keptTurns = TranscriptResume.KeepTurns(source, settings.KeepTurns);
        var loopAssist = LoopAssistOptions.FromEnvironment();

        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");

        TestContext.Out.WriteLine(
            $"Resuming '{settings.TranscriptPath}' after turn {settings.KeepTurns} of {source.Turns.Count}: " +
            $"{settings.Samples} sample(s) x {settings.ContinueTurns} live turn(s), loop assist " +
            $"{(loopAssist.Enabled ? "on (role " + loopAssist.InjectionRole + ")" : "off")}.");

        var rows = new List<ResumeSampleRow>();
        try
        {
            for (var sampleIndex = 1; sampleIndex <= settings.Samples; sampleIndex++)
            {
                ResumeSampleRow row;
                try
                {
                    row = await RunSampleAsync(
                        sampleIndex, settings, source, keptTurns, loopAssist,
                        Path.Combine(runDirectory, $"sample-{sampleIndex:D2}"), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row = TranscriptResume.BuildHarnessErrorRow(sampleIndex, loopAssist, ex.Message);
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
        ResumeSettings settings,
        ResumeSource source,
        IReadOnlyList<AgentTranscriptTurn> keptTurns,
        LoopAssistOptions loopAssist,
        string sampleDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sampleDirectory);
        await using var environment = await ResumeEnvironment.CreateAsync(sampleDirectory, cancellationToken);

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

        if (source.Tools.Count > 0)
        {
            var offered = (await environment.Client.ListToolsAsync(cancellationToken: cancellationToken)).Select(t => t.Name).ToList();
            if (!source.Tools.SequenceEqual(offered))
            {
                TestContext.Out.WriteLine(
                    $"Sample {sampleIndex}: the tool surface differs from the transcript's (only in transcript: " +
                    $"{string.Join(", ", source.Tools.Except(offered))}; only now: {string.Join(", ", offered.Except(source.Tools))}).");
            }
        }

        // turnCap counts the seeded turns, so the live loop gets exactly ContinueTurns more.
        var runner = new ModelAgentRunner(
            environment.AgentClient, environment.Client,
            repeatedFailureLimit: 10,
            maxTokensPerTurn: 16384,
            turnCap: settings.KeepTurns + settings.ContinueTurns,
            wallClockCap: TimeSpan.FromMinutes(10),
            logger: environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>(),
            loopAssist: loopAssist);
        var result = await runner.RunAsync(
            systemPrompt, userPrompt, sampleDirectory, cancellationToken, seedTurns: seed);

        return TranscriptResume.BuildRow(
            sampleIndex, loopAssist, settings.KeepTurns, settings.ContinueTurns,
            result.Transcript.Turns, result.Transcript.AssistCount ?? 0, result.StopReason.ToString());
    }

    /// <summary>One sample's isolated world: fixture on disk, in-process MCP server with the ten-chain tool surface, MCP client, LM client.</summary>
    private sealed class ResumeEnvironment : IAsyncDisposable
    {
        private IHost? _host;
        private McpClient? _client;
        private RoslynSentinel.Tests.TestSolutionFixture? _fixture;

        public IHost Host => _host!;
        public McpClient Client => _client!;
        public LmStudioAgentClient AgentClient { get; private set; } = null!;
        public string SolutionDirectory => _fixture!.SolutionDirectory;

        public static async Task<ResumeEnvironment> CreateAsync(string sampleDirectory, CancellationToken cancellationToken)
        {
            var environment = new ResumeEnvironment();
            try
            {
                await environment.InitializeAsync(sampleDirectory, cancellationToken);
                return environment;
            }
            catch
            {
                await environment.DisposeAsync();
                throw;
            }
        }

        private async Task InitializeAsync(string sampleDirectory, CancellationToken cancellationToken)
        {
            _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            // Same wiring as OrderPricingRefactorChainAgentTests.SetUp for Model_AppliesTenChainedRefactors
            // (default ReplaceSnippet arm).
            var services = new ServiceCollection();
            services.AddRoslynSentinelEnginesAdvanced();
            var mcpBuilder = services.AddMcpServer();
            mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
            mcpBuilder.WithTasks(
                new InMemoryMcpTaskStore(),
                o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
            mcpBuilder.AddRoslynSentinelToolsAdvanced(
                services, ActiveModes,
                hideTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Features", "McpServerStatus" });

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
            await _fixture.AddFileToSolution(
                workspaceManager,
                Path.Combine("ContosoOrders.Core", "FixtureHelpers", "OrderPricingCalculator.cs"),
                OrderPricingRefactorReproducer.StartingCalculatorFileContent,
                reloadSolution: false,
                cancellationToken: cancellationToken);
            await _fixture.AddFileToSolution(
                workspaceManager,
                Path.Combine("ContosoOrders.Core", "FixtureHelpers", "OrderCheckout.cs"),
                OrderPricingRefactorReproducer.CheckoutCallerFileContent,
                reloadSolution: false,
                cancellationToken: cancellationToken);
            await _fixture.AddFileToSolution(
                workspaceManager,
                Path.Combine("ContosoOrders.Tests", "ModelEvalGenerated", "RefactorAcceptanceTests.cs"),
                OrderPricingRefactorChainAgentTests.RefactorAcceptanceTestsFileContent,
                reloadSolution: false,
                cancellationToken: cancellationToken);
            await _fixture.AddFileToSolution(
                workspaceManager,
                Path.Combine("ContosoOrders.Tests", "ModelEvalGenerated", "OrderCheckoutTests.cs"),
                OrderPricingRefactorReproducer.CheckoutFrontDoorTestsFileContent,
                reloadSolution: true,
                cancellationToken: cancellationToken);

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
