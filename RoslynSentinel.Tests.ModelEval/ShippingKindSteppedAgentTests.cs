using System.Diagnostics;
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
/// Stepped ShippingKind scenario: one test case per step (1-5). Each case checks out the previous
/// step's golden commit (tag step-(k-1)) into the sample solution, loads the solution, gives the model
/// that step's prompt and grades the resulting tree with <see cref="ShippingKindStepChecks"/>. A step
/// never starts from the model's own earlier output. See
/// docs/current/plans/plan_modeleval_stepped_shipping_kind_scenario.md.
/// </summary>
[TestFixture]
public class ShippingKindSteppedAgentTests
{
    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        // TODO extract the shared ModelEval host setup (third copy)
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

        // restorePackages: ContosoOrders.Tests needs restore assets or a solution-scope build reports CS0246 for every Xunit file
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages: true);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ChainEvalToolSurface.NewModes(), hideTools: ChainEvalToolSurface.NewHiddenTools());

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(_runDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        _agentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_CompletesShippingKindStep(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStep(step));
    }

    private async Task RunStepAsync(int step, string prompt)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        Directory.CreateDirectory(_runDirectory);

        // The checkout happens before LoadSolutionAsync, so the server never sees a harness write.
        var repository = GoldenStepRepository.Create(
            _fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, ShippingKindStepGoldens.FilesForStep);
        repository.CheckoutStep(step - 1, $"run/step-{step}");

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, cancellationToken);

        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 25, wallClockCap: TimeSpan.FromMinutes(15),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent),
            prompt,
            _runDirectory,
            cancellationToken);
        stopwatch.Stop();

        // All three happen before the check writes its test file, so the golden diff and the changed paths never contain it.
        var changedPaths = repository.ChangedPathsSinceStep(step - 1);
        repository.CommitWorkingTree($"model result for step {step}");
        File.WriteAllText(Path.Combine(_runDirectory, $"step-{step}.golden.diff"), repository.DiffAgainstStep(step));

        var finalMessage = result.Transcript.FinalMessage();
        var (checkOutcome, checkDetail) = await ShippingKindStepChecks.CheckAsync(
            step, _fixture.SolutionDirectory, finalMessage, changedPaths, cancellationToken);

        var outcome = checkOutcome;
        var detail = checkDetail;
        if (!result.Converged)
        {
            outcome = ShippingKindStepOutcome.NotConverged;
            detail = $"Stopped: {result.StopReason}. State check would have been {checkOutcome}: {checkDetail}";
        }
        else if (checkOutcome != ShippingKindStepOutcome.Pass
            && (StopMarker.IsLeading(finalMessage) || StopMarker.ContainsAnywhere(finalMessage)))
        {
            outcome = ShippingKindStepOutcome.Blocked;
            detail = $"Blocked-stop final message. State check was {checkOutcome}: {checkDetail}";
        }

        var summary =
            $"Step {step}: outcome={outcome}, turns={result.TurnCount}, wallTime={stopwatch.Elapsed:hh\\:mm\\:ss}, stopReason={result.StopReason}. {detail}";
        TestContext.Out.WriteLine(summary);
        File.WriteAllText(Path.Combine(_runDirectory, $"step-{step}.outcome.txt"), summary + "\r\n");

        Assert.That(outcome, Is.EqualTo(ShippingKindStepOutcome.Pass), $"{detail} Transcript: {result.TranscriptPath}");
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV2(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV2(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV3(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV3(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV5(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV5(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV6(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV6(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV4(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV4(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV7(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV7(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV8(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV8(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV9(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV9(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV10(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV10(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV11(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV11(step));
    }
}
