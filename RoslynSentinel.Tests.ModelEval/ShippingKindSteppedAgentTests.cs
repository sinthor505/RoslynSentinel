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
    private ModelEvalMcpEnvironment? _environment;
    private string _runDirectory = null!;

    [SetUp]
    public void SetUp()
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

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
        }

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
    [TestCase(6)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_CompletesShippingKindStep(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStep(step));
    }

    // withVisibleTests (V12/V13) seeds a visible shipping test file in the step-0 golden; every other variant keeps the original fixture so earlier results stay comparable.
    // style UsingStatic seeds `using static ...ShippingKinds;` into the four target files from the step 3 golden on (see ShippingKindStepGoldens.FilesForStep);
    // style PriorityOvernight renames the constant Overnight to PriorityOvernight in goldens and check sources (prompts: PromptForStepPriorityOvernight).
    private async Task RunStepAsync(int step, string prompt, bool withVisibleTests = false, ShippingKindFixtureStyle style = ShippingKindFixtureStyle.Default)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        Directory.CreateDirectory(_runDirectory);

        // The checkout happens before LoadSolutionAsync, so the server never sees a harness write.
        GoldenStepRepository? repository = null;
        _environment = await ModelEvalMcpEnvironment.CreateAsync(
            _runDirectory,
            restorePackages: true,
            beforeLoad: (fixture, workspaceManager, ct) =>
            {
                repository = GoldenStepRepository.Create(
                    fixture.SolutionDirectory, ShippingKindStepGoldens.LastStep, s => ShippingKindStepGoldens.FilesForStep(s, withVisibleTests, style));
                repository.CheckoutStep(step - 1, $"run/step-{step}");
                return Task.CompletedTask;
            },
            afterLoad: null,
            cancellationToken);

        // ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES (modeleval -StepWallClockMinutes) raises the 15-minute cap for slow models;
        // a raised cap passes slow-but-correct runs that 15 minutes would fail, so the cap is printed in step-<k>.outcome.txt.
        var wallClockCap = TimeSpan.FromMinutes(
            double.TryParse(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var capMinutes) && capMinutes > 0
                ? capMinutes
                : 15);
        var runner = new ModelAgentRunner(
            _environment.AgentClient, _environment.Client, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 25, wallClockCap: wallClockCap,
            logger: _environment.Host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            EvalContractOptions.ComposeSystemPrompt(),
            prompt,
            _runDirectory,
            cancellationToken);
        stopwatch.Stop();

        // All three happen before the check writes its test file, so the golden diff and the changed paths never contain it.
        var changedPaths = repository!.ChangedPathsSinceStep(step - 1);
        repository.CommitWorkingTree($"model result for step {step}");
        File.WriteAllText(Path.Combine(_runDirectory, $"step-{step}.golden.diff"), repository.DiffAgainstStep(step));

        // A capped run's mid-task text is not graded as an answer.
        var finalMessage = result.Transcript.FinalMessageIfFinished();
        var (checkOutcome, checkDetail) = await ShippingKindStepChecks.CheckAsync(
            step, _environment.SolutionDirectory, finalMessage, changedPaths, cancellationToken, style);

        var outcome = checkOutcome;
        var detail = checkDetail;
        if (!result.Converged)
        {
            // Separated from NotConverged so a run whose work already passed the check is not labelled as an unfinished task.
            if (checkOutcome == ShippingKindStepOutcome.Pass
                && result.StopReason is AgentStopReason.WallClockCapExceeded or AgentStopReason.TurnCapExceeded)
            {
                outcome = ShippingKindStepOutcome.PassedAfterCap;
                detail = $"Stopped: {result.StopReason} after the work already passed the step check: {checkDetail}";
            }
            else
            {
                outcome = ShippingKindStepOutcome.NotConverged;
                detail = $"Stopped: {result.StopReason}. State check would have been {checkOutcome}: {checkDetail}";
            }
        }
        else if (checkOutcome != ShippingKindStepOutcome.Pass
            && (StopMarker.IsLeading(finalMessage) || StopMarker.ContainsAnywhere(finalMessage)))
        {
            outcome = ShippingKindStepOutcome.Blocked;
            detail = $"Blocked-stop final message. State check was {checkOutcome}: {checkDetail}";
        }

        var summary =
            $"Step {step}: outcome={outcome}, turns={result.TurnCount}, wallTime={stopwatch.Elapsed:hh\\:mm\\:ss}, wallClockCap={wallClockCap.TotalMinutes}m, stopReason={result.StopReason}. {detail}";
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

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV12(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV12(step), withVisibleTests: true);
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV13(int step)
    {
        // V13 = control: the V7 prompt with the visible tests present but not mentioned.
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV7(step), withVisibleTests: true);
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptV14(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepV14(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    // Final prompt set, all six steps. The name must not contain Model_CompletesShippingKindStep (the V1 filter is Name~ on it).
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptFinal(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepFinal(step));
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    // UsingStatic variant, all six steps: the step 3 golden seeds `using static ContosoOrders.Core.ShippingKinds;` into the four target
    // files, and the steps 4 and 5 prompts ask for bare constant names; steps 1, 2, 3 and 6 reuse the Final prompts. Token-effect test
    // against Model_RunsShippingKindStepPromptFinal: the model never has to write the directive itself.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptUsingStatic(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepUsingStatic(step), style: ShippingKindFixtureStyle.UsingStatic);
    }

    // The name must not contain "Model_CompletesShippingKindStep" because the baseline script filter is Name~ on that string.
    // PriorityOvernight variant, all six steps: the Final prompts with the constant Overnight named PriorityOvernight everywhere (goldens,
    // prompts, check sources; the value stays "overnight"). Tests whether the ".O" misspelling of the member name moves or disappears.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_RunsShippingKindStepPromptPriorityOvernight(int step)
    {
        await RunStepAsync(step, ShippingKindStepGoldens.PromptForStepPriorityOvernight(step), style: ShippingKindFixtureStyle.PriorityOvernight);
    }
}
