using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Pins the output format of <see cref="DiffEngine.CreateDiff"/>, which backs the returnDiff
/// preview of ReplaceSnippet (single and batch) and the other mutating tools: ONE hunk per
/// contiguous gap, header <c>@@ -oldStart,oldCount +newStart,newCount @@</c>, all removed lines
/// then all added lines, "\n" line ends throughout.
/// </summary>
[TestFixture]
public class DiffEngineCreateDiffTests
{
    private const string Preamble = "--- Original\n+++ Modified\n";

    private static readonly Regex HeaderRegex = new(@"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@$");
    private static readonly Regex LineSplitRegex = new(@"\r\n|\r|\n");

    private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount)
    {
        public List<string> Removed { get; } = new();
        public List<string> Added { get; } = new();

        // 0-based index of the first affected line; a side with count 0 names the line BEFORE the gap.
        public int OldBegin => OldCount == 0 ? OldStart : OldStart - 1;
        public int NewBegin => NewCount == 0 ? NewStart : NewStart - 1;
    }

    private static List<Hunk> ParseHunks(string diff)
    {
        Assert.That(diff, Does.StartWith(Preamble));
        Assert.That(diff, Does.Not.Contain("\r"), "diff text must use \\n only");

        var hunks = new List<Hunk>();
        Hunk? current = null;
        bool seenAdded = false;
        foreach (var line in diff[Preamble.Length..].Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var match = HeaderRegex.Match(line);
            if (match.Success)
            {
                current = new Hunk(
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value),
                    int.Parse(match.Groups[4].Value));
                hunks.Add(current);
                seenAdded = false;
            }
            else if (current == null)
            {
                Assert.Fail($"Body line before any hunk header: {line}");
            }
            else if (line[0] == '-')
            {
                Assert.That(seenAdded, Is.False, "a '-' line followed a '+' line inside one hunk");
                current.Removed.Add(line[1..]);
            }
            else if (line[0] == '+')
            {
                seenAdded = true;
                current.Added.Add(line[1..]);
            }
            else
            {
                Assert.Fail($"Unexpected diff line: {line}");
            }
        }

        return hunks;
    }

    /// <summary>
    /// Creates the diff and checks the invariants every diff must satisfy: header counts match the
    /// body, hunks never touch, and replaying the hunks over the old text yields the new text.
    /// </summary>
    private static List<Hunk> CreateAndVerify(string oldText, string newText)
    {
        var diff = DiffEngine.CreateDiff(oldText, newText);
        var hunks = ParseHunks(diff);

        var oldLines = LineSplitRegex.Split(oldText);
        var newLines = LineSplitRegex.Split(newText);

        var result = new List<string>();
        int pos = 0;
        Hunk? previous = null;
        foreach (var hunk in hunks)
        {
            Assert.That(hunk.Removed, Has.Count.EqualTo(hunk.OldCount), "old count must match the '-' lines");
            Assert.That(hunk.Added, Has.Count.EqualTo(hunk.NewCount), "new count must match the '+' lines");
            Assert.That(hunk.OldCount + hunk.NewCount, Is.GreaterThan(0), "a hunk must change something");

            if (previous != null)
            {
                Assert.That(hunk.OldBegin, Is.GreaterThan(previous.OldBegin + previous.OldCount), "hunks must not touch (old side)");
                Assert.That(hunk.NewBegin, Is.GreaterThan(previous.NewBegin + previous.NewCount), "hunks must not touch (new side)");
            }

            Assert.That(hunk.OldBegin, Is.GreaterThanOrEqualTo(pos));
            result.AddRange(oldLines.Skip(pos).Take(hunk.OldBegin - pos));
            Assert.That(result, Has.Count.EqualTo(hunk.NewBegin), "new start must point at the replayed position");
            Assert.That(hunk.Removed, Is.EqualTo(oldLines.Skip(hunk.OldBegin).Take(hunk.OldCount).ToList()));
            result.AddRange(hunk.Added);
            pos = hunk.OldBegin + hunk.OldCount;
            previous = hunk;
        }

        result.AddRange(oldLines.Skip(pos));
        Assert.That(result, Is.EqualTo(newLines), "replaying the hunks over the old text must give the new text");
        return hunks;
    }

    private static string Join(params string[] lines) => string.Join("\n", lines);

    [Test]
    public void CreateDiff_IdenticalInput_ReturnsPreambleOnly()
    {
        var diff = DiffEngine.CreateDiff(Join("a", "b", "c"), Join("a", "b", "c"));

        Assert.That(diff, Is.EqualTo(Preamble));
    }

    [Test]
    public void CreateDiff_SingleLineChange_EmitsOneOneByOneHunk()
    {
        var diff = DiffEngine.CreateDiff(Join("a", "b", "c"), Join("a", "X", "c"));

        Assert.That(diff, Is.EqualTo(Preamble + "@@ -2,1 +2,1 @@\n-b\n+X\n"));
        CreateAndVerify(Join("a", "b", "c"), Join("a", "X", "c"));
    }

    [Test]
    public void CreateDiff_TwoRegionEdit_EmitsExactlyTwoHunksWithCorrectCounts()
    {
        var oldText = Join("a", "b", "c", "d", "e", "f");
        var newText = Join("a", "BC", "d", "E1", "E2", "f");

        var diff = DiffEngine.CreateDiff(oldText, newText);

        Assert.That(diff, Is.EqualTo(
            Preamble +
            "@@ -2,2 +2,1 @@\n-b\n-c\n+BC\n" +
            "@@ -5,1 +4,2 @@\n-e\n+E1\n+E2\n"));
        Assert.That(CreateAndVerify(oldText, newText), Has.Count.EqualTo(2));
    }

    [Test]
    public void CreateDiff_PureInsertion_UsesLineBeforeGapAndZeroOldCount()
    {
        var diff = DiffEngine.CreateDiff(Join("a", "b"), Join("a", "X", "b"));

        Assert.That(diff, Is.EqualTo(Preamble + "@@ -1,0 +2,1 @@\n+X\n"));
        CreateAndVerify(Join("a", "b"), Join("a", "X", "b"));
    }

    [Test]
    public void CreateDiff_InsertionAtTopOfFile_UsesStartZero()
    {
        var diff = DiffEngine.CreateDiff("a", Join("X", "a"));

        Assert.That(diff, Is.EqualTo(Preamble + "@@ -0,0 +1,1 @@\n+X\n"));
        CreateAndVerify("a", Join("X", "a"));
    }

    [Test]
    public void CreateDiff_PureDeletion_UsesLineBeforeGapAndZeroNewCount()
    {
        var diff = DiffEngine.CreateDiff(Join("a", "X", "b"), Join("a", "b"));

        Assert.That(diff, Is.EqualTo(Preamble + "@@ -2,1 +1,0 @@\n-X\n"));
        CreateAndVerify(Join("a", "X", "b"), Join("a", "b"));
    }

    [Test]
    public void CreateDiff_ThreeLinesReplacedByThreeDifferentPlusOne_IsOneHunkWithAllRemovalsFirst()
    {
        var oldText = Join("h", "x1", "x2", "x3", "t");
        var newText = Join("h", "y1", "y2", "y3", "y4", "t");

        var diff = DiffEngine.CreateDiff(oldText, newText);

        Assert.That(diff, Is.EqualTo(
            Preamble + "@@ -2,3 +2,4 @@\n-x1\n-x2\n-x3\n+y1\n+y2\n+y3\n+y4\n"));
        Assert.That(CreateAndVerify(oldText, newText), Has.Count.EqualTo(1));
    }

    [Test]
    public void CreateDiff_RepeatedBraceAndBlankLinesInsideGaps_StillOneHunkPerGap()
    {
        var oldText = Join("void M()", "{", "    One();", "    Two();", "}", "", "void N()", "{", "    Three();", "}");
        var newText = Join("void M()", "{", "    Uno();", "}", "", "void N()", "{", "    Tres();", "    Quatro();", "}");

        var diff = DiffEngine.CreateDiff(oldText, newText);

        Assert.That(diff, Is.EqualTo(
            Preamble +
            "@@ -3,2 +3,1 @@\n-    One();\n-    Two();\n+    Uno();\n" +
            "@@ -9,1 +8,2 @@\n-    Three();\n+    Tres();\n+    Quatro();\n"));
        CreateAndVerify(oldText, newText);
    }

    [Test]
    public void CreateDiff_CrLfVersusLfInput_ProducesNoSpuriousHunks()
    {
        Assert.That(
            DiffEngine.CreateDiff("a\r\nb\r\nc\r\n", "a\nb\nc\n"),
            Is.EqualTo(Preamble));

        var diff = DiffEngine.CreateDiff("a\r\nb\r\nc\r\n", "a\nX\nc\n");

        Assert.That(diff, Is.EqualTo(Preamble + "@@ -2,1 +2,1 @@\n-b\n+X\n"));
    }

    [Test]
    public void CreateDiff_LargeDifferingRegionBeyondLcsCap_FallsBackToOneHunkForTheWholeMiddle()
    {
        // 4001 x 4001 = 16_008_001 cells, just over the 16_000_000 LCS cap, so the fallback path
        // runs. No line is shared, so no table is ever allocated and this stays cheap.
        const int count = 4001;
        var oldLines = new List<string> { "head" };
        var newLines = new List<string> { "head" };
        for (int i = 0; i < count; i++)
        {
            oldLines.Add("o" + i);
            newLines.Add("n" + i);
        }
        oldLines.Add("tail");
        newLines.Add("tail");
        var oldText = string.Join("\n", oldLines);
        var newText = string.Join("\n", newLines);

        var diff = DiffEngine.CreateDiff(oldText, newText);

        Assert.That(diff, Does.StartWith(Preamble + "@@ -2,4001 +2,4001 @@\n-o0\n-o1\n"));
        var hunks = CreateAndVerify(oldText, newText);
        Assert.That(hunks, Has.Count.EqualTo(1));
        Assert.That(hunks[0].Removed, Has.Count.EqualTo(count));
        Assert.That(hunks[0].Added, Has.Count.EqualTo(count));
    }

    [Test]
    public void CreateDiff_RandomRepetitiveInputs_AlwaysRoundTripAndNeverEmitTouchingHunks()
    {
        var alphabet = new[] { "}", "", "a", "b", "c" };
        var random = new Random(12345);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            string MakeText()
            {
                var length = random.Next(0, 13);
                return string.Join("\n", Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]));
            }

            CreateAndVerify(MakeText(), MakeText());
        }
    }
}
