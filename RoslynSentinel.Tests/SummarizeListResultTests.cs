namespace RoslynSentinel.Tests;

/// <summary>
/// The list statusMessage carries counts only: the per-file paths already travel in
/// <c>listSummary.byFile</c> of the same envelope, so repeating them in the message just doubled
/// every file path in a list response.
/// </summary>
[TestFixture]
public class SummarizeListResultTests
{
    private static readonly string[] Paths =
    [
        @"c:\repo\A.cs", @"c:\repo\A.cs", @"c:\repo\A.cs",
        @"c:\repo\B.cs", @"c:\repo\B.cs",
        @"c:\repo\C.cs",
    ];

    [Test]
    public void ToSummaryMessage_ReportsCountsOnly_NoFilePaths()
    {
        var summary = SummarizeListResult.Build(Paths, p => p);

        var message = summary.ToSummaryMessage("match");

        Assert.That(message, Is.EqualTo("6 matches across 3 files."));
        Assert.That(message, Does.Not.Contain("A.cs"));
    }

    [Test]
    public void ToSummaryMessage_TruncatedFileList_StillCountsEveryFile_AndKeepsPathsInByFile()
    {
        var many = Enumerable.Range(0, 25).Select(i => $@"c:\repo\File{i}.cs").ToList();
        var summary = SummarizeListResult.Build(many, p => p, maxFilesShown: 20);

        Assert.That(summary.ToSummaryMessage("hit"), Is.EqualTo("25 hits across 25 files."));
        Assert.That(summary.ByFile, Has.Count.EqualTo(20));
        Assert.That(summary.TruncatedFileCount, Is.EqualTo(5));
    }

    [Test]
    public void ToSummaryMessage_SingularAndPlural_AndZero()
    {
        Assert.That(SummarizeListResult.Build(new[] { @"c:\repo\A.cs" }, p => p).ToSummaryMessage("caller"),
            Is.EqualTo("1 caller across 1 file."));
        Assert.That(SummarizeListResult.Build(Array.Empty<string>(), p => p).ToSummaryMessage("implementation"),
            Is.EqualTo("0 implementations."));
    }

    [Test]
    public void ByFile_StillCarriesPerFileCounts_SortedByCountDescending()
    {
        var summary = SummarizeListResult.Build(Paths, p => p);

        Assert.That(summary.ByFile.Select(f => (f.FilePath, f.Count)),
            Is.EqualTo(new[] { (@"c:\repo\A.cs", 3), (@"c:\repo\B.cs", 2), (@"c:\repo\C.cs", 1) }));
    }
}
