using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("DriftMessages")]
public class DriftMessagesTests
{
    #region SummarizeFiles Tests

    [Test]
    public void SummarizeFiles_EmptyCollection_ReturnsNoFiles()
    {
        var paths = new List<string>();
        var result = DriftMessages.SummarizeFiles(paths);
        Assert.That(result, Is.EqualTo("no files"));
    }

    [Test]
    public void SummarizeFiles_SinglePath_ReturnsFileName()
    {
        var paths = new List<string> { @"C:\Users\test\file.cs" };
        var result = DriftMessages.SummarizeFiles(paths);
        Assert.That(result, Is.EqualTo("file.cs"));
    }

    [Test]
    public void SummarizeFiles_FiveDistinctFiles_ReturnsAllNames()
    {
        var paths = new List<string>
        {
            @"C:\path\one.cs",
            @"C:\path\two.cs",
            @"C:\path\three.cs",
            @"C:\path\four.cs",
            @"C:\path\five.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        Assert.That(result, Is.EqualTo("one.cs, two.cs, three.cs, four.cs, five.cs"));
    }

    [Test]
    public void SummarizeFiles_SevenDistinctFilesDefaultMax_ShowsFirstFivePlusSuffix()
    {
        var paths = new List<string>
        {
            @"C:\path\one.cs",
            @"C:\path\two.cs",
            @"C:\path\three.cs",
            @"C:\path\four.cs",
            @"C:\path\five.cs",
            @"C:\path\six.cs",
            @"C:\path\seven.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        Assert.That(result, Is.EqualTo("one.cs, two.cs, three.cs, four.cs, five.cs (+2 more)"));
    }

    [Test]
    public void SummarizeFiles_DuplicatesByDifferentCase_CollapsesDeduplication()
    {
        var paths = new List<string>
        {
            @"C:\path\File.cs",
            @"D:\other\FILE.cs",
            @"E:\another\file.CS"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        // Case-insensitive deduplication keeps first-seen
        Assert.That(result, Is.EqualTo("File.cs"));
    }

    [Test]
    public void SummarizeFiles_DuplicatesByDifferentDirectories_CollapsesDeduplication()
    {
        var paths = new List<string>
        {
            @"C:\dir1\App.cs",
            @"D:\dir2\App.cs",
            @"E:\dir3\App.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        // Same file name from different directories collapses to one entry
        Assert.That(result, Is.EqualTo("App.cs"));
    }

    [Test]
    public void SummarizeFiles_MixedDuplicatesAndUnique_DeduplicatesCorrectly()
    {
        var paths = new List<string>
        {
            @"C:\path\Program.cs",
            @"D:\path\Program.cs",
            @"C:\path\Startup.cs",
            @"D:\path\Startup.cs",
            @"C:\path\Main.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        // Should have three distinct names: Program.cs, Startup.cs, Main.cs
        Assert.That(result, Is.EqualTo("Program.cs, Startup.cs, Main.cs"));
    }

    [Test]
    public void SummarizeFiles_CustomMaxParameter_RespectsBound()
    {
        var paths = new List<string>
        {
            @"C:\path\a.cs",
            @"C:\path\b.cs",
            @"C:\path\c.cs",
            @"C:\path\d.cs",
            @"C:\path\e.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths, max: 2);
        Assert.That(result, Is.EqualTo("a.cs, b.cs (+3 more)"));
    }

    [Test]
    public void SummarizeFiles_MaxEqualsCount_NoSuffix()
    {
        var paths = new List<string>
        {
            @"C:\path\one.cs",
            @"C:\path\two.cs",
            @"C:\path\three.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths, max: 3);
        Assert.That(result, Is.EqualTo("one.cs, two.cs, three.cs"));
    }

    [Test]
    public void SummarizeFiles_ReturnsOnlyBareFileNames_NotFullPaths()
    {
        var paths = new List<string>
        {
            @"C:\Users\Administrator\source\repos\RoslynSentinel\Program.cs",
            @"D:\another\very\long\path\Startup.cs"
        };
        var result = DriftMessages.SummarizeFiles(paths);
        Assert.That(result, Is.EqualTo("Program.cs, Startup.cs"));
        Assert.That(result, Does.Not.Contain(@"\"));
    }

    #endregion

    #region BuildHint Tests

    [Test]
    public void BuildHint_EmptyCollection_ReturnsNull()
    {
        var changes = new List<string>();
        var result = DriftMessages.BuildHint(changes);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void BuildHint_NonEmpty_NamesExternalFileDrift()
    {
        var changes = new List<string> { @"C:\path\file.cs" };
        var result = DriftMessages.BuildHint(changes);
        Assert.That(result, Does.Contain("ExternalFileDrift"));
    }

    [Test]
    public void BuildHint_NonEmpty_ContainsFirstFileName()
    {
        var changes = new List<string> { @"C:\path\file.cs" };
        var result = DriftMessages.BuildHint(changes);
        Assert.That(result, Does.Contain("file.cs"));
    }

    [Test]
    public void BuildHint_TwoFiles_ContainsFileCountAndSummary()
    {
        var changes = new List<string>
        {
            @"C:\path\one.cs",
            @"C:\path\two.cs"
        };
        var result = DriftMessages.BuildHint(changes);
        Assert.That(result, Does.Contain("2 file(s)"));
        Assert.That(result, Does.Contain("one.cs"));
        Assert.That(result, Does.Contain("two.cs"));
    }

    [Test]
    public void BuildHint_MultipleFiles_FormatsCorrectly()
    {
        var changes = new List<string>
        {
            @"C:\path\alpha.cs",
            @"C:\path\beta.cs",
            @"C:\path\gamma.cs"
        };
        var result = DriftMessages.BuildHint(changes);
        Assert.That(result, Does.Contain("3 file(s)"));
        Assert.That(result, Does.Contain("alpha.cs, beta.cs, gamma.cs"));
    }

    #endregion

    #region ResolveSelection Tests

    [Test]
    public void ResolveSelection_EmptyRequested_NoMatches()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string>();
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Is.Empty);
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_ExactMatch_AddsToMatched()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { @"C:\repo\file.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\file.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_CaseInsensitiveMatch_Succeeds()
    {
        var current = new List<string> { @"C:\repo\File.cs" };
        var requested = new List<string> { @"C:\repo\file.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_MatchByBareFileName_Succeeds()
    {
        var current = new List<string> { @"C:\repo\Sub\Foo.cs" };
        var requested = new List<string> { "Foo.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\Sub\Foo.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_MatchByRelativePathWithForwardSlash_Succeeds()
    {
        var current = new List<string> { @"C:\repo\Sub\Foo.cs" };
        var requested = new List<string> { "Sub/Foo.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\Sub\Foo.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_UnknownToken_AddsToUnmatched()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { "unknown.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Is.Empty);
        Assert.That(unmatched, Has.Count.EqualTo(1));
        Assert.That(unmatched[0], Is.EqualTo("unknown.cs"));
    }

    [Test]
    public void ResolveSelection_BlankTokens_AreIgnored()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { "   ", "", "\t" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Is.Empty);
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_MixedBlankAndValid_IgnoresBlanks()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { "   ", @"C:\repo\file.cs", "\t" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_MultipleMatches_ReturnedInOrder()
    {
        var current = new List<string>
        {
            @"C:\repo\one.cs",
            @"C:\repo\two.cs",
            @"C:\repo\three.cs"
        };
        var requested = new List<string> { "one.cs", "three.cs", "two.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(3));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\one.cs"));
        Assert.That(matched[1], Is.EqualTo(@"C:\repo\three.cs"));
        Assert.That(matched[2], Is.EqualTo(@"C:\repo\two.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_DuplicateRequestedTokens_ReturnedDistinct()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { @"C:\repo\file.cs", @"C:\repo\file.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\file.cs"));
    }

    [Test]
    public void ResolveSelection_MixedMatched_AndUnmatched()
    {
        var current = new List<string>
        {
            @"C:\repo\found.cs",
            @"C:\repo\also_found.cs"
        };
        var requested = new List<string> { "found.cs", "notfound.cs", "also_found.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(2));
        Assert.That(unmatched, Has.Count.EqualTo(1));
        Assert.That(unmatched[0], Is.EqualTo("notfound.cs"));
    }

    [Test]
    public void ResolveSelection_NestedPathMatch_WithForwardSlash()
    {
        var current = new List<string> { @"C:\repo\src\Models\User.cs" };
        var requested = new List<string> { "src/Models/User.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\src\Models\User.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_MatchedReturnsFull_OriginalEntries()
    {
        var current = new List<string>
        {
            @"C:\very\long\path\to\file.cs",
            @"D:\another\long\path\other.cs"
        };
        var requested = new List<string> { "file.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched[0], Is.EqualTo(@"C:\very\long\path\to\file.cs"));
        // Not just the bare name
        Assert.That(matched[0], Does.Contain(@"\"));
    }

    [Test]
    public void ResolveSelection_TokensWithWhitespace_AreTrimmed()
    {
        var current = new List<string> { @"C:\repo\file.cs" };
        var requested = new List<string> { "  file.cs  " };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(unmatched, Is.Empty);
    }

    [Test]
    public void ResolveSelection_SuffixMatch_AfterBackslash()
    {
        var current = new List<string> { @"C:\repo\src\Program.cs" };
        var requested = new List<string> { @"src\Program.cs" };
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);
        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0], Is.EqualTo(@"C:\repo\src\Program.cs"));
        Assert.That(unmatched, Is.Empty);
    }

    #endregion
}
