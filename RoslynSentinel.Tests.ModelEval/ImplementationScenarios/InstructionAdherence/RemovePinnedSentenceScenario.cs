using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Remove one trailing sentence from two bodies of a markdown policy held in a C# raw string literal (PolicyDocument.cs) and from the
/// two test literals that pin them. Both files must end up exactly as the seed minus that sentence, with uniform CRLF.
/// </summary>
internal static class RemovePinnedSentenceScenario
{
    private const string PolicyPath = "ContosoOrders.Core/Evals/RemovePinnedSentence/PolicyDocument.cs";
    private const string TestPath = "ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyBodyTests.cs";

    /// <summary>The sentence to remove, without the single space in front of it.</summary>
    private const string Sentence = "If the task also describes what the symbol does, make one Search or ReadFile in the file the task names to look for a member matching that description before you stop.";

    private const string PolicySource = $$""""
namespace ContosoOrders.Evals.RemovePinnedSentence;

/// <summary>The error policy: one ### section per error kind, the body text on the line under the heading.</summary>
public static class PolicyDocument
{
    public const string Markdown = """
        # Error policy

        Each section below is the body text shown to a caller for one error kind.

        ### FileNotFound.Edit

        The file does not exist. Check the path against the solution listing and retry with the corrected path.

        ### FileNotFound.Lookup

        The file is not part of the loaded solution. List the project's files, pick the closest match and continue.

        ### SymbolNotFound.Edit

        The symbol was not found in the file. Read the file outline, copy the member name exactly and correct it and continue. {{Sentence}}

        ### SymbolNotFound.Lookup

        The symbol is not declared in the project. Search by name, pick the closest member and correct it and continue. {{Sentence}}
        """;
}
"""";

    private const string TestSource = $$"""
using ContosoOrders.Evals.RemovePinnedSentence;
using Xunit;

namespace ContosoOrders.Tests.Evals.RemovePinnedSentence;

public class PolicyBodyTests
{
    private static readonly Dictionary<string, string> ExpectedBodies = new()
    {
        ["FileNotFound.Edit"] = "The file does not exist. Check the path against the solution listing and retry with the corrected path.",
        ["FileNotFound.Lookup"] = "The file is not part of the loaded solution. List the project's files, pick the closest match and continue.",
        ["SymbolNotFound.Edit"] = "The symbol was not found in the file. Read the file outline, copy the member name exactly and correct it and continue. {{Sentence}}",
        ["SymbolNotFound.Lookup"] = "The symbol is not declared in the project. Search by name, pick the closest member and correct it and continue. {{Sentence}}",
    };

    [Fact]
    public void Entries_MatchVerbatimBodiesForEachKey()
    {
        var bodies = ReadBodies();
        Assert.Equal(ExpectedBodies.Count, bodies.Count);
        foreach (var (key, expected) in ExpectedBodies)
        {
            Assert.True(bodies.ContainsKey(key), key);
            Assert.Equal(expected, bodies[key]);
        }
    }

    [Fact]
    public void EveryExpectedBodyEndsWithAFullStop()
    {
        foreach (var (key, body) in ExpectedBodies)
        {
            Assert.True(body.EndsWith('.'), key);
        }
    }

    private static Dictionary<string, string> ReadBodies()
    {
        var bodies = new Dictionary<string, string>();
        string? key = null;
        foreach (var line in PolicyDocument.Markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                key = line[4..].Trim();
            }
            else if (key is not null && line.Length > 0)
            {
                bodies[key] = line;
                key = null;
            }
        }

        return bodies;
    }
}
""";

    private const string EndingTestPath = "ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyEndingTests.cs";

    // Visible acceptance test, red on the seed (both SymbolNotFound bodies still end with the pinned sentence) and compiled against
    // the seed (it only reads PolicyDocument.Markdown). It repeats the body reader of PolicyBodyTests because that one is private.
    private const string EndingTestSource = """
using ContosoOrders.Evals.RemovePinnedSentence;
using Xunit;

namespace ContosoOrders.Tests.Evals.RemovePinnedSentence;

public class PolicyEndingTests
{
    private const string SharedClosing = "correct it and continue.";

    [Fact]
    public void EverySymbolNotFoundBodyEndsWithTheSharedClosing()
    {
        var bodies = ReadBodies()
            .Where(entry => entry.Key.StartsWith("SymbolNotFound.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, bodies.Count);
        foreach (var (key, body) in bodies)
        {
            Assert.True(body.EndsWith(SharedClosing, StringComparison.Ordinal), key + " does not end with: " + SharedClosing);
        }
    }

    private static Dictionary<string, string> ReadBodies()
    {
        var bodies = new Dictionary<string, string>();
        string? key = null;
        foreach (var line in PolicyDocument.Markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                key = line[4..].Trim();
            }
            else if (key is not null && line.Length > 0)
            {
                bodies[key] = line;
                key = null;
            }
        }

        return bodies;
    }
}
""";

    private const string CannedReport = "DONE: removed the pinned sentence and the space before it from both SymbolNotFound bodies in PolicyDocument.cs and PolicyBodyTests.cs.";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "Two files must keep identical body text: the Markdown raw string literal in ContosoOrders.Core/Evals/RemovePinnedSentence/PolicyDocument.cs " +
            "and the `ExpectedBodies` literals in ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyBodyTests.cs. " +
            "In both SymbolNotFound bodies (`SymbolNotFound.Edit` and `SymbolNotFound.Lookup`), in both files, remove this trailing sentence " +
            "together with the single space in front of it, so each body ends at `...correct it and continue.`: " +
            "\"If the task also describes what the symbol does, make one Search or ReadFile in the file the task names to look for a member matching that description before you stop.\"",
        Files: "PolicyDocument.cs and PolicyBodyTests.cs in the folders above. No other file.",
        Conditions: "The FileNotFound bodies stay as they are. The document bodies and the test literals stay identical to each other. " +
            "Both files keep their CRLF line endings.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including PolicyBodyTests and PolicyEndingTests."));

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "RemovePinnedSentence",
        "Remove a pinned sentence from a markdown policy and its test literals",
        "adherence, exactness",
        "ModelEval-Scenario-Ideas/01_remove_sentence_md_and_test_pin.md, commit a3fee1de",
        new Dictionary<string, string>
        {
            [PolicyPath] = PolicySource,
            [TestPath] = TestSource,
            [EndingTestPath] = EndingTestSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [PolicyPath] = RemoveSentence(PolicySource),
                    [TestPath] = RemoveSentence(TestSource),
                },
                AllowedPaths: new List<string> { PolicyPath, TestPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckSentenceRemoved(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "PolicyBodyTests", "PolicyEndingTests" }),
        },
        PreferredHost: "112",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    /// <summary>
    /// The executable outcome table (Spec S8): each variant is an overlay on step 1 (golden) with a DONE: final message, so every
    /// non-passing row is SilentIncomplete. The tests run before the extra check, so both rows fail on the test run.
    /// Table-only (no overlay can express it): a model that keeps the sentence out but writes LF line endings (LfEndings, CheckFailed
    /// through CheckSentenceRemoved). The variant test writes every overlay through ImplementationScenario.NormalizeToCrlf, which turns
    /// LF into CRLF before the check sees it.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // TODO LfEndings variant is table-only: Variant_OutcomeMatchesTable writes overlays through NormalizeToCrlf, so an LF overlay cannot be tested; needs a raw-bytes overlay option on ScenarioVariant
        const string Done = "DONE: removed the sentence from both files.";

        // The policy is edited and the test literals are not: the golden policy over the seed test file.
        var onlyPolicyEdited = new Dictionary<string, string>
        {
            [PolicyPath] = RemoveSentence(PolicySource),
            [TestPath] = TestSource,
        };

        // The sentence is gone from both files but the single space in front of it stays, so each body ends at "continue. ".
        var trailingSpaceKept = new Dictionary<string, string>
        {
            [PolicyPath] = PolicySource.Replace(Sentence, string.Empty),
            [TestPath] = TestSource.Replace(Sentence, string.Empty),
        };

        return new[]
        {
            new ScenarioVariant(
                Name: "OnlyPolicyEdited",
                BaseStep: 1,
                Files: onlyPolicyEdited,
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
            new ScenarioVariant(
                Name: "TrailingSpaceKept",
                BaseStep: 1,
                Files: trailingSpaceKept,
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
        };
    }

    private static string RemoveSentence(string text) => text.Replace(" " + Sentence, string.Empty);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    // The two FileNotFound bodies the task says to leave alone; each must still appear verbatim in both files.
    private static readonly string[] UntouchedBodies =
    [
        "The file does not exist. Check the path against the solution listing and retry with the corrected path.",
        "The file is not part of the loaded solution. List the project's files, pick the closest match and continue.",
    ];

    /// <summary>Null when every line ending in <paramref name="bytes"/> is CRLF; otherwise a short description of the first bare LF or CR.</summary>
    private static string? DescribeNonCrlfLineEnding(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
            {
                return $"a bare LF at byte {i}";
            }

            if (bytes[i] == (byte)'\r' && (i + 1 >= bytes.Length || bytes[i + 1] != (byte)'\n'))
            {
                return $"a bare CR at byte {i}";
            }
        }

        return null;
    }

    /// <summary>Each file must equal its seed minus the sentence (nothing else changed, no stray space), ignoring the final newline.</summary>
    private static string? CheckSentenceRemoved(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { PolicyPath, TestPath })
        {
            if (!ctx.Exists(path))
            {
                return $"missing: {path}";
            }

            var lineEndingProblem = DescribeNonCrlfLineEnding(ctx.ReadBytes(path));
            if (lineEndingProblem is not null)
            {
                return $"{path} no longer has uniform CRLF line endings: {lineEndingProblem}";
            }

            var actual = Normalize(ctx.ReadText(path));
            if (actual.Contains("If the task also describes", StringComparison.Ordinal))
            {
                return $"the sentence is still in {path}";
            }

            foreach (var body in UntouchedBodies)
            {
                if (!actual.Contains(body, StringComparison.Ordinal))
                {
                    return $"a FileNotFound body that had to stay unchanged is missing or altered in {path}: {body}";
                }
            }

            var expected = Normalize(RemoveSentence(ctx.SeedFiles[path]));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return $"{path} differs from the seed minus the sentence (something else changed, or a stray space remains)";
            }
        }

        return null;
    }
}
