using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.AmbiguityResolution;

/// <summary>
/// Read-only scenario: review a stale plan against the current code and report. No file may change (the step allows no path, so
/// the scope check grades an edit). The check parses the final message's Report lines (ScenarioReport) and compares each field
/// to a constant derived from the seed: the six anchors, the three methods, both call totals with their per-file counts, and the
/// Step 2 verdict; the Step 2 reason is not graded. The golden step has no file changes and its canned final message is the
/// correct Report; the Variants cover the outcome table.
/// </summary>
internal static class PlanStalenessReviewScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/PlanStalenessReview/";
    private const string PlanPath = "Evals/PlanStalenessReview/plan.md";

    private const string PlanSource = """
# Plan: widen the change-set parameter of IWriter.Apply

**Status:** DRAFT 2026-10-14, not started.

## Problem

`IWriter.Apply` (`ContosoOrders.Core/Evals/PlanStalenessReview/IWriter.cs:7`) takes a mutable
`Dictionary<string, string>` although the writer only ever reads the map. The only implementation,
`Writer.Apply` (`Writer.cs:9`), copies the entries into its store through the private method
`Writer.StoreChangedContent`, which converts every key to a string first.

There are 47 call sites in 9 files. Every one of them builds a plain `Dictionary<string, string>`
and passes it, so none of them needs the mutable type.

## Anchors

Line numbers were taken from the code on the draft date.

| # | Anchor | What it points at |
| --- | --- | --- |
| 1 | `IWriter.cs:7` | the `Apply` declaration on the interface |
| 2 | `Writer.cs:9` | the `Apply` implementation |
| 3 | `CallerA.cs:9` | the first call in `CallerA` |
| 4 | `CallerB.cs:5` | the first call in `CallerB` |
| 5 | `CallerC.cs:14` | a call with a pending map |
| 6 | `CallerD.cs:9` | a call with a map the caller built |

## Steps

### Step 1 - Pin the current behaviour

Add one test that applies a two-entry map through `Writer` and reads `StoredCount`.

### Step 2 - Widen the parameter

Change the parameter of `IWriter.Apply` and `Writer.Apply` to `IReadOnlyDictionary<string, string>`.
Callers need no edit, because a `Dictionary<string, string>` converts to the read-only interface
implicitly. Build after this step; it must compile with no change in any caller file.

### Step 3 - Simplify the store

Remove the string-key conversion in `StoreChangedContent` and store the keys as they are.

### Step 4 - Document

Update the XML comment of `IWriter.Apply` to say the map is only read.

## Done when

- the solution builds with no caller edited
- the test from step 1 passes
""";

    private const string IWriterSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies a change set (path to new content) to a store.</summary>
public interface IWriter
{
    /// <summary>Applies every entry of <paramref name="changes"/>.</summary>
    void Apply(Dictionary<string, string> changes);
}
""";

    private const string WriterSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Keeps applied changes in memory.</summary>
public sealed class Writer : IWriter
{
    private readonly Dictionary<string, string> _store = new();

    public int StoredCount => _store.Count;

    /// <summary>Copies every entry of <paramref name="changes"/> into the store.</summary>
    public void Apply(Dictionary<string, string> changes)
    {
        foreach (var (path, content) in changes)
        {
            Store(path, content);
        }
    }

    private void Store(string path, string content)
    {
        _store[path] = content;
    }
}
""";

    private const string CallerASource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies two fixed batches.</summary>
public static class CallerA
{
    public static void Run(IWriter writer)
    {
        var first = new Dictionary<string, string> { ["a.txt"] = "one" };
        writer.Apply(first);
        var second = new Dictionary<string, string> { ["b.txt"] = "two" };
        writer.Apply(second);
    }
}
""";

    private const string CallerBSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies batches built inline.</summary>
public static class CallerB
{
    public static void Run(IWriter writer)
    {
        writer.Apply(new Dictionary<string, string> { ["c.txt"] = "three" });
        writer.Apply(new() { ["d.txt"] = "four" });
        var extra = new Dictionary<string, string>();
        extra["e.txt"] = "five";
        writer.Apply(extra);
    }
}
""";

    private const string CallerCSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies conditionally.</summary>
public static class CallerC
{
    public static void Run(IWriter writer, bool flush)
    {
        if (flush)
        {
            writer.Apply([]);
        }

        var pending = new Dictionary<string, string> { ["f.txt"] = "six" };
        writer.Apply(pending);
        writer.Apply(pending);
    }
}
""";

    private const string CallerDSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies the same batch twice through the concrete writer.</summary>
public static class CallerD
{
    public static void Run(Writer writer)
    {
        var h = new Dictionary<string, string> { ["g.txt"] = "seven" };
        writer.Apply(h);
        writer.Apply(h);
    }
}
""";

    private const string CallerESource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Applies through the concrete writer.</summary>
public static class CallerE
{
    public static void Run(Writer writer)
    {
        writer.Apply(new() { ["i.txt"] = "eight" });
        writer.Apply(new Dictionary<string, string>());
    }
}
""";

    private const string ArchiveSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>An unrelated archive that happens to have its own Apply method.</summary>
public sealed class Archive
{
    public int Count { get; private set; }

    public void Apply(int entries) => Count += entries;
}
""";

    private const string CallerFSource = """
namespace ContosoOrders.Evals.PlanStalenessReview;

/// <summary>Feeds the archive.</summary>
public static class CallerF
{
    public static void Run(Archive archive)
    {
        archive.Apply(3);
        archive.Apply(4);
    }
}
""";

    private const string CannedReview = """
DONE: reviewed the plan against the current code; two anchors moved, StoreChangedContent is gone, and Step 2 is not possible.
Anchor 1: holds
Anchor 2: line 11
Anchor 3: holds
Anchor 4: line 8
Anchor 5: holds
Anchor 6: holds
Method IWriter.Apply: exists
Method Writer.Apply: exists
Method Writer.StoreChangedContent: gone
Calls IWriter.Apply: 8; CallerA=2, CallerB=3, CallerC=3
Calls Writer.Apply: 4; CallerD=2, CallerE=2
Step 2: not possible
Step 2 reason: the target-typed new() arguments and the [] argument cannot target IReadOnlyDictionary<string, string> (CS0144, CS9174), so those sites would need a caller edit.
""";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "The plan Evals/PlanStalenessReview/plan.md (at the solution root) was drafted some time ago. " +
            "The code it describes is in ContosoOrders.Core/Evals/PlanStalenessReview. " +
            "Check the plan against the current code and tell the owner what is stale. Implement nothing.",
        Files: "none (change no file).",
        Conditions: "",
        IfFails: "Change no file. If you cannot answer a Report line, write `unknown` and why.",
        DoneWhen: "Every Report line is answered from the current code, and no file changed. " +
            "Count only calls that bind to `IWriter.Apply` or `Writer.Apply`, not other methods named Apply.",
        Report: "One line each, exactly in this form. " +
            "`Anchor <n>: holds` or `Anchor <n>: line <N>` for each of the six rows of the plan's Anchors table. " +
            "`Method <Type.Name>: exists` or `Method <Type.Name>: gone` for each method the plan names. " +
            "`Calls IWriter.Apply: <total>; <File>=<n>, ...` and `Calls Writer.Apply: <total>; <File>=<n>, ...` (real direct call sites, total and per file; `<File>` is the file name without its folder). " +
            "`Step 2: possible` or `Step 2: not possible` for the plan's Step 2 (widening the parameter to `IReadOnlyDictionary<string, string>` with no caller edits). " +
            "`Step 2 reason: <one sentence>`."));

    /// <summary>
    /// The executable outcome table (Spec S1): every variant is the canned review with one small edit (or the review plus an edit of
    /// Writer.cs) on the seed (base step 0, which is also the golden tree). All grade as a DONE: or BLOCKED: end of a review step.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Throws when the canned review no longer contains the text to edit, so a change to it cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText) =>
            text.Contains(oldText, StringComparison.Ordinal)
                ? text.Replace(oldText, newText, StringComparison.Ordinal)
                : throw new InvalidOperationException($"PlanStalenessReview variant edit: '{oldText}' is not in the canned review.");

        // Row 7 of the table (edits then reverts) leaves no changed path, so it is the golden and needs no variant.
        return new[]
        {
            // Row 2: no label lines, so the first field the check looks for (Anchor 1) is missing.
            new ScenarioVariant(
                Name: "ProseReview",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: "DONE: all anchors hold except two; StoreChangedContent is gone; IWriter.Apply is called 8 times and Writer.Apply 4 times; " +
                    "Step 2 is not possible because the new() and [] arguments would need a caller edit.",
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "Anchor 1"),
            // Row 3: every Apply call counted, Archive.Apply (two calls in CallerF) included, 12 + 2 = 14.
            new ScenarioVariant(
                Name: "ArchiveCounted",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: Edited(
                    CannedReview,
                    "Calls IWriter.Apply: 8; CallerA=2, CallerB=3, CallerC=3",
                    "Calls IWriter.Apply: 14; CallerA=2, CallerB=3, CallerC=3, CallerD=2, CallerE=2, CallerF=2"),
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "field 'Calls IWriter.Apply'"),
            // Row 4: an anchor that still holds reported as moved.
            new ScenarioVariant(
                Name: "HoldingAnchorMoved",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: Edited(CannedReview, "Anchor 1: holds", "Anchor 1: line 9"),
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "field 'Anchor 1'"),
            // Row 5.
            new ScenarioVariant(
                Name: "StepTwoPossible",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: Edited(CannedReview, "Step 2: not possible", "Step 2: possible"),
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "field 'Step 2'"),
            // Row 6: the reason is recorded as a metric, not graded.
            new ScenarioVariant(
                Name: "StepTwoReasonOnly",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: Edited(CannedReview, "Step 2 reason: the target-typed new() arguments", "Step 2 reason: the collection-expression and null-literal arguments"),
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            // Row 9: the extra check still runs on a BLOCKED end and finds no Report fields; the class is FalseBlocked from the token.
            new ScenarioVariant(
                Name: "BlockedReview",
                BaseStep: 0,
                Files: new Dictionary<string, string>(),
                FinalMessage: "BLOCKED: the plan cannot be checked against the code.",
                ExpectedClass: EvalOutcomeClass.FalseBlocked,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "Anchor 1"),
            // Row 8: the right review, but Writer.cs was edited and left; the step allows no path, so the scope check grades it.
            new ScenarioVariant(
                Name: "EditedAndLeft",
                BaseStep: 0,
                Files: new Dictionary<string, string> { [CoreFolder + "Writer.cs"] = WriterSource + "\n// left behind by the review\n" },
                FinalMessage: CannedReview,
                ExpectedClass: EvalOutcomeClass.Bypass,
                ExpectedOutcome: ImplementationScenarioOutcome.OutOfScopeEdit,
                DetailContains: "Writer.cs changed but is outside the allowed paths"),
        };
    }

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "PlanStalenessReview",
        "Review a stale plan against the current code without editing",
        "read-only, ambiguity",
        "DCS dispatches/main_01_Review-DocumentChangeSet-plan-vs-code, reports/explore-tier2-agent-aa1498aa8fa03396f",
        new Dictionary<string, string>
        {
            [PlanPath] = PlanSource,
            [CoreFolder + "IWriter.cs"] = IWriterSource,
            [CoreFolder + "Writer.cs"] = WriterSource,
            [CoreFolder + "CallerA.cs"] = CallerASource,
            [CoreFolder + "CallerB.cs"] = CallerBSource,
            [CoreFolder + "CallerC.cs"] = CallerCSource,
            [CoreFolder + "CallerD.cs"] = CallerDSource,
            [CoreFolder + "CallerE.cs"] = CallerESource,
            [CoreFolder + "Archive.cs"] = ArchiveSource,
            [CoreFolder + "CallerF.cs"] = CallerFSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>(),
                AllowedPaths: new List<string>(),
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckReview(ctx)),
                CannedFinalMessage: CannedReview,
                Variants: BuildVariants()),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.AmbiguityResolution);

    /// <summary>
    /// Grades the Report block of the final message field by field against the expected values below (the six anchors, the three
    /// methods, both call lists with their per-file counts, the Step 2 verdict). A missing field fails with its name, a wrong one
    /// with the field, the expected and the actual value. `Step 2 reason` is not graded. A changed file is not judged here: the
    /// step allows no path, so the shared scope check reports it as an out-of-scope edit.
    /// </summary>
    private static string? CheckReview(ImplementationScenarioCheckContext ctx)
    {
        var fields = ScenarioReport.Parse(ctx.FinalMessage);
        for (var index = 0; index < ExpectedAnchors.Length; index++)
        {
            var failure = CheckField(fields, $"Anchor {index + 1}", ExpectedAnchors[index]);
            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var (label, expected) in ExpectedMethods)
        {
            var failure = CheckField(fields, label, expected);
            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var (label, total, perFile) in ExpectedCalls)
        {
            var failure = CheckCalls(fields, label, total, perFile);
            if (failure is not null)
            {
                return failure;
            }
        }

        return CheckField(fields, "Step 2", ExpectedStepTwo);
    }

    // The expected Report values, derived from the seed: anchors 2 (Writer.cs) and 4 (CallerB.cs) are the only ones that moved.
    private static readonly string[] ExpectedAnchors = { "holds", "line 11", "holds", "line 8", "holds", "holds" };

    private static readonly (string Label, string Expected)[] ExpectedMethods =
    {
        ("Method IWriter.Apply", "exists"),
        ("Method Writer.Apply", "exists"),
        ("Method Writer.StoreChangedContent", "gone"),
    };

    // Real direct call sites per file; CallerF calls Archive.Apply, which is a different method and is not counted.
    private static readonly (string Label, int Total, IReadOnlyDictionary<string, int> PerFile)[] ExpectedCalls =
    {
        ("Calls IWriter.Apply", 8, new Dictionary<string, int> { ["CallerA"] = 2, ["CallerB"] = 3, ["CallerC"] = 3 }),
        ("Calls Writer.Apply", 4, new Dictionary<string, int> { ["CallerD"] = 2, ["CallerE"] = 2 }),
    };

    private const string ExpectedStepTwo = "not possible";

    private static string? CheckField(IReadOnlyDictionary<string, string> fields, string label, string expected)
    {
        if (!fields.TryGetValue(label, out var actual))
        {
            return $"the Report has no '{label}' line (expected '{label}: {expected}')";
        }

        return string.Equals(NormalizeValue(actual), expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"field '{label}': expected '{expected}', got '{actual}'";
    }

    private static string? CheckCalls(IReadOnlyDictionary<string, string> fields, string label, int expectedTotal, IReadOnlyDictionary<string, int> expectedPerFile)
    {
        var expectedText = $"{expectedTotal}; " + string.Join(", ", expectedPerFile.Select(pair => $"{pair.Key}={pair.Value}"));
        if (!fields.TryGetValue(label, out var actual))
        {
            return $"the Report has no '{label}' line (expected '{label}: {expectedText}')";
        }

        return TryParseCalls(actual, out var total, out var perFile)
            && total == expectedTotal
            && perFile.Count == expectedPerFile.Count
            && expectedPerFile.All(pair => perFile.TryGetValue(pair.Key, out var count) && count == pair.Value)
            ? null
            : $"field '{label}': expected '{expectedText}', got '{actual}'";
    }

    // Reads "<total>; <File>=<n>, <File>=<n>": spaces and an optional .cs suffix on the file label are tolerated, file labels compare case-insensitively.
    private static bool TryParseCalls(string value, out int total, out Dictionary<string, int> perFile)
    {
        perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parts = NormalizeValue(value).Split(';', 2);
        if (!int.TryParse(parts[0].Trim(), out total))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        foreach (var entry in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            var file = pair[0].EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? pair[0][..^3] : pair[0];
            if (pair.Length != 2 || !int.TryParse(pair[1], out var count) || !perFile.TryAdd(file, count))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeValue(string value) => value.Trim().TrimEnd('.').Trim();
}
