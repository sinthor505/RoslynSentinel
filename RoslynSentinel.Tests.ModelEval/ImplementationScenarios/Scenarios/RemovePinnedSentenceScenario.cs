namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Remove one trailing sentence from two bodies of a markdown policy file and from the two test literals that pin them.
/// Both files must end up exactly as the seed minus that sentence.
/// </summary>
internal static class RemovePinnedSentenceScenario
{
    private const string PolicyPath = "Evals/RemovePinnedSentence/policy.md";
    private const string TestPath = "ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyBodyTests.cs";

    /// <summary>The sentence to remove, without the single space in front of it.</summary>
    private const string Sentence = "If the task also describes what the symbol does, make one Search or ReadFile in the file the task names to look for a member matching that description before you stop.";

    private const string PolicySource = $$"""
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

    private const string TestSource = $$"""
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
        foreach (var line in File.ReadAllLines(FindPolicyFile()))
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

    private static string FindPolicyFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Evals", "RemovePinnedSentence", "policy.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("policy.md was not found above the test assembly.");
    }
}
""";

    private const string StepPrompt = """
Two files must keep identical body text: Evals/RemovePinnedSentence/policy.md (at the solution root) and the
ExpectedBodies literals in ContosoOrders.Tests/Evals/RemovePinnedSentence/PolicyBodyTests.cs. In both
SymbolNotFound bodies (SymbolNotFound.Edit and SymbolNotFound.Lookup), in both files, remove this trailing sentence
together with the single space in front of it, so each body ends at "...correct it and continue.":
"If the task also describes what the symbol does, make one Search or ReadFile in the file the task names to look for a member matching that description before you stop."
Change nothing else: the FileNotFound bodies stay as they are, and the md bodies and the test literals must stay
identical to each other.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "RemovePinnedSentence",
        "Remove a pinned sentence from a markdown policy and its test literals",
        "adherence, exactness",
        "ModelEval-Scenario-Ideas/01_remove_sentence_md_and_test_pin.md, commit a3fee1de",
        new Dictionary<string, string>
        {
            [PolicyPath] = PolicySource,
            [TestPath] = TestSource,
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
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    private static string RemoveSentence(string text) => text.Replace(" " + Sentence, string.Empty);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Each file must equal its seed minus the sentence (nothing else changed, no stray space), ignoring the final newline.</summary>
    private static string? CheckSentenceRemoved(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { PolicyPath, TestPath })
        {
            if (!ctx.Exists(path))
            {
                return $"missing: {path}";
            }

            var actual = Normalize(ctx.ReadText(path));
            if (actual.Contains("If the task also describes", StringComparison.Ordinal))
            {
                return $"the sentence is still in {path}";
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
