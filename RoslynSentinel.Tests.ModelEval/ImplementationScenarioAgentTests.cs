using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.Fixtures;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Data-driven implementation scenarios: one test case per scenario step. Each case checks out the previous step's golden
/// commit (tag step-(k-1)) into the sample solution, loads the solution, gives the model that step's prompt and grades the
/// resulting tree with <see cref="ImplementationScenarioChecks"/>. Runner settings and outcome mapping follow
/// ShippingKindSteppedAgentTests.RunStepAsync. See docs/current/plans/closed/plan_modeleval_implementation_scenarios.md.
/// </summary>
[TestFixture]
public class ImplementationScenarioAgentTests
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
            Assert.Ignore("git is not available - implementation scenarios keep their golden steps in a git repository.");
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

    [TestCaseSource(nameof(StepCases))]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_CompletesImplementationScenario(string scenarioId, int step)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        await RunStepAsync(scenario, step);
    }

    // SetName replaces the method name in VSTest names, so the script filter is Name~ImplScenario_<Id> (plan Step 9).
    // A parked scenario yields one Ignored case carrying its reason.
    private static IEnumerable<TestCaseData> StepCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All)
        {
            if (scenario.ParkedReason is not null)
            {
                yield return new TestCaseData(scenario.Id, 0)
                    .SetName($"ImplScenario_{scenario.Id}_Parked")
                    .Ignore(scenario.ParkedReason);
                continue;
            }

            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                var testCase = new TestCaseData(scenario.Id, step).SetName($"ImplScenario_{scenario.Id}_Step{step}");

                // The category is generated from the property, never written by hand, so it cannot drift from the declared axis.
                testCase.SetCategory(EvalAxisCategory.Name(scenario.PrimaryAxis));

                yield return testCase;
            }
        }
    }

    private async Task RunStepAsync(ImplementationScenario scenario, int step)
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var prompt = scenario.Steps[step - 1].Prompt;
        Directory.CreateDirectory(_runDirectory);
        File.WriteAllText(Path.Combine(_runDirectory, "prompt.txt"), prompt);
        File.WriteAllText(Path.Combine(_runDirectory, "scenario.txt"), $"{scenario.Id}\r\n");

        // The checkout happens before LoadSolutionAsync, so the server never sees a harness write.
        GoldenStepRepository? repository = null;
        _environment = await ModelEvalMcpEnvironment.CreateAsync(
            _runDirectory,
            restorePackages: true,
            beforeLoad: (fixture, workspaceManager, ct) =>
            {
                repository = GoldenStepRepository.Create(fixture.SolutionDirectory, scenario.Steps.Count, scenario.FilesForStep);
                repository.CheckoutStep(step - 1, $"run/step-{step}");
                return Task.CompletedTask;
            },
            afterLoad: null,
            cancellationToken,
            // The edit arm (ROSLYNSENTINEL_MODELEVAL_EDIT_ARM, see EditArmToolSurface); unset keeps the default surface.
            configureToolSurface: EditArmToolSurface.RegisterAdvanced,
            fullToolSchema: scenario.OfferFullToolSchema);

        // ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES (modeleval -StepWallClockMinutes) raises the 15-minute cap for slow models;
        // the cap is printed in step-<k>.outcome.txt.
        var wallClockCap = TimeSpan.FromMinutes(
            double.TryParse(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_STEP_WALL_CLOCK_MINUTES"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var capMinutes) && capMinutes > 0
                ? capMinutes
                : 15);
        var runner = new ModelAgentRunner(
            _environment.AgentClient, _environment.Client, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: scenario.TurnCap, wallClockCap: wallClockCap,
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

        // Only a run the model ended itself has a final message. FinalMessage() returns the last turn's text even when that turn
        // was a tool call cut off by a cap, and a mid-task "I need to report what breaks: ChangePublisher ..." then satisfied a
        // report check (WideningRejectedByGate on 85 graded PassedAfterCap with no report and an unchanged tree).
        var finalMessage = result.Transcript.FinalMessageIfFinished();
        var verdict = await ImplementationScenarioChecks.VerdictAsync(
            scenario, step, _environment.SolutionDirectory, finalMessage, changedPaths, cancellationToken);
        var checkOutcome = verdict.Outcome;
        var checkDetail = verdict.Detail;

        var outcome = checkOutcome;
        var detail = checkDetail;
        if (!result.Converged)
        {
            // Separated from NotConverged so a run whose work already passed the check is not labelled as an unfinished task.
            if (checkOutcome == ImplementationScenarioOutcome.Pass
                && result.StopReason is AgentStopReason.WallClockCapExceeded or AgentStopReason.TurnCapExceeded)
            {
                outcome = ImplementationScenarioOutcome.PassedAfterCap;
                detail = $"Stopped: {result.StopReason} after the work already passed the step check: {checkDetail}";
            }
            else
            {
                outcome = ImplementationScenarioOutcome.NotConverged;
                detail = $"Stopped: {result.StopReason}. State check would have been {checkOutcome}: {checkDetail}";
            }
        }

        // outcomeClass= and variant= are appended after the free-text detail so a parser of the leading key=value fields is unaffected.
        var summary =
            $"{scenario.Id} step {step}: outcome={outcome}, turns={result.TurnCount}, started={result.StartedTurnCount}, wallTime={stopwatch.Elapsed:hh\\:mm\\:ss}, wallClockCap={wallClockCap.TotalMinutes}m, stopReason={result.StopReason}. {detail} outcomeClass={verdict.Class} variant={verdict.Variant}";
        TestContext.Out.WriteLine(summary);
        File.WriteAllText(Path.Combine(_runDirectory, $"step-{step}.outcome.txt"), summary + "\r\n");

        // A step that expects BLOCKED passes on the class CorrectBlocked (it needs the token, an unchanged tree and a passing grade);
        // every other step passes on outcome Pass.
        if (scenario.Steps[step - 1].ExpectedEnding == ExpectedEnding.Blocked)
        {
            Assert.That(verdict.Class, Is.EqualTo(EvalOutcomeClass.CorrectBlocked), $"{detail} Transcript: {result.TranscriptPath}");
        }
        else
        {
            Assert.That(outcome, Is.EqualTo(ImplementationScenarioOutcome.Pass), $"{detail} Transcript: {result.TranscriptPath}");
        }
    }
}
