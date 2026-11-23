using System.Diagnostics;
using System.Globalization;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model self-test for the implementation scenarios. Per scenario: <c>Golden_&lt;Id&gt;_Step&lt;k&gt;</c> (the step's golden passes its
/// check), <c>Previous_&lt;Id&gt;_Step&lt;k&gt;</c> (the previous state fails it), <c>Budget_&lt;Id&gt;</c> (seed text + longest prompt fits the
/// context budget). Parked scenarios yield one Ignored <c>Golden_&lt;Id&gt;_Parked</c> case. Each scenario owns one fixture and one
/// git repository (cached by id, disposed in OneTimeTearDown), so the class must not run in parallel.
/// Filter: <c>ImplementationScenarioGoldenTests</c>, or <c>Name~&lt;Id&gt;</c> for one scenario.
/// </summary>
[TestFixture]
[NonParallelizable]
[Explicit("No model needed. Run after changing an implementation scenario: ImplementationScenarioGoldenTests")]
public class ImplementationScenarioGoldenTests
{
    private readonly Dictionary<string, (RoslynSentinel.Tests.TestSolutionFixture Fixture, GoldenStepRepository Repository)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _restoreTimeReported;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!GitTestRepo.IsGitAvailable())
        {
            Assert.Ignore("git not available");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var (fixture, _) in _cache.Values)
        {
            fixture.Dispose();
        }

        _cache.Clear();
    }

    [TestCaseSource(nameof(GoldenCases))]
    public async Task Golden_StepPassesItsCheck(string scenarioId, int step)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var (fixture, repository) = GetOrCreate(scenario);
        repository.CheckoutStep(step, $"verify/{scenario.Id}-step-{step}");
        var changedPaths = PathsChangedFromPreviousStep(repository, step);

        var (outcome, detail) = await ImplementationScenarioChecks.CheckAsync(
            scenario,
            step,
            fixture.SolutionDirectory,
            scenario.Steps[step - 1].CannedFinalMessage,
            changedPaths,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(outcome, Is.EqualTo(ImplementationScenarioOutcome.Pass), $"{scenario.Id} step {step}: {detail}");
    }

    [TestCaseSource(nameof(PreviousCases))]
    public async Task Previous_StepFailsTheCheck(string scenarioId, int step)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var (fixture, repository) = GetOrCreate(scenario);
        repository.CheckoutStep(step - 1, $"verify/{scenario.Id}-previous-of-step-{step}");

        var (outcome, detail) = await ImplementationScenarioChecks.CheckAsync(
            scenario,
            step,
            fixture.SolutionDirectory,
            null,
            Array.Empty<string>(),
            TestContext.CurrentContext.CancellationToken);
        TestContext.Out.WriteLine($"{scenario.Id} step {step} check on step-{step - 1} state: {outcome}: {detail}");

        Assert.That(
            outcome,
            Is.Not.EqualTo(ImplementationScenarioOutcome.Pass),
            $"{scenario.Id} step {step} check passed on the step-{step - 1} state: {detail}");
    }

    [TestCaseSource(nameof(BudgetCases))]
    public void Budget_FitsContextWindow(string scenarioId)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var characters = scenario.ContextCharacters();

        Assert.That(
            characters,
            Is.LessThanOrEqualTo(ImplementationScenario.ContextBudgetCharacters),
            $"{scenario.Id}: seed text plus the longest prompt is {characters} characters, budget is {ImplementationScenario.ContextBudgetCharacters}.");
    }

    [Test]
    public void Registry_IdsUniqueAndMatchClassNames()
    {
        var all = ImplementationScenarioRegistry.All;
        Assert.That(all, Has.Count.EqualTo(15));
        Assert.That(all.Select(scenario => scenario.Id), Is.Unique.IgnoreCase);

        foreach (var scenario in all)
        {
            var type = ImplementationScenarioRegistry.FindScenarioClass(scenario.Id);
            Assert.That(type, Is.Not.Null, $"no class <Id>Scenario found for id {scenario.Id}");

            var definition = type!.GetProperty("Definition")?.GetValue(null);
            Assert.That(definition, Is.SameAs(scenario), $"{type.Name}.Definition is not the registered definition for {scenario.Id}");
            Assert.That(ImplementationScenarioRegistry.Find(scenario.Id.ToUpperInvariant()), Is.SameAs(scenario));
        }
    }

    private static IEnumerable<TestCaseData> GoldenCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All)
        {
            if (scenario.ParkedReason is not null)
            {
                // The single Ignored case per parked scenario lives here only, so the report shows one per scenario.
                yield return new TestCaseData(scenario.Id, 0)
                    .SetName($"Golden_{scenario.Id}_Parked")
                    .Ignore(scenario.ParkedReason);
                continue;
            }

            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                yield return new TestCaseData(scenario.Id, step).SetName($"Golden_{scenario.Id}_Step{step}");
            }
        }
    }

    private static IEnumerable<TestCaseData> PreviousCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                yield return new TestCaseData(scenario.Id, step).SetName($"Previous_{scenario.Id}_Step{step}");
            }
        }
    }

    private static IEnumerable<TestCaseData> BudgetCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            yield return new TestCaseData(scenario.Id).SetName($"Budget_{scenario.Id}");
        }
    }

    private (RoslynSentinel.Tests.TestSolutionFixture Fixture, GoldenStepRepository Repository) GetOrCreate(ImplementationScenario scenario)
    {
        if (_cache.TryGetValue(scenario.Id, out var cached))
        {
            return cached;
        }

        var fixture = CreateFixture(scenario);
        var repository = GoldenStepRepository.Create(fixture.SolutionDirectory, scenario.Steps.Count, scenario.FilesForStep);
        _cache[scenario.Id] = (fixture, repository);
        return (fixture, repository);
    }

    // The single place that decides how a scenario gets its solution copy. Today: one fresh copy plus `dotnet restore` per scenario.
    // If the measured restore time (see ReportRestoreTime) is over ~60 s, restore once into a template directory here and copy
    // that restored tree per scenario instead; nothing else in this class needs to change (GetOrCreate only uses SolutionDirectory
    // and Dispose of the returned fixture, so a copy-backed fixture type would have to offer both).
    private RoslynSentinel.Tests.TestSolutionFixture CreateFixture(ImplementationScenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        var fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages: true);
        stopwatch.Stop();
        ReportRestoreTime(scenario, stopwatch.Elapsed);
        return fixture;
    }

    // Reports the copy + restore time of the first fixture of the run: to the test progress output and to a one-line file
    // <scratch root>\ImplementationScenarios\restore-time.txt (overwritten each run; outside the repo).
    private void ReportRestoreTime(ImplementationScenario scenario, TimeSpan elapsed)
    {
        if (_restoreTimeReported)
        {
            return;
        }

        _restoreTimeReported = true;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"fixture copy+restore seconds={elapsed.TotalSeconds:F1} (first fixture, scenario {scenario.Id}, {DateTime.UtcNow:O})");
        TestContext.Progress.WriteLine(line);
        var directory = Path.Combine(RoslynSentinel.Common.ScratchRoot.Resolve(), "ImplementationScenarios");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "restore-time.txt"), line + "\r\n");
    }

    private static IReadOnlyList<string> PathsChangedFromPreviousStep(GoldenStepRepository repository, int step)
    {
        var output = GitTestRepo.RunGit(
            repository.RepoDirectory,
            "diff",
            "--name-only",
            GoldenStepRepository.TagForStep(step - 1),
            GoldenStepRepository.TagForStep(step));
        return output
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToList();
    }
}
