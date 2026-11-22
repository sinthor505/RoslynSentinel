using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Thread a trailing optional parameter through three methods, add a guard as the first statement of one branch and two tests
/// copied from a neighbour, without touching the callers.
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

    private const string HiddenTestSource = """
using System.Reflection;
using ContosoOrders.Evals.ThreadOptionalParameter;
using Xunit;

namespace ContosoOrders.Tests.Evals.ThreadOptionalParameter;

public class ThreadOptionalParameterStep1Tests
{
    [Fact]
    public void RenamedAwayName_GetsTheFixedSentenceOnly()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>(), renamedAwayNames: new[] { "rate" });
        Assert.Equal("CS0103: No symbol named 'rate' was found anywhere in the solution.", text);
    }

    [Fact]
    public void OtherRenamedName_KeepsTheTypoText()
    {
        var text = ErrorHint.Describe("CS0103", "rate", Array.Empty<string>(), renamedAwayNames: new[] { "other" });
        Assert.Contains("likely a typo", text);
    }

    [Fact]
    public void WithoutTheParameter_OutputIsUnchanged()
    {
        Assert.Equal(
            "CS0103: No symbol named 'rate' was found. 'rate' is likely a typo. Closest name: Rate.",
            ErrorHint.Describe("CS0103", "rate", Array.Empty<string>()));
        Assert.Equal("CS0103: 'rate' is not in scope. Candidates: rates.", ErrorHint.Describe("CS0103", "rate", new[] { "rates" }));
    }

    [Fact]
    public void RenamedNames_AreIgnoredWhenCandidatesExistOrTheCodeIsDifferent()
    {
        Assert.Equal(
            "CS0103: 'rate' is not in scope. Candidates: rates.",
            ErrorHint.Describe("CS0103", "rate", new[] { "rates" }, new[] { "rate" }));
        Assert.Equal("CS0246: No extra hint.", ErrorHint.Describe("CS0246", "rate", Array.Empty<string>(), new[] { "rate" }));
    }

    [Theory]
    [InlineData("Describe")]
    [InlineData("DescribeHint")]
    [InlineData("DescribeNameMissing")]
    public void ParameterIsTrailingOptionalWithNullDefault(string methodName)
    {
        var method = typeof(ErrorHint).GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
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

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/ThreadOptionalParameter/ErrorHint.cs the CS0103 hint says a name is "likely a typo" and offers a
"Closest name" even when an edit has just renamed that name away. Add a trailing optional parameter
`IReadOnlyCollection<string>? renamedAwayNames = null` to Describe, DescribeHint and DescribeNameMissing and pass it down the
chain. In DescribeNameMissing, as the very first statement of the `if (candidates.Count == 0)` block (before the typo and
closest-name sentences are built), return exactly the text `No symbol named '{name}' was found anywhere in the solution.`
when renamedAwayNames contains the name (ordinal comparison); every other output must stay byte-identical. Do not edit
CallerA.cs or CallerB.cs. In ContosoOrders.Tests/Evals/ThreadOptionalParameter/ErrorHintTests.cs add two tests next to the
existing one, copying its shape: one where renamedAwayNames contains the name (the text is the sentence above and has
neither "likely a typo" nor "Closest name"), and one where it holds a different name (the typo text stays).
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "ThreadOptionalParameter",
        "Thread a trailing optional parameter through three methods, guard first in a branch, add two copied tests",
        "multi-step state, adherence",
        "ModelEval-Scenario-Ideas 03, commit 96bac8c8",
        new Dictionary<string, string>
        {
            [HintPath] = SeedHintSource,
            [CallerAPath] = CallerASource,
            [CallerBPath] = CallerBSource,
            [TestsPath] = SeedTestsSource,
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
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckGuardAndTests(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "113");

    /// <summary>Callers unchanged; the guard is the first statement of the candidates.Count == 0 block; the model added at least two tests.</summary>
    private static string? CheckGuardAndTests(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { CallerAPath, CallerBPath })
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"changed but must stay as it was: {path}";
            }
        }

        var root = ctx.Syntax(HintPath).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "DescribeNameMissing");
        if (method?.Body is null)
        {
            return "DescribeNameMissing is gone or lost its block body";
        }

        var branch = method.Body.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Replace(" ", string.Empty) == "candidates.Count==0");
        if (branch?.Statement is not BlockSyntax block || block.Statements.Count == 0)
        {
            return "the 'if (candidates.Count == 0)' block of DescribeNameMissing is gone or empty";
        }

        if (block.Statements[0] is not IfStatementSyntax guard || !guard.Condition.ToString().Contains("renamedAwayNames"))
        {
            return "the first statement of the 'candidates.Count == 0' block is not a guard on renamedAwayNames";
        }

        var tests = ctx.Syntax(TestsPath).GetRoot();
        var facts = tests.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Count(m => m.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString() == "Fact"));
        if (facts < 3)
        {
            return $"ErrorHintTests.cs has {facts} [Fact] test(s), expected the original one plus two new ones";
        }

        // The seed Describe arity is read from SeedHintSource, so the check never hard-codes it. A call supplies the
        // new parameter when it has more arguments than the seed or names it renamedAwayNames: (positional or named).
        var seedArity = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(SeedHintSource).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == "Describe").ParameterList.Parameters.Count;
        var renamedAwareCalls = tests.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(call =>
            (call.Expression is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText) == "Describe"
            && (call.ArgumentList.Arguments.Count > seedArity
                || call.ArgumentList.Arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "renamedAwayNames")));
        if (renamedAwareCalls < 1)
        {
            return "the new tests do not pass renamedAwayNames";
        }

        return null;
    }
}
