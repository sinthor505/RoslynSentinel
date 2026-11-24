using System.Diagnostics;
using System.Globalization;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model self-test for the implementation scenarios. Per scenario: <c>Golden_&lt;Id&gt;_Step&lt;k&gt;</c> (the step's golden passes its
/// check), <c>Previous_&lt;Id&gt;_Step&lt;k&gt;</c> (the previous state fails it), <c>Budget_&lt;Id&gt;</c> (seed text + longest prompt fits the
/// context budget), <c>Variant_&lt;Id&gt;_Step&lt;k&gt;_&lt;Name&gt;</c> (the executable outcome table), <c>Seed_&lt;Id&gt;_BuildsWithZeroErrors</c> and
/// <c>Files_&lt;Id&gt;_Step&lt;k&gt;_NamesEveryAllowedPath</c>. Parked scenarios yield one Ignored <c>Golden_&lt;Id&gt;_Parked</c> case. Each scenario owns one fixture and one
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

        var verdict = await ImplementationScenarioChecks.VerdictAsync(
            scenario,
            step,
            fixture.SolutionDirectory,
            scenario.Steps[step - 1].CannedFinalMessage,
            changedPaths,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(verdict.Outcome, Is.EqualTo(ImplementationScenarioOutcome.Pass), $"{scenario.Id} step {step}: {verdict.Detail}");

        // The class needs the DONE:/BLOCKED: token in the canned message, which a scenario gets when it is reworked and declares its
        // axis; a scenario that has not been reworked yet (PrimaryAxis Unassigned) has no token and is graded on the outcome only.
        if (scenario.PrimaryAxis != EvalAxis.Unassigned)
        {
            var expectedClass = scenario.Steps[step - 1].ExpectedEnding == ExpectedEnding.Blocked
                ? EvalOutcomeClass.CorrectBlocked
                : EvalOutcomeClass.CorrectDone;
            Assert.That(verdict.Class, Is.EqualTo(expectedClass), $"{scenario.Id} step {step}: {verdict.Detail}");
        }
    }

    [TestCaseSource(nameof(PreviousCases))]
    public async Task Previous_StepFailsTheCheck(string scenarioId, int step)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var (fixture, repository) = GetOrCreate(scenario);
        repository.CheckoutStep(step - 1, $"verify/{scenario.Id}-previous-of-step-{step}");

        var verdict = await ImplementationScenarioChecks.VerdictAsync(
            scenario,
            step,
            fixture.SolutionDirectory,
            null,
            Array.Empty<string>(),
            TestContext.CurrentContext.CancellationToken);
        var outcome = verdict.Outcome;
        var detail = verdict.Detail;
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

    /// <summary>
    /// The executable outcome table: applies each named variant (an overlay on its base step plus an optional final message) and
    /// asserts what the grader says. A grader change that flips a table row fails here.
    /// </summary>
    [TestCaseSource(nameof(VariantCases))]
    public async Task Variant_OutcomeMatchesTable(string scenarioId, int step, string variantName)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var variant = scenario.Steps[step - 1].Variants!.Single(candidate => candidate.Name == variantName);
        var (fixture, repository) = GetOrCreate(scenario);
        repository.CheckoutStep(variant.BaseStep, $"verify/{scenario.Id}-step-{step}-variant-{variant.Name}");
        foreach (var (relativePath, text) in variant.Files)
        {
            var fullPath = Path.Combine(fixture.SolutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, ImplementationScenario.NormalizeToCrlf(text));
        }

        // Same order as the agent test: changed paths first, then the commit (CommitAll allows an empty commit for an overlay-free variant).
        var changedPaths = repository.ChangedPathsSinceStep(step - 1);
        repository.CommitWorkingTree($"variant {variant.Name} of step {step}");

        var verdict = await ImplementationScenarioChecks.VerdictAsync(
            scenario,
            step,
            fixture.SolutionDirectory,
            variant.FinalMessage,
            changedPaths,
            TestContext.CurrentContext.CancellationToken);
        TestContext.Out.WriteLine($"{scenario.Id} step {step} variant {variant.Name}: {verdict.Outcome}, {verdict.Class}, variant '{verdict.Variant}': {verdict.Detail}");

        var message = $"{scenario.Id} step {step} variant {variant.Name}: {verdict.Detail}";
        Assert.That(verdict.Outcome, Is.EqualTo(variant.ExpectedOutcome), message);
        Assert.That(verdict.Class, Is.EqualTo(variant.ExpectedClass), message);
        if (variant.DetailContains is not null)
        {
            Assert.That(verdict.Detail, Does.Contain(variant.DetailContains), message);
        }
    }

    /// <summary>A seed test must compile against the seed: step 0 (the seed with its visible tests) builds with 0 errors, so the new tests are red, not broken.</summary>
    [TestCaseSource(nameof(SeedCases))]
    public async Task Seed_BuildsWithZeroErrors(string scenarioId)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var (fixture, repository) = GetOrCreate(scenario);
        repository.CheckoutStep(0, $"verify/{scenario.Id}-seed-build");
        var testProject = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var startInfo = new ProcessStartInfo("dotnet", $"build \"{testProject}\" -c Debug --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start dotnet build");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput + await standardError;

        var errors = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("error CS", StringComparison.Ordinal))
            .Distinct()
            .Take(10);
        Assert.That(process.ExitCode, Is.EqualTo(0), $"{scenario.Id}: the seed does not build. First errors:\n{string.Join("\n", errors)}");
    }

    /// <summary>Every path a step lets the model change is named in the prompt's Files line (file name, or folder name for an entry ending in '/').</summary>
    [TestCaseSource(nameof(FilesCases))]
    public void Files_NamesEveryAllowedPath(string scenarioId, int step)
    {
        var scenario = ImplementationScenarioRegistry.Find(scenarioId);
        var stepDefinition = scenario.Steps[step - 1];
        var filesLine = stepDefinition.Prompt
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("Files:", StringComparison.Ordinal));
        if (filesLine is null)
        {
            // A prompt not yet rewritten into the Goal/Files/... shape has no Files line to check.
            Assert.Ignore($"{scenario.Id} step {step}: the prompt has no 'Files:' line (not reworked yet).");
        }

        foreach (var entry in stepDefinition.AllowedPaths)
        {
            var normalized = entry.Replace('\\', '/');
            var name = normalized.TrimEnd('/').Split('/').Last();
            Assert.That(filesLine, Does.Contain(name), $"{scenario.Id} step {step}: the Files line does not name allowed path '{entry}'.");
        }
    }

    [Test]
    public void ContextCharacters_ExcludesSeedTestsNoStepMayEdit()
    {
        var coreFile = new string('a', 100);
        var editableTest = new string('b', 200);
        var visibleTest = new string('c', 4000);
        const string prompt = "a prompt";
        var scenario = new ImplementationScenario(
            "ContextCountProbe",
            "Context count probe",
            "probe",
            "none",
            new Dictionary<string, string>
            {
                ["ContosoOrders.Core/Evals/Probe/Core.cs"] = coreFile,
                ["ContosoOrders.Tests/Evals/Probe/Editable.cs"] = editableTest,
                ["ContosoOrders.Tests/Evals/Probe/Visible.cs"] = visibleTest,
            },
            new[]
            {
                new ImplementationScenarioStep(
                    prompt,
                    new Dictionary<string, string>(),
                    new[] { "ContosoOrders.Tests/Evals/Probe/Editable.cs" },
                    null,
                    null,
                    null),
            });

        Assert.That(scenario.ContextCharacters(), Is.EqualTo(coreFile.Length + editableTest.Length + prompt.Length));
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

    private static IEnumerable<TestCaseData> VariantCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                foreach (var variant in scenario.Steps[step - 1].Variants ?? [])
                {
                    yield return new TestCaseData(scenario.Id, step, variant.Name).SetName($"Variant_{scenario.Id}_Step{step}_{variant.Name}");
                }
            }
        }
    }

    private static IEnumerable<TestCaseData> SeedCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            yield return new TestCaseData(scenario.Id).SetName($"Seed_{scenario.Id}_BuildsWithZeroErrors");
        }
    }

    // A step with no AllowedPaths (a read-only review) has no Files line to compare, so it yields no case.
    private static IEnumerable<TestCaseData> FilesCases()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                if (scenario.Steps[step - 1].AllowedPaths.Count > 0)
                {
                    yield return new TestCaseData(scenario.Id, step).SetName($"Files_{scenario.Id}_Step{step}_NamesEveryAllowedPath");
                }
            }
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
