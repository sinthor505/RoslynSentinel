using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Read-only scenario: review a stale plan against the current code and report. No file may change; the check grades
/// the final message. The golden step has no file changes and its canned final message is a correct review.
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
Anchor review:
1. IWriter.cs:7 holds.
2. Writer.cs:9 moved, Apply is now at Writer.cs:11.
3. CallerA.cs:9 holds.
4. CallerB.cs:5 moved, the first Apply call is now at CallerB.cs:8.
5. CallerC.cs:14 holds.
6. CallerD.cs:9 holds.
Methods: StoreChangedContent no longer exists in Writer.cs; the private method Store replaced it.
Call sites of IWriter.Apply / Writer.Apply: 12 in 5 files (CallerA 2, CallerB 3, CallerC 3, CallerD 2, CallerE 2). The plan's 47 call sites in 9 files is wrong. Archive.Apply called from CallerF is an unrelated method and is not counted.
Step 2 is not possible with zero caller edits: the target-typed new() arguments (CallerB.cs:9, CallerE.cs:8) and the [] argument (CallerC.cs:10) cannot target IReadOnlyDictionary<string, string> (CS0144, CS9174), so those three sites would break.
""";

    private const string StepPrompt = """
The file Evals/PlanStalenessReview/plan.md (at the solution root) is a plan drafted some time ago. The code it
describes is in ContosoOrders.Core/Evals/PlanStalenessReview. Review the plan against the current code and edit
nothing. For each of the six rows of the plan's Anchors table say whether the anchor still holds or give the new
line. Say whether each method the plan names still exists. Count the real direct call sites of IWriter.Apply and
Writer.Apply, in total and per file (count only calls that bind to those methods). Finally say whether the plan's
Step 2, widening the parameter to IReadOnlyDictionary<string, string> with no caller edits, is possible. Give your
review as your final message.
""";

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
                CannedFinalMessage: CannedReview),
        },
        PreferredHost: "113");

    /// <summary>
    /// No file changed; the final message says StoreChangedContent is gone, states the true total (12), names the five caller
    /// files, gives the new lines of the two moved anchors (+-1) in the sentence/line naming each anchor, and says in a negative verdict that the
    /// new() and [] sites break the zero-edit widening (a CS code, or each shape named as breaking; the bare word "error" is not evidence).
    /// A message that says the widening is possible with no caller edits fails.
    /// </summary>
    private static string? CheckReview(ImplementationScenarioCheckContext ctx)
    {
        if (ctx.ChangedPaths.Count > 0)
        {
            return "a read-only review changed files: " + string.Join(", ", ctx.ChangedPaths);
        }

        var message = ctx.FinalMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "no final message with the review";
        }

        if (!message.Contains("StoreChangedContent", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(message, @"no longer|does not exist|doesn't exist|not exist|missing|gone|removed|renamed|absent|not found|cannot find|can't find|no such|not present|replaced", RegexOptions.IgnoreCase))
        {
            return "the review does not say that StoreChangedContent no longer exists";
        }

        if (!Regex.IsMatch(message, @"(?<!\d)12(?!\d)"))
        {
            return "the review does not state the real call-site total (12)";
        }

        foreach (var file in new[] { "CallerA", "CallerB", "CallerC", "CallerD", "CallerE" })
        {
            if (!message.Contains(file, StringComparison.OrdinalIgnoreCase))
            {
                return $"the review does not name {file} among the files with call sites";
            }
        }

        var segments = SplitIntoSegments(message);

        // The anchor's new line must sit in the same sentence/line as the anchor name (a count line such as "CallerB.cs: 3 (lines 8, ...)" does not count).
        if (!MovedAnchorLineIsInContext(segments, @"(?<![A-Za-z])Writer", 10, 12))
        {
            return "the review does not give the new line of the moved Writer.cs anchor (11, +-1) in the sentence or line that names Writer.cs";
        }

        if (!MovedAnchorLineIsInContext(segments, "CallerB", 7, 9))
        {
            return "the review does not give the new line of the moved CallerB.cs anchor (8, +-1) in the sentence or line that names CallerB.cs";
        }

        // The verdict on Step 2 must be negative: no sentence about the widening may assert it works without a denial in the same sentence, and one must deny it.
        var wideningSegments = segments.Where(segment => Regex.IsMatch(segment, WideningClaimPattern, RegexOptions.IgnoreCase)).ToList();
        if (wideningSegments.Any(segment => Regex.IsMatch(segment, AssertsWideningPossiblePattern, RegexOptions.IgnoreCase)
                && !Regex.IsMatch(segment, DeniesWideningPattern, RegexOptions.IgnoreCase)))
        {
            return "the review asserts that the zero-edit widening is possible; the new() and [] call sites break it";
        }

        if (!wideningSegments.Any(segment => Regex.IsMatch(segment, DeniesWideningPattern, RegexOptions.IgnoreCase)))
        {
            return "the review does not say that Step 2 (widening with no caller edits) is not possible";
        }

        // Evidence: both shapes named, and either a CS code or each shape named as breaking (the bare word "error" is not evidence).
        var namesTargetTypedNew = message.Contains("new()", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(message, @"target[- ]typed", RegexOptions.IgnoreCase);
        var namesCollectionExpression = message.Contains("[]", StringComparison.Ordinal)
            || Regex.IsMatch(message, @"collection expression", RegexOptions.IgnoreCase);
        var namesErrorCode = Regex.IsMatch(message, @"CS\d{4}");
        var newNamedAsBreaking = segments.Any(segment => (segment.Contains("new()", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(segment, @"target[- ]typed", RegexOptions.IgnoreCase))
            && Regex.IsMatch(segment, BreakEvidencePattern, RegexOptions.IgnoreCase));
        var collectionNamedAsBreaking = segments.Any(segment => (segment.Contains("[]", StringComparison.Ordinal) || Regex.IsMatch(segment, @"collection expression", RegexOptions.IgnoreCase))
            && Regex.IsMatch(segment, BreakEvidencePattern, RegexOptions.IgnoreCase));
        if (!(namesTargetTypedNew && namesCollectionExpression && (namesErrorCode || (newNamedAsBreaking && collectionNamedAsBreaking))))
        {
            return "the review does not say that the new() and [] call sites break the zero-edit widening (name a CS code, or say each shape breaks)";
        }

        return null;
    }

    // Words that introduce the new position of a moved anchor; the line number must follow the last one in the same sentence/line as the anchor name.
    // The arrow is written as a \u escape so this file stays ASCII.
    private const string MoveKeywordPattern = @"\b(?:moved|shifted|now|new line|changed to|to line|is at)\b|→|->|=>";

    // Sentences/lines that talk about Step 2 (the zero-edit widening).
    private const string WideningClaimPattern = @"step 2|widen|IReadOnlyDictionary|read-only|zero[- ]edit|caller (?:edit|change)|no caller";

    // A widening sentence that denies the zero-edit claim: a negation, a CS code, or "would break" wording, or a leading "No," / "No -".
    private const string DeniesWideningPattern = @"not possible|impossible|isn't possible|cannot|can't|can not|would break|will break|\bbreak(?:s|ing)?\b|not compile|won't compile|does not compile|fails? to compile|CS\d{4}|not without|(?:^|[\s(])no\s*(?:[,:]|-|–|—)(?:\s|$)";

    // A widening sentence that asserts the zero-edit claim holds.
    private const string AssertsWideningPossiblePattern = @"\byes\b[^.\r\n]{0,40}\bpossible|(?<!not )(?<!n't )\bpossible\b[^.\r\n]{0,30}(?:\bwith (?:no|zero)\b|\bwithout\b)|\bno caller (?:edits?|changes?) (?:are |is )?(?:needed|required|necessary)|\bcallers? (?:need|needs|require) no (?:edit|change)|\bbuilds? (?:cleanly )?with (?:0|zero|no) (?:errors|caller)";

    // Evidence that a call-site shape breaks the widening; the bare words "error" and "fail" do not count (a green build says "0 errors").
    private const string BreakEvidencePattern = @"break|cannot|can't|not possible|impossible|not compile|does not compile|won't compile|fails? to compile|no (?:implicit )?conversion|not convert";

    private static List<string> SplitIntoSegments(string message)
    {
        return Regex.Split(message, @"[\r\n]+|(?<=[.!?;])\s+")
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
    }

    /// <summary>
    /// True when one sentence/line names the anchor (matched by <paramref name="anchorNamePattern"/>) and the first number after the
    /// last move keyword in it lies in [fromLine, toLine]. A number elsewhere in the message does not count.
    /// </summary>
    private static bool MovedAnchorLineIsInContext(IReadOnlyList<string> segments, string anchorNamePattern, int fromLine, int toLine)
    {
        foreach (var segment in segments)
        {
            if (!Regex.IsMatch(segment, anchorNamePattern, RegexOptions.IgnoreCase))
            {
                continue;
            }

            var keywords = Regex.Matches(segment, MoveKeywordPattern, RegexOptions.IgnoreCase);
            if (keywords.Count == 0)
            {
                continue;
            }

            var last = keywords[^1];
            var number = Regex.Match(segment[(last.Index + last.Length)..], @"(?<!\d)\d{1,4}(?!\d)");
            if (number.Success && int.TryParse(number.Value, out var line) && line >= fromLine && line <= toLine)
            {
                return true;
            }
        }

        return false;
    }
}
