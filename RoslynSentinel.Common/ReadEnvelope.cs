namespace RoslynSentinel.Common;

/// <summary>
/// Metadata attached to every source-reading tool's result, describing the file's total scope and
/// whether the returned content is the whole thing or a slice. Without this, a caller that reads a
/// truncated excerpt forms an inaccurate belief about the file's contents, then a later search hit
/// landing outside the read range looks like a contradiction rather than an expected consequence of
/// truncation -> see docs/current/spec-read-tool-metadata-envelope-v1.md for the full rationale.
/// </summary>
public record ReadEnvelope
{
    /// <summary>Envelope shape version. Bump when fields are added/removed/repurposed.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Total lines in the unit being read: the file on disk for file reads (not the returned slice), or the member itself for member reads (see <see cref="ReadEnvelopeBuilder.BuildForMember"/>).</summary>
    public int LineCount { get; init; }

    /// <summary>Total bytes in the unit being read: the file on disk for file reads (not the returned slice), or the member itself for member reads.</summary>
    public long ByteCount { get; init; }

    /// <summary>Total lines in the whole file on disk, always. Equals <see cref="LineCount"/> for file reads; for member reads it is the size of the file the member sits in.</summary>
    public int TotalLinesInFile { get; init; }

    /// <summary>True when the full file/method was returned.</summary>
    public bool IsComplete { get; init; }

    /// <summary>First line included in the returned content (1-based).</summary>
    public int ReturnedFromLine { get; init; }

    /// <summary>Last line included in the returned content (1-based).</summary>
    public int ReturnedToLine { get; init; }

    /// <summary>Next line to request to continue reading. Null when <see cref="IsComplete"/> is true.</summary>
    public int? ContinuationOffset { get; init; }

    /// <summary>True when the file is large enough that fetching a structural outline is likely worthwhile before reading blind.</summary>
    public bool OutlineAvailable { get; init; }
}

/// <summary>
/// Server-side configurable thresholds controlling <see cref="ReadEnvelope"/> truncation and outline
/// advertisement. See docs/current/spec-read-tool-metadata-envelope-v1.md for the rationale behind
/// each default.
/// </summary>
public static class ReadEnvelopeThresholds
{
    /// <summary>At or below this many lines, a whole-file read returns the full file (no truncation).</summary>
    public static int ReadWholeMaxLines { get; set; } = 800;

    /// <summary>At or above this many lines, <see cref="ReadEnvelope.OutlineAvailable"/> is set true.</summary>
    public static int OutlineAvailableMinLines { get; set; } = 400;

    /// <summary>Hard cap on lines returned by a single whole-file read; above this the read truncates.</summary>
    public static int MaxReturnedLines { get; set; } = 1200;
}

public static class ReadEnvelopeBuilder
{
    /// <summary>
    /// Builds the envelope for a read of <paramref name="totalLineCount"/>/<paramref name="totalByteCount"/>
    /// total lines/bytes, where <paramref name="returnedFromLine"/>..<paramref name="returnedToLine"/> (both
    /// 1-based, inclusive) is what's actually being returned to the caller this call.
    /// </summary>
    public static ReadEnvelope Build(int totalLineCount, long totalByteCount, int returnedFromLine, int returnedToLine)
    {
        var isComplete = returnedFromLine <= 1 && returnedToLine >= totalLineCount;
        return new ReadEnvelope
        {
            LineCount = totalLineCount,
            TotalLinesInFile = totalLineCount,
            ByteCount = totalByteCount,
            IsComplete = isComplete,
            ReturnedFromLine = isComplete ? Math.Min(1, totalLineCount) : returnedFromLine,
            ReturnedToLine = isComplete ? totalLineCount : returnedToLine,
            ContinuationOffset = isComplete ? null : returnedToLine + 1,
            OutlineAvailable = totalLineCount >= ReadEnvelopeThresholds.OutlineAvailableMinLines
        };
    }

    /// <summary>
    /// Builds the envelope for a read of one whole member (method/constructor) located at
    /// <paramref name="memberFromLine"/>..<paramref name="memberToLine"/> (1-based, inclusive) in a file of
    /// <paramref name="fileLineCount"/> lines. The member is returned in full, so the result is always complete
    /// with no continuation offset; <see cref="ReadEnvelope.LineCount"/>/<see cref="ReadEnvelope.ByteCount"/>
    /// describe the member (not the file), <see cref="ReadEnvelope.TotalLinesInFile"/> gives the file's size, and
    /// the returned range gives the member's position in the file.
    /// </summary>
    public static ReadEnvelope BuildForMember(int fileLineCount, int memberFromLine, int memberToLine, long memberByteCount)
    {
        return new ReadEnvelope
        {
            LineCount = memberToLine - memberFromLine + 1,
            TotalLinesInFile = fileLineCount,
            ByteCount = memberByteCount,
            IsComplete = true,
            ReturnedFromLine = memberFromLine,
            ReturnedToLine = memberToLine,
            ContinuationOffset = null,
            OutlineAvailable = fileLineCount >= ReadEnvelopeThresholds.OutlineAvailableMinLines
        };
    }

    /// <summary>Builds the envelope for a full-file read, applying <see cref="ReadEnvelopeThresholds.MaxReturnedLines"/> truncation when the file exceeds it.</summary>
    public static ReadEnvelope BuildForWholeFile(int totalLineCount, long totalByteCount)
    {
        var returnedTo = Math.Min(totalLineCount, Math.Max(totalLineCount == 0 ? 0 : 1, ReadEnvelopeThresholds.MaxReturnedLines));
        return Build(totalLineCount, totalByteCount, returnedFromLine: 1, returnedToLine: returnedTo);
    }
}
