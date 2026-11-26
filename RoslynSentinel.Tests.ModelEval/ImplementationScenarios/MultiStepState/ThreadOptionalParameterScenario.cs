using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.MultiStepState;

/// <summary>
/// Thread a trailing optional parameter through three methods, make one branch honour it and add two tests copied from a
/// neighbour, without touching the callers. The visible ErrorHintBehaviourTests decide the behaviour; the check wants the two
/// added tests to kill two mutants of the hint (parameter ignored; any non-empty collection matches).
/// </summary>
internal static class ThreadOptionalParameterScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/ThreadOptionalParameter/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/ThreadOptionalParameter/";
    private const string HintPath = CoreFolder + "ErrorHint.cs";
    private const string CallerAPath = CoreFolder + "CallerA.cs";
    private const string CallerBPath = CoreFolder + "CallerB.cs";
    private const string TestsPath = TestFolder + "ErrorHintTests.cs";

    private const string SeedHintSource = """
namespace ContosoOrders.Evals.ThreadOptionalParameter;

/// <summary>Builds the hint text appended to a compiler error.</summary>
public static class ErrorHint
{
    /// <summary>Describes the error code for a name; candidates are the names in scope that resemble it.</summary>
    public static string Describe(string code, string name, IReadOnlyCollection<string> candidates)
    {
        string Hint() => DescribeHint(code, name, candidates);
        return $"{code}: {Hint()}";
    }

    private static string DescribeHint(string code, string name, IReadOnlyCollection<string> candidates)
    {
        return code == "CS0103" ? DescribeNameMissing(name, candidates) : "No extra hint.";
    }

    private static string DescribeNameMissing(string name, IReadOnlyCollection<string> candidates)
    {
        if (candidates.Count == 0)
        {
            var typoSentence = $" '{name}' is likely a typo.";
            var closestNameSentence = $" Closest name: {Capitalize(name)}.";
            return $"No symbol named '{name}' was found.{typoSentence}{closestNameSentence}";
        }

        return $"'{name}' is not in scope. Candidates: {string.Join(", ", candidates)}.";
    }

    private static string Capitalize(string name)
    {
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
""";

    private const string GoldenHintSource = """
namespace ContosoOrders.Evals.ThreadOptionalParameter;

/// <summary>Builds the hint text appended to a compiler error.</summary>
public static class ErrorHint
{
    /// <summary>Describes the error code for a name; candidates are the names in scope that resemble it.</summary>
    public static string Describe(string code, string name, IReadOnlyCollection<string> candidates, IReadOnlyCollection<string>? renamedAwayNames = null)
    {
        string Hint() => DescribeHint(code, name, candidates, renamedAwayNames);
        return $"{code}: {Hint()}";
    }

    private static string DescribeHint(string code, string name, IReadOnlyCollection<string> candidates, IReadOnlyCollection<string>? renamedAwayNames = null)
    {
        return code == "CS0103" ? DescribeNameMissing(name, candidates, renamedAwayNames) : "No extra hint.";
    }

    private static string DescribeNameMissing(string name, IReadOnlyCollection<string> candidates, IReadOnlyCollection<string>? renamedAwayNames = null)
    {
        if (candidates.Count == 0)
        {
            // A name the edit renamed away is explained by the edit, so the typo and closest-name text is dropped.
            if (renamedAwayNames is not null && renamedAwayNames.Contains(name, StringComparer.Ordinal))
            {
                return $"No symbol named '{name}' was found anywhere in the solution.";
            }

            var typoSentence = $" '{name}' is likely a typo.";
            var closestNameSentence = $" Closest name: {Capitalize(name)}.";
            return $"No symbol named '{name}' was found.{typoSentence}{closestNameSentence}";
        }

        return $"'{name}' is not in scope. Candidates: {string.Join(", ", candidates)}.";
    }

    private static string Capitalize(string name)
    {
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
""";

    private const string CallerASource = """
namespace ContosoOrders.Evals.ThreadOptionalParameter;

/// <summary>First caller: a name with no candidates.</summary>
public static class CallerA
{
    public static string Explain(string name) => ErrorHint.Describe("CS0103", name, Array.Empty<string>());
}
""";

    private const string CallerBSource = """
namespace ContosoOrders.Evals.ThreadOptionalParameter;

/// <summary>Second caller: names in scope are passed through, and a different error code.</summary>
public static class CallerB
{
    public static string Explain(string name, IReadOnlyCollection<string> inScope) => ErrorHint.Describe("CS0103", name, inScope);

    public static string ExplainOther(string name) => ErrorHint.Describe("CS0246", name, Array.Empty<string>());
}
""";

    private const string SeedTestsSource = """
using ContosoOrders.Evals.ThreadOptionalParameter;
using Xunit;

namespace ContosoOrders.Tests.Evals.ThreadOptionalParameter;

public class ErrorHintTests
{
    [Fact]
    public void MissingNameWithoutCandidates_MentionsTypoAndClosestName()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>());
        Assert.Contains("likely a typo", text);
        Assert.Contains("Closest name: Rate.", text);
    }
}
""";

    private const string GoldenTestsSource = """
using ContosoOrders.Evals.ThreadOptionalParameter;
using Xunit;

namespace ContosoOrders.Tests.Evals.ThreadOptionalParameter;

public class ErrorHintTests
{
    [Fact]
    public void MissingNameWithoutCandidates_MentionsTypoAndClosestName()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>());
        Assert.Contains("likely a typo", text);
        Assert.Contains("Closest name: Rate.", text);
    }

    [Fact]
    public void MissingNameRenamedAwayByTheEdit_DropsTheTypoAndClosestNameText()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>(), renamedAwayNames: new[] { "rate" });
        Assert.Equal("CS0103: No symbol named 'rate' was found anywhere in the solution.", text);
        Assert.DoesNotContain("likely a typo", text);
        Assert.DoesNotContain("Closest name", text);
    }

    [Fact]
    public void MissingNameNotRenamed_KeepsTheTypoText()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>(), renamedAwayNames: new[] { "other" });
        Assert.Contains("likely a typo", text);
    }
}
""";

    // Visible acceptance tests (the old hidden step-1 tests). Describe is called through reflection, so this file compiles on the seed, where
    // Describe has no fourth parameter: passing renamedAwayNames then fails the call (red) instead of breaking the build.
    private const string BehaviourTestsSource = """
using System.Reflection;
using ContosoOrders.Evals.ThreadOptionalParameter;
using Xunit;

namespace ContosoOrders.Tests.Evals.ThreadOptionalParameter;

public class ErrorHintBehaviourTests
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static string Describe(string code, string name, IReadOnlyCollection<string> candidates)
    {
        return Invoke(code, name, candidates);
    }

    private static string DescribeWith(string code, string name, IReadOnlyCollection<string> candidates, IReadOnlyCollection<string> renamedAwayNames)
    {
        return Invoke(code, name, candidates, renamedAwayNames);
    }

    // Missing fills an optional parameter the call leaves out; a fourth argument on a three-parameter method throws.
    private static string Invoke(params object[] arguments)
    {
        var method = typeof(ErrorHint).GetMethod("Describe", AnyStatic)!;
        var all = new List<object>(arguments);
        while (all.Count < method.GetParameters().Length)
        {
            all.Add(Type.Missing);
        }

        try
        {
            return (string)method.Invoke(null, all.ToArray())!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void RenamedAwayName_GetsTheFixedTextWithThePrefix()
    {
        var text = DescribeWith("CS0103", "rate", Array.Empty<string>(), new[] { "rate" });
        Assert.Equal("CS0103: No symbol named 'rate' was found anywhere in the solution.", text);
        Assert.DoesNotContain("likely a typo", text);
        Assert.DoesNotContain("Closest name", text);
    }

    [Fact]
    public void OtherRenamedName_KeepsTheTypoText()
    {
        var text = DescribeWith("CS0103", "rate", Array.Empty<string>(), new[] { "other" });
        Assert.Equal("CS0103: No symbol named 'rate' was found. 'rate' is likely a typo. Closest name: Rate.", text);
    }

    [Fact]
    public void WithoutTheParameter_OutputIsUnchanged()
    {
        Assert.Equal(
            "CS0103: No symbol named 'rate' was found. 'rate' is likely a typo. Closest name: Rate.",
            Describe("CS0103", "rate", Array.Empty<string>()));
        Assert.Equal("CS0103: 'rate' is not in scope. Candidates: rates.", Describe("CS0103", "rate", new[] { "rates" }));
    }

    [Fact]
    public void RenamedNames_AreIgnoredWhenCandidatesExistOrTheCodeIsDifferent()
    {
        Assert.Equal(
            "CS0103: 'rate' is not in scope. Candidates: rates.",
            DescribeWith("CS0103", "rate", new[] { "rates" }, new[] { "rate" }));
        Assert.Equal("CS0246: No extra hint.", DescribeWith("CS0246", "rate", Array.Empty<string>(), new[] { "rate" }));
    }

    [Fact]
    public void RenamedAwayNames_MatchIsOrdinal()
    {
        // A collection with its own case-insensitive comparer must not decide the match: the comparison is ordinal.
        var renamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RATE" };
        var text = DescribeWith("CS0103", "rate", Array.Empty<string>(), renamed);
        Assert.Contains("likely a typo", text);
    }

    [Theory]
    [InlineData("Describe")]
    [InlineData("DescribeHint")]
    [InlineData("DescribeNameMissing")]
    public void ParameterIsTrailingOptionalWithNullDefault(string methodName)
    {
        var method = typeof(ErrorHint).GetMethod(methodName, AnyStatic)!;
        var last = method.GetParameters().Last();
        Assert.Equal("renamedAwayNames", last.Name);
        Assert.Equal(typeof(IReadOnlyCollection<string>), last.ParameterType);
        Assert.True(last.HasDefaultValue);
        Assert.Null(last.DefaultValue);
    }

    [Fact]
    public void ExistingCallersStillWork()
    {
        Assert.Contains("likely a typo", CallerA.Explain("rate"));
        Assert.Contains("Candidates: rates", CallerB.Explain("rate", new[] { "rates" }));
        Assert.Equal("CS0246: No extra hint.", CallerB.ExplainOther("rate"));
    }
}
""";

    // Variant overlay: the model's ErrorHintTests.cs with the original test plus one [Theory] of two rows (fewer than three [Fact] methods).
    private const string TheoryTestsSource = """
using ContosoOrders.Evals.ThreadOptionalParameter;
using Xunit;

namespace ContosoOrders.Tests.Evals.ThreadOptionalParameter;

public class ErrorHintTests
{
    [Fact]
    public void MissingNameWithoutCandidates_MentionsTypoAndClosestName()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>());
        Assert.Contains("likely a typo", text);
        Assert.Contains("Closest name: Rate.", text);
    }

    [Theory]
    [InlineData("rate", true)]
    [InlineData("other", false)]
    public void RenamedAwayNames_DecideWhetherTheTypoTextIsDropped(string renamed, bool dropped)
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>(), renamedAwayNames: new[] { renamed });
        Assert.Equal(dropped, !text.Contains("likely a typo"));
        Assert.Equal(dropped, !text.Contains("Closest name"));
    }
}
""";

    private const string CannedReport = "DONE: threaded renamedAwayNames through Describe, DescribeHint and DescribeNameMissing and added two tests to ErrorHintTests.cs.";

    // The guard's condition as it appears in GoldenHintSource; the mutants and variants below each change this text, so a template change cannot silently turn them into the golden.
    private const string GuardCondition = "renamedAwayNames is not null && renamedAwayNames.Contains(name, StringComparer.Ordinal)";

    // Edits are written with \n; the template's line endings are kept. Throws when the template no longer contains the text to
    // edit, so a template change cannot silently turn a mutant or variant into the golden.
    private static string Edited(string text, string oldText, string newText)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains(oldText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ThreadOptionalParameter edit: '{oldText}' is not in the template.");
        }

        return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
    }

    // The two mutants (Spec S9): the golden hint with exactly one changed condition each. static readonly (not const) so the change is stated
    // against GoldenHintSource; only read by the check, never by the Definition.
    // The parameter is ignored: the guard never fires.
    private static readonly string GuardIgnoredHint = Edited(GoldenHintSource, GuardCondition, "name.Length < 0");

    // Any non-empty collection counts as a match, whatever it holds.
    private static readonly string MatchesAnyNonEmptyHint = Edited(GoldenHintSource, GuardCondition, "renamedAwayNames is not null && renamedAwayNames.Count > 0");

    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/ThreadOptionalParameter/ErrorHint.cs the CS0103 hint says a name is \"likely a typo\" and offers a \"Closest name\" " +
            "even when an edit has just renamed that name away. Add a trailing optional parameter `IReadOnlyCollection<string>? renamedAwayNames = null` " +
            "to `Describe`, `DescribeHint` and `DescribeNameMissing` and pass it down the chain. When there are no candidates and `renamedAwayNames` " +
            "contains the name (ordinal comparison), `Describe` returns exactly `CS0103: No symbol named '{name}' was found anywhere in the solution.` " +
            "(the `CS0103: ` prefix every Describe text carries, then that sentence), without \"likely a typo\" or \"Closest name\". " +
            "Every other output stays byte-identical.",
        Files: "ErrorHint.cs and ContosoOrders.Tests/Evals/ThreadOptionalParameter/ErrorHintTests.cs. CallerA.cs and CallerB.cs keep compiling unedited.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ErrorHintBehaviourTests; " +
            "in ErrorHintTests you add two tests next to the existing one, copying its shape: one where `renamedAwayNames` contains the name, " +
            "one where it holds a different name (the typo text stays); your two tests fail if the parameter is ignored and if it matches any non-empty collection."));

    /// <summary>The base prompt's wording of the match rule, replaced by <see cref="ExplicitOrdinalWording"/> in the ExplicitOrdinal prompt variant.</summary>
    internal const string OrdinalPhrase = "(ordinal comparison)";

    /// <summary>The ExplicitOrdinal variant's wording: says why the collection's own Contains is not enough and what to call instead.</summary>
    internal const string ExplicitOrdinalWording = "(the collection may carry its own comparer, so compare each element with `string.Equals(element, name, StringComparison.Ordinal)` rather than the collection's `Contains`)";

    internal const string ExplicitOrdinalVariantName = "ExplicitOrdinal";

    // Built from the base prompt so the two cannot drift; throws when the base no longer holds the phrase.
    private static string ExplicitOrdinalPrompt
    {
        get
        {
            var basePrompt = StepPrompt;
            if (!basePrompt.Contains(OrdinalPhrase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ThreadOptionalParameter prompt variant {ExplicitOrdinalVariantName}: '{OrdinalPhrase}' is not in the base prompt.");
            }

            return basePrompt.Replace(OrdinalPhrase, ExplicitOrdinalWording, StringComparison.Ordinal);
        }
    }

    public static ImplementationScenario Definition
    {
        get;
    } = new ImplementationScenario(
        "ThreadOptionalParameter",
        "Thread a trailing optional parameter through three methods and add two tests copied from a neighbour",
        "multi-step state, adherence",
        "ModelEval-Scenario-Ideas 03, commit 96bac8c8",
        new Dictionary<string, string>
        {
            [HintPath] = SeedHintSource,
            [CallerAPath] = CallerASource,
            [CallerBPath] = CallerBSource,
            [TestsPath] = SeedTestsSource,
            [TestFolder + "ErrorHintBehaviourTests.cs"] = BehaviourTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [HintPath] = GoldenHintSource,
                    [TestsPath] = GoldenTestsSource,
                },
                AllowedPaths: new List<string> { HintPath, TestsPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => CheckAddedTestsKillMutantsAsync(ctx),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "ErrorHintBehaviourTests", "ErrorHintTests" },
                PromptVariants: new Dictionary<string, string> { [ExplicitOrdinalVariantName] = ExplicitOrdinalPrompt }),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.MultiStepState);

    private const string TestProjectPath = "ContosoOrders.Tests/ContosoOrders.Tests.csproj";

    private const string ModelTestClassName = "ContosoOrders.Tests.Evals.ThreadOptionalParameter.ErrorHintTests";

    private const string SeedTestName = "MissingNameWithoutCandidates_MentionsTypoAndClosestName";

    /// <summary>
    /// The hint and test files exist and the tests the model added to ErrorHintTests (every test method besides the seed one) pass against the
    /// reference ErrorHint and kill both mutants (parameter ignored; any non-empty collection matches). The build, the visible
    /// ErrorHintBehaviourTests (trailing optional parameter, ordinal match, unchanged output) and scope (CallerA.cs and CallerB.cs untouched)
    /// are decided by the shared pipeline and the test run, not here.
    /// </summary>
    private static async Task<string?> CheckAddedTestsKillMutantsAsync(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.Exists(HintPath))
        {
            return $"missing: {HintPath}";
        }

        if (!ctx.Exists(TestsPath))
        {
            return $"missing: {TestsPath}";
        }

        var addedTests = ctx.Syntax(TestsPath).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == "ErrorHintTests")
            .SelectMany(declaration => declaration.Members.OfType<MethodDeclarationSyntax>())
            .Where(method => method.Identifier.ValueText != SeedTestName && method.AttributeLists.SelectMany(list => list.Attributes).Any(IsTestAttribute))
            .Select(method => method.Identifier.ValueText)
            .Distinct()
            .ToList();
        if (addedTests.Count == 0)
        {
            return "ErrorHintTests.cs has no test method besides the original one, expected two new ones";
        }

        // A [Theory] shows up as its method name plus the row, so the ~ (contains) filter picks up every row.
        var filter = string.Join("|", addedTests.Select(name => $"FullyQualifiedName~{ModelTestClassName}.{name}"));
        var mutants = new[]
        {
            new ScenarioMutant("Golden", new Dictionary<string, string> { [HintPath] = GoldenHintSource }),
            new ScenarioMutant(nameof(GuardIgnoredHint), new Dictionary<string, string> { [HintPath] = GuardIgnoredHint }),
            new ScenarioMutant(nameof(MatchesAnyNonEmptyHint), new Dictionary<string, string> { [HintPath] = MatchesAnyNonEmptyHint }),
        };

        // Sequential inside MutationGrader: one dotnet test at a time. The first entry is the unmutated golden, the rest are the mutants.
        var results = await MutationGrader.RunAsync(ctx.SolutionDirectory, TestProjectPath, filter, mutants, ctx.CancellationToken);
        var golden = results[0];
        var names = string.Join(", ", addedTests);
        if (golden.Killed)
        {
            var why = golden.Note.Length > 0 ? golden.Note : $"{golden.Failed} of {golden.Passed + golden.Failed} failed";
            return $"the tests you added ({names}) do not pass against the reference ErrorHint: {why}";
        }

        if (golden.Passed == 0)
        {
            return $"no test of {names} ran against the reference ErrorHint";
        }

        var survivors = results.Skip(1).Where(result => !result.Killed).Select(result => result.Name).ToList();
        if (survivors.Count > 0)
        {
            return $"the tests you added do not kill: {string.Join(", ", survivors)} (every test still passed against an ErrorHint with that one defect)";
        }

        return null;
    }

    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute";
    }

    /// <summary>
    /// The executable outcome table (Spec S9): each variant overlays ErrorHint.cs and/or ErrorHintTests.cs (the golden plus one change) on step 1
    /// (golden) with a DONE: final message. A failing row claims DONE, so its class is SilentIncomplete. The two failing rows break a visible
    /// ErrorHintBehaviourTests test, so the shared test run rejects them before the mutation check.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        const string Done = "DONE: threaded renamedAwayNames through the chain and added two tests.";
        const string TypoBlock = "            var typoSentence";
        const string FixedSentenceReturn = "                return $\"No symbol named '{name}' was found anywhere in the solution.\";";

        // Row 1: the guard is a ternary on the final return of the block instead of an early return as the first statement.
        var withoutGuardBlock = Edited(
            GoldenHintSource,
            "            // A name the edit renamed away is explained by the edit, so the typo and closest-name text is dropped.\n"
            + "            if (" + GuardCondition + ")\n"
            + "            {\n"
            + FixedSentenceReturn + "\n"
            + "            }\n\n"
            + TypoBlock,
            TypoBlock);
        var ternaryGuard = Edited(
            withoutGuardBlock,
            "            return $\"No symbol named '{name}' was found.{typoSentence}{closestNameSentence}\";",
            "            return " + GuardCondition + "\n"
            + "                ? $\"No symbol named '{name}' was found anywhere in the solution.\"\n"
            + "                : $\"No symbol named '{name}' was found.{typoSentence}{closestNameSentence}\";");

        // Row 3: the match goes through the collection's own comparer (a case-insensitive set matches "RATE" for "rate"); it builds, and the ordinal test fails.
        var collectionContains = Edited(GoldenHintSource, "renamedAwayNames.Contains(name, StringComparer.Ordinal)", "renamedAwayNames.Contains(name)");

        return new[]
        {
            new ScenarioVariant(
                Name: "TernaryGuard",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [HintPath] = ternaryGuard },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "TheoryTests",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [TestsPath] = TheoryTestsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "CollectionContains",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [HintPath] = collectionContains },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
            new ScenarioVariant(
                Name: "GuardIgnored",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [HintPath] = GuardIgnoredHint },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
        };
    }
}
