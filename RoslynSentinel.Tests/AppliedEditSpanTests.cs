using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// AppliedEditSpan.FromEdits: null when there is nothing to report, per-file ordering by start offset, the
/// total span cap, and truncation of long replacement text.
/// </summary>
[TestFixture]
public class AppliedEditSpanTests
{
    private static readonly FilePathWrapper FileA = new(@"C:\repo\A.cs");
    private static readonly FilePathWrapper FileB = new(@"C:\repo\B.cs");

    [Test]
    public void FromEdits_NullInput_ReturnsNull()
    {
        Assert.That(AppliedEditSpan.FromEdits(null), Is.Null);
    }

    [Test]
    public void FromEdits_EmptyDictionary_ReturnsNull()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>();

        Assert.That(AppliedEditSpan.FromEdits(edits), Is.Null);
    }

    [Test]
    public void FromEdits_FileWithNoChanges_ReturnsNull()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>> { [FileA] = Array.Empty<TextChange>() };

        Assert.That(AppliedEditSpan.FromEdits(edits), Is.Null);
    }

    [Test]
    public void FromEdits_SingleFile_OrdersSpansByStart()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [FileA] = new[] { new TextChange(new TextSpan(30, 2), "c"), new TextChange(new TextSpan(5, 1), "a"), new TextChange(new TextSpan(12, 0), "b") },
        };

        var spans = AppliedEditSpan.FromEdits(edits);

        Assert.That(spans, Is.Not.Null);
        Assert.That(spans!.Select(s => s.Start), Is.EqualTo(new[] { 5, 12, 30 }));
        Assert.That(spans.Select(s => s.Length), Is.EqualTo(new[] { 1, 0, 2 }));
        Assert.That(spans.Select(s => s.NewText), Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(spans.All(s => s.FilePath == FileA.ToString()), Is.True, "Every span must carry its file path");
    }

    [Test]
    public void FromEdits_MultipleFiles_OrdersSpansWithinEachFile()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [FileA] = new[] { new TextChange(new TextSpan(9, 1), "x"), new TextChange(new TextSpan(2, 1), "y") },
            [FileB] = new[] { new TextChange(new TextSpan(7, 1), "p"), new TextChange(new TextSpan(1, 1), "q") },
        };

        var spans = AppliedEditSpan.FromEdits(edits);

        Assert.That(spans, Is.Not.Null);
        Assert.That(spans!.Where(s => s.FilePath == FileA.ToString()).Select(s => s.Start), Is.EqualTo(new[] { 2, 9 }));
        Assert.That(spans.Where(s => s.FilePath == FileB.ToString()).Select(s => s.Start), Is.EqualTo(new[] { 1, 7 }));
    }

    [Test]
    public void FromEdits_TotalSpanCap_StopsAtMaxSpans()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [FileA] = new[] { new TextChange(new TextSpan(1, 1), "a"), new TextChange(new TextSpan(3, 1), "b") },
            [FileB] = new[] { new TextChange(new TextSpan(5, 1), "c"), new TextChange(new TextSpan(7, 1), "d") },
        };

        var spans = AppliedEditSpan.FromEdits(edits, maxSpans: 3);

        Assert.That(spans, Has.Count.EqualTo(3), "The cap is on the total across all files, not per file");
        Assert.That(spans!.Where(s => s.FilePath == FileA.ToString()).Select(s => s.Start), Is.EqualTo(new[] { 1, 3 }));
        Assert.That(spans.Count(s => s.FilePath == FileB.ToString()), Is.EqualTo(1));
    }

    [Test]
    public void FromEdits_NewTextLongerThanLimit_IsCutAndFlaggedTruncated()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [FileA] = new[] { new TextChange(new TextSpan(0, 0), "abcdefghij") },
        };

        var spans = AppliedEditSpan.FromEdits(edits, maxNewTextChars: 4);

        Assert.That(spans, Has.Count.EqualTo(1));
        Assert.That(spans![0].NewText, Is.EqualTo("abcd"));
        Assert.That(spans[0].NewTextTruncated, Is.True);
    }

    [Test]
    public void FromEdits_NewTextAtLimit_IsNotTruncated()
    {
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [FileA] = new[] { new TextChange(new TextSpan(0, 0), "abcd") },
        };

        var spans = AppliedEditSpan.FromEdits(edits, maxNewTextChars: 4);

        Assert.That(spans![0].NewText, Is.EqualTo("abcd"));
        Assert.That(spans[0].NewTextTruncated, Is.False, "Text exactly at the limit is not cut");
    }
}
