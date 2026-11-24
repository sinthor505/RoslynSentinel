using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.ToolUse;

/// <summary>
/// Delete one long comment block and replace another long block (about 120 lines) with a single statement, in one file. Built for
/// the edit-tool comparison (todo-punchlist #12): a replace-by-text edit has to re-send the whole long block as its old text,
/// while a replace-by-range edit needs only the two one-line marker anchors plus the short new text. The seed is expanded
/// from loops once at type initialisation, so this source stays small; the text written to disk is the literal expansion.
/// </summary>
internal static class LargeBlockRewriteScenario
{
    private const string QuotePath = "ContosoOrders.Core/Evals/LargeBlockRewrite/QuoteCalculator.cs";

    private const string VisibleTestsPath = "ContosoOrders.Tests/Evals/LargeBlockRewrite/QuoteCalculatorTests.cs";

    /// <summary>Number of weight brackets in the legacy table; 8 lines each, so the block is 120 lines.</summary>
    private const int BracketCount = 15;

    /// <summary>Content lines of the disabled-experiment comment block (the two marker lines come on top, 60 lines in all).</summary>
    private const int DeadCodeLineCount = 58;

    // Fields below are initialised in textual order; Definition (further down) reads both of these.
    private static readonly string SeedSource = BuildSource(replaceTable: false, removeExperiment: false);
    private static readonly string GoldenSource = BuildSource(replaceTable: true, removeExperiment: true);

    // Visible test file: red on the seed (the legacy prices differ from weight times rate), green on the golden.
    private const string VisibleTestsSource = """
using ContosoOrders.Evals.LargeBlockRewrite;
using Xunit;

namespace ContosoOrders.Tests.Evals.LargeBlockRewrite;

public class QuoteCalculatorTests
{
    [Theory]
    [InlineData(100, "domestic", 100)]
    [InlineData(250, "domestic", 250)]
    [InlineData(1000, "domestic", 1000)]
    [InlineData(5000, "domestic", 5000)]
    [InlineData(100, "international", 300)]
    [InlineData(700, "international", 2100)]
    [InlineData(4000, "international", 12000)]
    public void Quote_IsWeightTimesRatePerGram(int weightGrams, string zone, int expected)
    {
        Assert.Equal((decimal)expected, QuoteCalculator.Quote(weightGrams, zone));
    }

    [Fact]
    public void Quote_UnknownZone_Throws()
    {
        Assert.Throws<ArgumentException>(() => QuoteCalculator.Quote(100, "mars"));
    }

    [Fact]
    public void Describe_ReturnsItsOriginalText()
    {
        Assert.Equal("Shipping zone: domestic", QuoteCalculator.Describe("domestic"));
    }
}
""";

    private const string CannedReport = "DONE: deleted the disabled-experiment comment block and replaced the legacy-table block in Quote with one return statement; the build and the tests pass.";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order). The prompt names no tool.
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/LargeBlockRewrite/QuoteCalculator.cs make two changes. " +
            "1. Delete the whole disabled-experiment comment block: the marker line `// BEGIN DISABLED EXPERIMENT`, the marker line `// END DISABLED EXPERIMENT` and every line between them. " +
            "2. In the method `Quote`, replace the whole legacy-table block (the marker line `// BEGIN LEGACY TABLE`, the marker line `// END LEGACY TABLE` and every line between them) " +
            "with the single statement `return weightGrams * RatePerGram(zone);`.",
        Files: "QuoteCalculator.cs only.",
        Conditions: "Every other line of the file stays exactly as it is, including `RatePerGram`, `Describe`, the `throw` after the new return " +
            "(it becomes unreachable; the compiler warning CS0162 is expected) and the blank lines around the deleted block.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including QuoteCalculatorTests."));

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "LargeBlockRewrite",
        "Delete one long comment block and replace one long block with a single statement, leaving the rest of the file alone",
        "edit tool, exactness",
        "todo-punchlist #12 (edit-tool comparison: ReplaceSnippet vs ReplaceRange vs EditSnippets)",
        new Dictionary<string, string>
        {
            [QuotePath] = SeedSource,
            [VisibleTestsPath] = VisibleTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [QuotePath] = GoldenSource,
                },
                AllowedPaths: new List<string> { QuotePath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckExactFile(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "QuoteCalculatorTests" }),
        },
        PreferredHost: "112",
        PrimaryAxis: EvalAxis.ToolUse);

    /// <summary>
    /// The seed (legacy table and disabled block present), the golden (both gone, one return statement, the blank lines before and after
    /// the deleted experiment block kept) or a half-done state (only one of the two changes made).
    /// </summary>
    private static string BuildSource(bool replaceTable, bool removeExperiment)
    {
        var lines = new List<string>
        {
            "namespace ContosoOrders.Evals.LargeBlockRewrite;",
            "",
            "/// <summary>Shipping quotes by weight and zone.</summary>",
            "public static class QuoteCalculator",
            "{",
            "    public static decimal Quote(int weightGrams, string zone)",
            "    {",
        };

        if (replaceTable)
        {
            lines.Add("        return weightGrams * RatePerGram(zone);");
        }
        else
        {
            lines.Add("        // BEGIN LEGACY TABLE");
            lines.AddRange(LegacyTableLines());
            lines.Add("        // END LEGACY TABLE");
        }

        lines.Add("        throw new ArgumentException(\"unknown zone\");");
        lines.Add("    }");
        lines.Add("");

        if (!removeExperiment)
        {
            lines.Add("    // BEGIN DISABLED EXPERIMENT");
            lines.AddRange(DeadCodeLines());
            lines.Add("    // END DISABLED EXPERIMENT");
        }

        // The blank line after the experiment block stays when the block is deleted (the brief says the blank lines around it stay).
        lines.Add("");

        lines.AddRange(
        [
            "    public static decimal RatePerGram(string zone)",
            "    {",
            "        if (zone == \"domestic\")",
            "        {",
            "            return 1m;",
            "        }",
            "",
            "        if (zone == \"international\")",
            "        {",
            "            return 3m;",
            "        }",
            "",
            "        throw new ArgumentException(\"unknown zone\");",
            "    }",
            "",
            "    public static string Describe(string zone)",
            "    {",
            "        return \"Shipping zone: \" + zone;",
            "    }",
            "}",
        ]);

        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>A regular if/else-if chain over weight brackets and zones; 8 lines per bracket, prices that differ from weight times rate.</summary>
    private static IEnumerable<string> LegacyTableLines()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        for (var bracket = 1; bracket <= BracketCount; bracket++)
        {
            var limit = bracket * 250;
            var domestic = (4.50m + (0.75m * bracket)).ToString("0.00", invariant);
            var international = (12.00m + (2.25m * bracket)).ToString("0.00", invariant);
            yield return $"        {(bracket == 1 ? "if" : "else if")} (weightGrams <= {limit} && zone == \"domestic\")";
            yield return "        {";
            yield return $"            return {domestic}m;";
            yield return "        }";
            yield return $"        else if (weightGrams <= {limit} && zone == \"international\")";
            yield return "        {";
            yield return $"            return {international}m;";
            yield return "        }";
        }
    }

    /// <summary>Commented-out statements that look like code that was switched off.</summary>
    private static IEnumerable<string> DeadCodeLines()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        for (var n = 1; n <= DeadCodeLineCount; n++)
        {
            var threshold = n * 100;
            var amount = (n * 0.05m).ToString("0.00", invariant);
            yield return $"    //     surcharge += weightGrams > {threshold} ? {amount}m : 0m;";
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>
    /// The task is a pure text transformation, so the exact file is the spec: the whole file, line endings normalised, equals the
    /// golden (both blocks gone, every other line and blank line kept, including the now unreachable throw). The detail names the
    /// first differing line. Scope (this one file only) is enforced by the framework from the step's allowed paths.
    /// </summary>
    private static string? CheckExactFile(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.Exists(QuotePath))
        {
            return $"missing: {QuotePath}";
        }

        var actual = Normalize(ctx.ReadText(QuotePath)).Split('\n');
        var expected = GoldenSource.Split('\n');
        var common = Math.Min(actual.Length, expected.Length);
        for (var index = 0; index < common; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
            {
                return $"{QuotePath} differs from the expected result at line {index + 1}: expected '{expected[index]}', found '{actual[index]}'";
            }
        }

        if (actual.Length != expected.Length)
        {
            return $"{QuotePath} has {actual.Length} line(s) (counting the end), expected {expected.Length}: lines were added or removed after line {common}";
        }

        return null;
    }

    /// <summary>
    /// The executable outcome table: each variant overlays QuoteCalculator.cs (the golden with one defect) on step 1 (golden) with a
    /// DONE: final message; every failing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Throws when the golden no longer contains the text to edit, so a golden change cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText)
        {
            if (!text.Contains(oldText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"LargeBlockRewrite variant edit: '{oldText}' is not in the golden.");
            }

            return text.Replace(oldText, newText, StringComparison.Ordinal);
        }

        const string Done = "DONE: deleted the experiment block and replaced the legacy table with one return statement.";
        const string ReturnLine = "        return weightGrams * RatePerGram(zone);\n";

        // Row 1: the unreachable throw after the new return deleted (the brief says it stays).
        var throwDeleted = Edited(GoldenSource, ReturnLine + "        throw new ArgumentException(\"unknown zone\");\n", ReturnLine);

        // Row 2: one of the two blank lines around the deleted experiment block deleted.
        var blankLineDeleted = Edited(GoldenSource, "    }\n\n\n    public static decimal RatePerGram", "    }\n\n    public static decimal RatePerGram");

        // Row 3: the legacy table replaced but the disabled experiment block left in place.
        var onlyTableReplaced = BuildSource(replaceTable: true, removeExperiment: false);

        return new[]
        {
            new ScenarioVariant(
                Name: "ThrowDeleted",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [QuotePath] = throwDeleted },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "differs from the expected result"),
            new ScenarioVariant(
                Name: "BlankLineDeleted",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [QuotePath] = blankLineDeleted },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "differs from the expected result"),
            new ScenarioVariant(
                Name: "OnlyOneBlockRemoved",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [QuotePath] = onlyTableReplaced },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "differs from the expected result"),
        };
    }
}
