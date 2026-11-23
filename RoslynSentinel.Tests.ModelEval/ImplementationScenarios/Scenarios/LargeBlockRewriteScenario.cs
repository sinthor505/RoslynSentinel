using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Delete one long comment block and replace another long block (about 120 lines) with a single statement, in one file. Built for
/// the edit-tool comparison (todo-punchlist #12): a replace-by-text edit has to re-send the whole long block as its old text,
/// while a replace-by-range edit needs only the two one-line marker anchors plus the short new text. The seed is expanded
/// from loops once at type initialisation, so this source stays small; the text written to disk is the literal expansion.
/// </summary>
internal static class LargeBlockRewriteScenario
{
    private const string QuotePath = "ContosoOrders.Core/Evals/LargeBlockRewrite/QuoteCalculator.cs";

    /// <summary>Number of weight brackets in the legacy table; 8 lines each, so the block is 120 lines.</summary>
    private const int BracketCount = 15;

    /// <summary>Content lines of the disabled-experiment comment block (the two marker lines come on top, 60 lines in all).</summary>
    private const int DeadCodeLineCount = 58;

    private static readonly string[] Markers =
    [
        "BEGIN LEGACY TABLE",
        "END LEGACY TABLE",
        "BEGIN DISABLED EXPERIMENT",
        "END DISABLED EXPERIMENT",
    ];

    // Fields below are initialised in textual order; Definition (further down) reads both of these.
    private static readonly string SeedSource = BuildSource(golden: false);
    private static readonly string GoldenSource = BuildSource(golden: true);

    private const string HiddenTestSource = """
using ContosoOrders.Evals.LargeBlockRewrite;
using Xunit;

namespace ContosoOrders.Tests.Evals.LargeBlockRewrite;

public class LargeBlockRewriteStep1Tests
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

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/LargeBlockRewrite/QuoteCalculator.cs make two changes:
1. Delete the whole disabled-experiment comment block: the marker line "// BEGIN DISABLED EXPERIMENT", the marker line
"// END DISABLED EXPERIMENT" and every line between them.
2. In the method Quote, replace the whole legacy-table block: the marker line "// BEGIN LEGACY TABLE", the marker line
"// END LEGACY TABLE" and every line between them, with the single statement `return weightGrams * RatePerGram(zone);`.
Keep every other line of the file exactly as it is, including the methods RatePerGram and Describe. Edit no other file.
Then confirm the project builds and the ContosoOrders tests pass.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "LargeBlockRewrite",
        "Delete one long comment block and replace one long block with a single statement, leaving the rest of the file alone",
        "edit tool, exactness",
        "todo-punchlist #12 (edit-tool comparison: ReplaceSnippet vs ReplaceRange vs EditSnippets)",
        new Dictionary<string, string>
        {
            [QuotePath] = SeedSource,
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
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckBlocksRewritten(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    /// <summary>The seed (legacy table and disabled block present) or the golden (both gone, one return statement).</summary>
    private static string BuildSource(bool golden)
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

        if (golden)
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

        if (!golden)
        {
            lines.Add("    // BEGIN DISABLED EXPERIMENT");
            lines.AddRange(DeadCodeLines());
            lines.Add("    // END DISABLED EXPERIMENT");
            lines.Add("");
        }

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

    private static string? MethodText(SyntaxTree tree, string name) =>
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == name)?.ToString();

    /// <summary>
    /// No marker line, legacy-table line or dead-code line remains, and RatePerGram and Describe are byte-identical to the seed.
    /// Scope (this one file only) is enforced by the framework from the step's allowed paths.
    /// </summary>
    private static string? CheckBlocksRewritten(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.Exists(QuotePath))
        {
            return $"missing: {QuotePath}";
        }

        var text = ctx.ReadText(QuotePath);
        foreach (var marker in Markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return $"the marker '{marker}' is still in {QuotePath}";
            }
        }

        if (text.Contains("weightGrams <=", StringComparison.Ordinal))
        {
            return $"lines of the legacy table are still in {QuotePath}";
        }

        if (text.Contains("surcharge +=", StringComparison.Ordinal))
        {
            return $"lines of the disabled experiment are still in {QuotePath}";
        }

        var actualTree = ctx.Syntax(QuotePath);
        var seedTree = CSharpSyntaxTree.ParseText(ctx.SeedFiles[QuotePath]);
        foreach (var name in new[] { "RatePerGram", "Describe" })
        {
            var actual = MethodText(actualTree, name);
            if (actual is null)
            {
                return $"the method {name} is missing from {QuotePath}";
            }

            if (!string.Equals(Normalize(actual), Normalize(MethodText(seedTree, name)!), StringComparison.Ordinal))
            {
                return $"the method {name} differs from the seed (it had to stay exactly as it was)";
            }
        }

        return null;
    }
}
