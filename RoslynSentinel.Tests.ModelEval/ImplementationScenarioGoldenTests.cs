using System.Diagnostics;
using System.Globalization;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.MultiStepState;

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

        // The class comes from the DONE:/BLOCKED: token in the canned message; every scenario declares its axis and carries the token.
        var expectedClass = scenario.Steps[step - 1].ExpectedEnding == ExpectedEnding.Blocked
            ? EvalOutcomeClass.CorrectBlocked
            : EvalOutcomeClass.CorrectDone;
        Assert.That(verdict.Class, Is.EqualTo(expectedClass), $"{scenario.Id} step {step}: {verdict.Detail}");

        // Slip-site steps: every site must be graded Correct, so the golden implements each site as the brief asks.
        if (scenario.Steps[step - 1].Sites is not null)
        {
            Assert.That(verdict.Sites, Is.Not.Null, $"{scenario.Id} step {step}: the check produced no site grades");
            foreach (var grade in verdict.Sites ?? Array.Empty<SiteGrade>())
            {
                Assert.That(grade.Outcome, Is.EqualTo(SiteOutcome.Correct), $"{scenario.Id} step {step}: site '{grade.SiteName}' is {grade.Outcome}: {grade.EndStateText}");
            }
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

        // Slip-site steps: the previous state must fail the check and grade each site as the slip predicts.
        if (scenario.Steps[step - 1].Sites is not null)
        {
            Assert.That(outcome, Is.EqualTo(ImplementationScenarioOutcome.CheckFailed), $"{scenario.Id} step {step}: the step-{step - 1} state must be CheckFailed: {detail}");
            foreach (var grade in verdict.Sites ?? Array.Empty<SiteGrade>())
            {
                var expected = grade.Role == SiteRole.Changed ? SiteOutcome.Unchanged : SiteOutcome.Correct;
                Assert.That(grade.Outcome, Is.EqualTo(expected), $"{scenario.Id} step {step}: site '{grade.SiteName}' ({grade.Role}) is {grade.Outcome} on the step-{step - 1} state, expected {expected}: {grade.EndStateText}");
            }
        }
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
            File.WriteAllText(fullPath, variant.WriteRawBytes ? text : ImplementationScenario.NormalizeToCrlf(text));
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

        if (variant.ExpectedVariantTag is not null)
        {
            Assert.That(verdict.Variant, Is.EqualTo(variant.ExpectedVariantTag), message);
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
            Assert.Fail($"{scenario.Id} step {step}: the prompt has no 'Files:' line.");
        }

        foreach (var entry in stepDefinition.AllowedPaths)
        {
            var normalized = entry.Replace('\\', '/');
            var name = normalized.TrimEnd('/').Split('/').Last();
            Assert.That(filesLine, Does.Contain(name), $"{scenario.Id} step {step}: the Files line does not name allowed path '{entry}'.");
        }
    }

    /// <summary>A prompt variant is a prompt, so it names every allowed path in its Files line just as the base prompt does.</summary>
    [Test]
    public void PromptVariants_StillNameEveryAllowedPath()
    {
        var checkedVariants = 0;
        foreach (var scenario in ImplementationScenarioRegistry.All)
        {
            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                var stepDefinition = scenario.Steps[step - 1];
                foreach (var (name, variantPrompt) in stepDefinition.PromptVariants ?? new Dictionary<string, string>())
                {
                    var filesLine = variantPrompt
                        .Split('\n')
                        .Select(line => line.Trim())
                        .FirstOrDefault(line => line.StartsWith("Files:", StringComparison.Ordinal));
                    Assert.That(filesLine, Is.Not.Null, $"{scenario.Id} step {step} variant {name}: the prompt has no 'Files:' line.");
                    foreach (var entry in stepDefinition.AllowedPaths)
                    {
                        var allowedName = entry.Replace('\\', '/').TrimEnd('/').Split('/').Last();
                        Assert.That(filesLine, Does.Contain(allowedName), $"{scenario.Id} step {step} variant {name}: the Files line does not name allowed path '{entry}'.");
                    }

                    checkedVariants++;
                }
            }
        }

        Assert.That(checkedVariants, Is.GreaterThan(0), "no scenario declares a prompt variant, so this test checked nothing");
    }

    /// <summary>ExplicitOrdinal changes the base prompt in exactly one place: the ordinal-comparison phrase. Putting the phrase back gives the base prompt.</summary>
    [Test]
    public void ThreadOptionalParameter_ExplicitOrdinal_DiffersFromTheBasePromptOnlyInTheOrdinalPhrase()
    {
        var step = ImplementationScenarioRegistry.Find("ThreadOptionalParameter").Steps[0];
        var variant = step.PromptFor(ThreadOptionalParameterScenario.ExplicitOrdinalVariantName);

        Assert.That(variant, Is.Not.EqualTo(step.Prompt));
        Assert.That(variant, Does.Contain(ThreadOptionalParameterScenario.ExplicitOrdinalWording));
        Assert.That(variant, Does.Not.Contain(ThreadOptionalParameterScenario.OrdinalPhrase));
        Assert.That(variant.Replace(ThreadOptionalParameterScenario.ExplicitOrdinalWording, ThreadOptionalParameterScenario.OrdinalPhrase, StringComparison.Ordinal), Is.EqualTo(step.Prompt));
    }

    [Test]
    public void PromptFor_BlankIsTheBasePrompt_UnknownNameThrowsNamingSettingValueAndKnownVariants()
    {
        var step = ImplementationScenarioRegistry.Find("ThreadOptionalParameter").Steps[0];

        Assert.That(step.PromptFor(null), Is.EqualTo(step.Prompt));
        Assert.That(step.PromptFor("  "), Is.EqualTo(step.Prompt));
        var exception = Assert.Throws<InvalidOperationException>(() => step.PromptFor("NoSuchVariant"));
        Assert.That(exception!.Message, Does.Contain(TestHarnessSettings.ModelEvalPromptVariantSetting.EnvName));
        Assert.That(exception.Message, Does.Contain("NoSuchVariant"));
        Assert.That(exception.Message, Does.Contain(ThreadOptionalParameterScenario.ExplicitOrdinalVariantName));
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
        Assert.That(all, Has.Count.EqualTo(22));
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

    // Model-free (the class is [Explicit] only so the heavy per-step cases do not run by default; run these by name).
    // StepCases is private in the LiveModel agent test class, so it is read by reflection: that asserts the Axis:<Name>
    // category on the real case source instead of re-deriving it from the property.
    [Test]
    public void Registry_EveryScenarioDeclaresAnAxis()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All)
        {
            Assert.That(scenario.PrimaryAxis, Is.Not.EqualTo(EvalAxis.Unassigned), $"{scenario.Id} does not declare a PrimaryAxis.");
        }

        var stepCases = typeof(RoslynSentinel.Tests.ModelEval.LiveModel.ImplementationScenarioAgentTests)
            .GetMethod("StepCases", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(stepCases, Is.Not.Null, "ImplementationScenarioAgentTests.StepCases not found (renamed?); update this test.");

        var cases = ((IEnumerable<TestCaseData>)stepCases!.Invoke(null, null)!).ToList();
        foreach (var scenario in ImplementationScenarioRegistry.All.Where(scenario => scenario.ParkedReason is null))
        {
            var expectedCategory = EvalAxisCategory.Name(scenario.PrimaryAxis);
            for (var step = 1; step <= scenario.Steps.Count; step++)
            {
                var stepCase = cases.SingleOrDefault(testCase => (string)testCase.Arguments[0]! == scenario.Id && (int)testCase.Arguments[1]! == step);
                Assert.That(stepCase, Is.Not.Null, $"StepCases has no case for {scenario.Id} step {step}.");

                var categories = stepCase!.Properties["Category"].Cast<object>().Select(category => category.ToString()).ToList();
                Assert.That(categories, Does.Contain(expectedCategory), $"{scenario.Id} step {step} is missing category {expectedCategory}.");
            }
        }
    }

    // Pins Decision 6 of the scenario-rework plan (the stop family half lives in ScenarioRegistryTests). A re-axis is a deliberate edit of this test.
    [Test]
    public void Registry_AxisAssignment_MatchesThePlan()
    {
        var expected = new Dictionary<EvalAxis, string[]>
        {
            [EvalAxis.AmbiguityResolution] = ["PlanStalenessReview", "BreakCycleWithForwarders", "ShareHelperDifferingRules"],
            [EvalAxis.ErrorRecovery] = ["SweepConstructionSites"],
            [EvalAxis.MultiStepState] = ["ThreadOptionalParameter"],
            [EvalAxis.ToolUse] = ["LargeBlockRewrite"],
            [EvalAxis.Transcription] = ["WidenAcrossFilesSlip", "WidenDictionaryLeafFirstSlip", "SweepNamedSitesSlip", "EscapeLiteralsSlip", "DocCommentsSlip", "LongGenericsSlip", "NearDuplicateIdentifiersSlip"],
            [EvalAxis.InstructionAdherence] =
            [
                "WidenToReadOnlyDictionary", "WideningRejectedByGate", "ChangeSetTypeFromSpec", "RetypeSliceExactLines",
                "PropertyFirstRetype", "RemovePinnedSentence", "NotFoundToTypedThrow", "ObsoleteNamespaceCopy", "MigrateOntoEnvironmentHelper",
            ],
        };

        var expectedIds = expected.Values.SelectMany(ids => ids).ToList();
        Assert.That(ImplementationScenarioRegistry.All.Select(scenario => scenario.Id), Is.EquivalentTo(expectedIds),
            "The registry and the pinned assignment disagree on the scenario ids; add the new scenario to this test with its axis.");

        foreach (var (axis, ids) in expected)
        {
            foreach (var id in ids)
            {
                Assert.That(ImplementationScenarioRegistry.Find(id).PrimaryAxis, Is.EqualTo(axis), $"{id} is expected on axis {axis}.");
            }
        }
    }

    // The folder under ImplementationScenarios is the axis, so a scenario's namespace must end with its PrimaryAxis name.
    [Test]
    public void Registry_ScenarioFolderMatchesAxis()
    {
        foreach (var scenario in ImplementationScenarioRegistry.All)
        {
            var type = ImplementationScenarioRegistry.FindScenarioClass(scenario.Id);
            Assert.That(type, Is.Not.Null, $"no class <Id>Scenario found for id {scenario.Id}");
            Assert.That(type!.Namespace, Does.EndWith("." + scenario.PrimaryAxis), $"{type.Name} lives in the wrong axis folder for {scenario.PrimaryAxis}.");
        }

        var strayInGraders = typeof(ImplementationScenarioRegistry).Assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Scenario", StringComparison.Ordinal) && type.Namespace is { } ns && ns.EndsWith(".Graders", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToList();
        Assert.That(strayInGraders, Is.Empty, "a *Scenario class lives in Graders; scenario definitions belong in their axis folder.");
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
