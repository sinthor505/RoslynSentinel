using System.Diagnostics;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// FileLineChange.Compute reports the per-file added/removed line counts of a write. These tests pin that the counts are exact for
// sparse edits at any file size (a few changed lines in a 5000+ line file must not be reported as thousands), that the line
// terminator is part of the comparison, and that a pathological rewrite still returns quickly with an over-report.
[TestFixture]
public class FileLineChangeTests
{
    // Builds a text of `count` lines ("line 0", "line 1", ...) with the given terminator, replacing the lines named in `overrides`.
    private static string Text(int count, string terminator = "\r\n", IReadOnlyDictionary<int, string>? overrides = null)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            builder.Append(overrides is not null && overrides.TryGetValue(i, out var replacement) ? replacement : "line " + i).Append(terminator);
        }

        return builder.ToString();
    }

    [Test]
    public void Compute_IdenticalText_ReportsNothing()
    {
        var text = Text(50);
        var change = FileLineChange.Compute("A.cs", text, text);

        Assert.That(change.LinesAdded, Is.EqualTo(0));
        Assert.That(change.LinesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Compute_NewFile_CountsEveryLineAsAdded()
    {
        var change = FileLineChange.Compute("A.cs", null, Text(7));

        Assert.That(change.LinesAdded, Is.EqualTo(7));
        Assert.That(change.LinesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Compute_DeletedFile_CountsEveryLineAsRemoved()
    {
        var change = FileLineChange.Compute("A.cs", Text(7), string.Empty);

        Assert.That(change.LinesAdded, Is.EqualTo(0));
        Assert.That(change.LinesRemoved, Is.EqualTo(7));
    }

    [Test]
    public void Compute_OneLineChangedInTheMiddle_ReportsOneAddedOneRemoved()
    {
        var before = Text(40);
        var after = Text(40, overrides: new Dictionary<int, string> { [20] = "changed" });
        var change = FileLineChange.Compute("A.cs", before, after);

        Assert.That(change.LinesAdded, Is.EqualTo(1));
        Assert.That(change.LinesRemoved, Is.EqualTo(1));
    }

    [Test]
    public void Compute_TwoDistantEditsInALargeFile_ReportsExactlyTwoLines()
    {
        // The differing middle spans ~5800 lines, so a table-based LCS over it exceeded its size cap and reported the whole span.
        var before = Text(6000);
        var after = Text(6000, overrides: new Dictionary<int, string> { [100] = "changed 100", [5900] = "changed 5900" });
        var change = FileLineChange.Compute("A.cs", before, after);

        Assert.That(change.LinesAdded, Is.EqualTo(2));
        Assert.That(change.LinesRemoved, Is.EqualTo(2));
    }

    [Test]
    public void Compute_ManySparseEditsInALargeFile_ReportsExactlyTheEditedLines()
    {
        // Every 25th line of a 9000-line file changes: 360 lines, spread over the whole file.
        var overrides = Enumerable.Range(0, 9000).Where(i => i % 25 == 7).ToDictionary(i => i, i => "changed " + i);
        var change = FileLineChange.Compute("A.cs", Text(9000), Text(9000, overrides: overrides));

        Assert.That(change.LinesAdded, Is.EqualTo(overrides.Count));
        Assert.That(change.LinesRemoved, Is.EqualTo(overrides.Count));
    }

    [Test]
    public void Compute_DistantInsertionAndDeletionInALargeFile_AreCountedSeparately()
    {
        var beforeLines = Enumerable.Range(0, 6000).Select(i => "line " + i).ToList();
        var afterLines = new List<string>(beforeLines);
        afterLines.RemoveAt(100);
        afterLines.Insert(5000, "inserted a");
        afterLines.Insert(5001, "inserted b");
        var change = FileLineChange.Compute("A.cs", string.Concat(beforeLines.Select(l => l + "\r\n")), string.Concat(afterLines.Select(l => l + "\r\n")));

        Assert.That(change.LinesRemoved, Is.EqualTo(1));
        Assert.That(change.LinesAdded, Is.EqualTo(2));
    }

    [Test]
    public void Compute_RepeatedLinesAreMatchedAsALongestCommonSubsequence()
    {
        // Duplicate lines ("{" / "}" / blank) are everywhere in source; the count must still be the minimal edit.
        var change = FileLineChange.Compute("A.cs", "{\n}\n{\n}\n{\n}\n", "{\n}\n{\nx\n}\n{\n}\n");

        Assert.That(change.LinesAdded, Is.EqualTo(1));
        Assert.That(change.LinesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Compute_WholeFileEolRewrite_ReportsEveryLineChanged()
    {
        // Terminators are part of the comparison, so a CRLF to LF rewrite is not invisible, at any size.
        var change = FileLineChange.Compute("A.cs", Text(6000), Text(6000, "\n"));

        Assert.That(change.LinesAdded, Is.EqualTo(6000));
        Assert.That(change.LinesRemoved, Is.EqualTo(6000));
    }

    [Test]
    public void Compute_SmallWholeFileEolRewrite_ReportsEveryLineChanged()
    {
        var change = FileLineChange.Compute("A.cs", Text(30), Text(30, "\n"));

        Assert.That(change.LinesAdded, Is.EqualTo(30));
        Assert.That(change.LinesRemoved, Is.EqualTo(30));
    }

    [Test]
    public void Compute_TotallyDifferentLargeFiles_ReturnsQuicklyWithTheFullCounts()
    {
        var before = Text(6000);
        var after = string.Concat(Enumerable.Range(0, 6000).Select(i => "other " + i + "\r\n"));
        var stopwatch = Stopwatch.StartNew();
        var change = FileLineChange.Compute("A.cs", before, after);
        stopwatch.Stop();

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "A pathological rewrite must return in bounded time");
        // No line is shared, so the exact answer and the over-report are both 6000/6000.
        Assert.That(change.LinesAdded, Is.EqualTo(6000));
        Assert.That(change.LinesRemoved, Is.EqualTo(6000));
    }

    [Test]
    public void Compute_ModeratelyDifferentLargeFiles_NeverUnderReports()
    {
        // 6000 lines where two thirds change: whether exact or over-reported, the counts must cover the real edit.
        var overrides = Enumerable.Range(0, 6000).Where(i => i % 3 != 0).ToDictionary(i => i, i => "changed " + i);
        var stopwatch = Stopwatch.StartNew();
        var change = FileLineChange.Compute("A.cs", Text(6000), Text(6000, overrides: overrides));
        stopwatch.Stop();

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        Assert.That(change.LinesAdded, Is.GreaterThanOrEqualTo(overrides.Count));
        Assert.That(change.LinesRemoved, Is.GreaterThanOrEqualTo(overrides.Count));
        Assert.That(change.LinesAdded, Is.EqualTo(change.LinesRemoved), "Same line count on both sides");
    }

    [Test]
    public void Compute_RandomSmallTexts_MatchTheReferenceLongestCommonSubsequence()
    {
        var random = new Random(12345);
        string[] alphabet = ["{\n", "}\n", "\n", "a\n", "b\n", "a\r\n", "c\n"];
        for (int round = 0; round < 400; round++)
        {
            var oldLines = Enumerable.Range(0, random.Next(0, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var newLines = Enumerable.Range(0, random.Next(0, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            int common = ReferenceLcs(oldLines, newLines);

            var change = FileLineChange.Compute("A.cs", string.Concat(oldLines), string.Concat(newLines));

            Assert.That(change.LinesAdded, Is.EqualTo(newLines.Length - common), $"round {round} added");
            Assert.That(change.LinesRemoved, Is.EqualTo(oldLines.Length - common), $"round {round} removed");
        }
    }

    private static int ReferenceLcs(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Length, b.Length];
    }
}
