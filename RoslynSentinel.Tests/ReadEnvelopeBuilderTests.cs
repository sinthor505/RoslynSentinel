using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

// Covers docs/current/spec-read-tool-metadata-envelope-v1.md's "BuildEnvelope helper" test list.
[TestFixture]
public class ReadEnvelopeBuilderTests
{
    private int _originalReadWholeMaxLines;
    private int _originalOutlineAvailableMinLines;
    private int _originalMaxReturnedLines;

    [SetUp]
    public void Setup()
    {
        _originalReadWholeMaxLines = ReadEnvelopeThresholds.ReadWholeMaxLines;
        _originalOutlineAvailableMinLines = ReadEnvelopeThresholds.OutlineAvailableMinLines;
        _originalMaxReturnedLines = ReadEnvelopeThresholds.MaxReturnedLines;
    }

    [TearDown]
    public void TearDown()
    {
        ReadEnvelopeThresholds.ReadWholeMaxLines = _originalReadWholeMaxLines;
        ReadEnvelopeThresholds.OutlineAvailableMinLines = _originalOutlineAvailableMinLines;
        ReadEnvelopeThresholds.MaxReturnedLines = _originalMaxReturnedLines;
    }

    [Test]
    public void BuildForWholeFile_FileAtOrBelowReadWholeMax_IsCompleteWithFullRange()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 500, totalByteCount: 12_000);

        Assert.That(envelope.IsComplete, Is.True);
        Assert.That(envelope.ReturnedFromLine, Is.EqualTo(1));
        Assert.That(envelope.ReturnedToLine, Is.EqualTo(500));
        Assert.That(envelope.ContinuationOffset, Is.Null);
    }

    [Test]
    public void BuildForWholeFile_FileAboveMaxReturnedLines_TruncatesAndSetsContinuationOffset()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 2000, totalByteCount: 50_000);

        Assert.That(envelope.IsComplete, Is.False);
        Assert.That(envelope.ReturnedToLine, Is.EqualTo(ReadEnvelopeThresholds.MaxReturnedLines));
        Assert.That(envelope.ContinuationOffset, Is.EqualTo(ReadEnvelopeThresholds.MaxReturnedLines + 1));
    }

    [Test]
    public void BuildForWholeFile_FileInOutlineBand_IsCompleteAndOutlineAvailable()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 600, totalByteCount: 15_000);

        Assert.That(envelope.IsComplete, Is.True);
        Assert.That(envelope.OutlineAvailable, Is.True);
    }

    [Test]
    public void BuildForWholeFile_FileBelowOutlineMin_OutlineNotAvailable()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 50, totalByteCount: 1_000);

        Assert.That(envelope.OutlineAvailable, Is.False);
    }

    [Test]
    public void BuildForWholeFile_TruncatedCase_CountsReflectTotalFileNotReturnedSlice()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 2000, totalByteCount: 50_000);

        Assert.That(envelope.LineCount, Is.EqualTo(2000));
        Assert.That(envelope.ByteCount, Is.EqualTo(50_000));
    }

    [Test]
    public void Build_AlwaysPopulatesSchemaVersion()
    {
        var envelope = ReadEnvelopeBuilder.Build(100, 2000, 1, 100);

        Assert.That(envelope.SchemaVersion, Is.EqualTo(1));
    }

    [Test]
    public void BuildForWholeFile_EmptyFile_IsCompleteWithZeroCounts()
    {
        var envelope = ReadEnvelopeBuilder.BuildForWholeFile(totalLineCount: 0, totalByteCount: 0);

        Assert.That(envelope.IsComplete, Is.True);
        Assert.That(envelope.LineCount, Is.EqualTo(0));
        Assert.That(envelope.ByteCount, Is.EqualTo(0));
        Assert.That(envelope.ContinuationOffset, Is.Null);
    }

    [Test]
    public void Build_PartialSliceNotStartingAtOne_IsNotComplete()
    {
        var envelope = ReadEnvelopeBuilder.Build(totalLineCount: 1400, totalByteCount: 40_000, returnedFromLine: 50, returnedToLine: 120);

        Assert.That(envelope.IsComplete, Is.False);
        Assert.That(envelope.ReturnedFromLine, Is.EqualTo(50));
        Assert.That(envelope.ReturnedToLine, Is.EqualTo(120));
        Assert.That(envelope.ContinuationOffset, Is.EqualTo(121));
    }
}
