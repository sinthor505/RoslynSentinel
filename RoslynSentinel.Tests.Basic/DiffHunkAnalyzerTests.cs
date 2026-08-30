using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class DiffHunkAnalyzerTests
{
    [Test]
    public void Analyze_WellFormedSingleHunk_ReportsMatchingCounts()
    {
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added\n line2\n line3";

        var report = DiffHunkAnalyzer.Analyze(diff);

        Assert.That(report.HunkCount, Is.EqualTo(1));
        Assert.That(report.Hunks[0].HeaderCountsMatchBody, Is.True);
        Assert.That(report.HasFindings, Is.False);
    }

    [Test]
    public void Analyze_HeaderDeclaresMoreLinesThanBodyActuallyHas_FlagsMismatch()
    {
        // Regression shape for the real model-eval failure: header claims 7 old-side lines but
        // the body only has 2 context + 1 removed = 3.
        var diff = "@@ -320,7 +329,7 @@ namespace X;\n" +
                    "         var a = 1;\n" +
                    "         var b = 2;\n" +
                    "-        return Old();\n" +
                    "+        return New();\n";

        var report = DiffHunkAnalyzer.Analyze(diff);

        Assert.That(report.HunkCount, Is.EqualTo(1));
        var hunk = report.Hunks[0];
        Assert.That(hunk.HeaderCountsMatchBody, Is.False);
        Assert.That(hunk.DeclaredOldCount, Is.EqualTo(7));
        Assert.That(hunk.ActualOldCount, Is.EqualTo(3));
        Assert.That(hunk.DeclaredNewCount, Is.EqualTo(7));
        Assert.That(hunk.ActualNewCount, Is.EqualTo(3));
        Assert.That(report.HasFindings, Is.True);
        Assert.That(report.Describe(), Does.Contain("HEADER COUNT MISMATCH"));
    }

    [Test]
    public void Analyze_MultipleHunks_ReportsEachIndependently()
    {
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added1\n line2\n line3\n" +
                    "@@ -4,2 +5,3 @@\n line4\n+added2\n line5";

        var report = DiffHunkAnalyzer.Analyze(diff);

        Assert.That(report.HunkCount, Is.EqualTo(2));
        Assert.That(report.Hunks[0].HeaderCountsMatchBody, Is.True);
        Assert.That(report.Hunks[1].HeaderCountsMatchBody, Is.True);
    }

    [Test]
    public void Analyze_RealSize60TranscriptDiff_FlagsTheKnownHeaderMismatch()
    {
        // Both hunks in this real model-generated diff have wrong header counts: hunk 1 declares
        // "-3,6 +3,15" but its body actually has 4 old-side lines (not 6) and 19 new-side lines
        // (4 context + 15 added, not 15); hunk 2 declares "-320,7 +329,7" but its body only has 3
        // old-side and 3 new-side lines. Neither mismatch broke the apply itself (ReadHunkBody
        // doesn't trust these counts), but both are exactly the kind of drift this analyzer exists
        // to surface for future investigations.
        var diff =
            "@@ -3,6 +3,15 @@ namespace ContosoOrders.Core.FixtureHelpers;\n" +
            "\n" +
            " public class BlockConverter\n" +
            " {\n" +
            "+    private static string ReplaceBlockFormatted(string fileText, string oldBlock, string newBlock)\n" +
            "+    {\n" +
            "+        var index = fileText.IndexOf(oldBlock, StringComparison.Ordinal);\n" +
            "+        if (index < 0)\n" +
            "+        {\n" +
            "+            throw new InvalidOperationException(\"oldBlock not found in fileText.\");\n" +
            "+        }\n" +
            "+\n" +
            "+        var lineStart = fileText.LastIndexOf('\\n', Math.Max(0, index - 1)) + 1;\n" +
            "+        var indent = fileText[lineStart..index];\n" +
            "+\n" +
            "+        var formattedNewBlock = string.Join(\"\\n\" + indent, newBlock.Split('\\n').Select(line => line.TrimEnd()));\n" +
            "+\n" +
            "+        return fileText[..index] + formattedNewBlock + fileText[(index + oldBlock.Length)..];\n" +
            "+    }\n" +
            "     private readonly object _unrelatedField = new();\n" +
            "\n" +
            "@@ -320,7 +329,7 @@ namespace ContosoOrders.Core.FixtureHelpers;\n" +
            "         var newHeader = $\"public interface I{className}\";\n" +
            "         var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);\n" +
            "-        return ReformatWholeFile(rewritten);\n" +
            "+        return ReplaceBlockFormatted(fileText, oldHeader, newHeader);\n";

        var report = DiffHunkAnalyzer.Analyze(diff);

        Assert.That(report.HunkCount, Is.EqualTo(2));
        Assert.That(report.Hunks[0].HeaderCountsMatchBody, Is.False, "hunk 1's header falsely claimed 6/15 lines");
        Assert.That(report.Hunks[0].ActualOldCount, Is.EqualTo(4));
        Assert.That(report.Hunks[0].ActualNewCount, Is.EqualTo(19));
        Assert.That(report.Hunks[1].HeaderCountsMatchBody, Is.False, "hunk 2's header falsely claimed 7/7 lines");
        Assert.That(report.Hunks[1].ActualOldCount, Is.EqualTo(3));
        Assert.That(report.Hunks[1].ActualNewCount, Is.EqualTo(3));
        Assert.That(report.HasFindings, Is.True);
    }
}
