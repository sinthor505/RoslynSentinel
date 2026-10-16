using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Why a position or line has no destination in the trial text.</summary>
public enum PositionUnmappedReason
{
    /// <summary>Not unmapped.</summary>
    None = 0,

    /// <summary>The offset or line number is outside the original document.</summary>
    OutOfRange,

    /// <summary>The offset lies inside a span the engine replaced or deleted (and that span is not the moved member).</summary>
    InsideEditedRegion,

    /// <summary>The offset lies in a part of the moved member the engine trimmed away (leading blank lines, trailing blank lines).</summary>
    InsideTrimmedRegion,

    /// <summary>The line was deleted by an edit that is not the moved member.</summary>
    LineRemoved,

    /// <summary>The line is touched by an edit that spans a line break or adds one, so its destination line is not defined.</summary>
    LineStructurallyEdited,

    /// <summary>The map failed line-text validation, so no line may be trusted.</summary>
    MapUntrusted,
}

/// <summary>Result of mapping one offset: either the destination document and offset in its trial text, or a reason.</summary>
public readonly record struct PositionMapResult(bool IsMapped, FilePathWrapper Document, int Offset, PositionUnmappedReason Reason)
{
    /// <summary>A mapped result.</summary>
    public static PositionMapResult Mapped(FilePathWrapper document, int offset) => new(true, document, offset, PositionUnmappedReason.None);

    /// <summary>An unmapped result; <c>Offset</c> is -1.</summary>
    public static PositionMapResult Unmapped(FilePathWrapper document, PositionUnmappedReason reason) => new(false, document, -1, reason);
}

/// <summary>Result of mapping one line: either the destination document and 1-based line in its trial text, or a reason.</summary>
public readonly record struct LineMapResult(bool IsMapped, FilePathWrapper Document, int Line, PositionUnmappedReason Reason)
{
    /// <summary>A mapped result; <paramref name="line"/> is 1-based.</summary>
    public static LineMapResult Mapped(FilePathWrapper document, int line) => new(true, document, line, PositionUnmappedReason.None);

    /// <summary>An unmapped result; <c>Line</c> is 0.</summary>
    public static LineMapResult Unmapped(FilePathWrapper document, PositionUnmappedReason reason) => new(false, document, 0, reason);
}

/// <summary>
/// One member that an instance move cuts out of its source document and writes into a target document. Plain data,
/// everything is in the ORIGINAL texts' coordinates.
/// </summary>
/// <param name="SourceDocument">Document the member is removed from.</param>
/// <param name="SourceSpan">The member's <c>FullSpan</c> in the source document's original text (the span
/// <c>BuildMovedMemberText</c> reads). Must start at the start of a line.</param>
/// <param name="TargetDocument">Document the member is inserted into (may equal <paramref name="SourceDocument"/>).</param>
/// <param name="TargetInsertionOffset">Start of the zero-length insertion <c>TextChange</c> (<c>BuildInsertion</c>) in the
/// target document's original text.</param>
/// <param name="MovedText">The block <c>BuildMovedMemberText</c> returned for this member: re-indented, trimmed, using the
/// target's line ending. It must appear verbatim in that insertion's <c>NewText</c>. When several members share one
/// insertion, list them in insertion order.</param>
/// <param name="InMemberEdits">The edits (spans in the source document's original text, inside <paramref name="SourceSpan"/>)
/// that travel with the member text, i.e. <c>insideMovedMemberEdits</c> from <c>BuildSourceEdits</c>. They must not add or
/// remove line breaks. Null means none.</param>
public sealed record MovedMemberRegion(
    FilePathWrapper SourceDocument,
    TextSpan SourceSpan,
    FilePathWrapper TargetDocument,
    int TargetInsertionOffset,
    string MovedText,
    IReadOnlyList<TextChange>? InMemberEdits = null);

/// <summary>The first source line whose destination line did not have the text the map predicted.</summary>
public sealed record PositionMapLineMismatch(
    FilePathWrapper SourceDocument,
    int SourceLine,
    FilePathWrapper DestinationDocument,
    int DestinationLine,
    string Expected,
    string Actual,
    string Detail)
{
    /// <summary>One-sentence note suitable for a preview row.</summary>
    public string Describe()
    {
        var source = Path.GetFileName(SourceDocument.Absolute);
        var destination = Path.GetFileName(DestinationDocument.Absolute);
        return DestinationLine > 0
            ? $"line {SourceLine} of '{source}' maps to line {DestinationLine} of '{destination}' but {Detail}; expected `{Expected}`, found `{Actual}`"
            : $"line {SourceLine} of '{source}' could not be checked: {Detail}";
    }
}

/// <summary>
/// The per-line table (source line to destination line, per document). A document that has no edits maps every line to
/// itself. Lines are 1-based, as in <c>DiagnosticInfo</c>. When the map is untrusted every line is unmapped.
/// </summary>
public sealed class PositionMapLines
{
    private readonly Dictionary<FilePathWrapper, LineMapResult[]> _table;
    private readonly bool _untrusted;

    internal PositionMapLines(Dictionary<FilePathWrapper, LineMapResult[]> table, bool untrusted)
    {
        _table = table;
        _untrusted = untrusted;
    }

    /// <summary>Destination of a 1-based line of a document's original text.</summary>
    public LineMapResult MapLine(FilePathWrapper sourceDocument, int sourceLine)
    {
        if (_untrusted)
        {
            return LineMapResult.Unmapped(sourceDocument, PositionUnmappedReason.MapUntrusted);
        }

        if (!_table.TryGetValue(sourceDocument, out var lines))
        {
            return LineMapResult.Mapped(sourceDocument, sourceLine);
        }

        return sourceLine < 1 || sourceLine > lines.Length ? LineMapResult.Unmapped(sourceDocument, PositionUnmappedReason.OutOfRange) : lines[sourceLine - 1];
    }
}

/// <summary>Outcome of <see cref="MovePositionMap.Validate"/>. Use <c>Lines</c> only when <c>IsTrusted</c>.</summary>
public sealed record PositionMapValidation(bool IsTrusted, PositionMapLineMismatch? FirstMismatch, PositionMapLines Lines);

/// <summary>
/// A source-to-trial position map for a move, built from the engine's OWN <see cref="TextChange"/>s (never by diffing
/// texts). <list type="bullet">
/// <item>Unchanged text maps to itself plus the cumulative delta of every edit that ends at or before it. Character
/// semantics: a zero-length insertion exactly at offset p lands BEFORE the character at p, so p maps to the character
/// after the inserted text (this differs from <see cref="MoveMemberTextEdits.MapPositionThroughEdits"/>, which leaves p
/// before the insertion).</item>
/// <item>The moved member maps across documents: a source offset inside <c>SourceSpan</c> maps to the insertion's new
/// start, plus the block's offset inside the insertion text, plus the line-wise position inside the block. Re-indentation
/// changes the column only; line breaks map one to one.</item>
/// <item>An offset inside any other edited-away span, or inside a part of the member the engine trimmed, is
/// <c>Unmapped</c> with a reason. Nothing is guessed.</item>
/// </list>
/// Self-references inside the moved body need no special case: they are ordinary offsets of the original source
/// document inside <c>SourceSpan</c>. A caller that recorded such a reference relative to the member start passes
/// <see cref="MapMovedMemberOffset"/> (member index, relative offset) instead of adding the start itself.
/// After the trial texts exist, call <see cref="Validate"/>; a mismatching line makes the whole map untrusted and the
/// caller should fall back to its Ambiguous classification with the mismatch note.
/// A moved member whose target is a brand-new file is not supported (there is no original target text).
/// </summary>
public sealed class MovePositionMap
{
    private readonly Dictionary<FilePathWrapper, DocumentEdits> _documents;
    private readonly List<MovedMember> _moved;

    private MovePositionMap(Dictionary<FilePathWrapper, DocumentEdits> documents, List<MovedMember> moved)
    {
        _documents = documents;
        _moved = moved;
    }

    /// <summary>
    /// Builds the map. <paramref name="originalTexts"/> must hold the original text of every document that has edits;
    /// <paramref name="editsByDocument"/> is the exact edit list applied to each (positions in the original text; for the
    /// instance route, <c>MaterializeDocumentEditsWithAppliedEditsAsync</c>'s <c>AppliedEdits</c> re-keyed is that list).
    /// Returns false with an explanatory <paramref name="error"/> when the inputs are inconsistent (missing text, overlapping
    /// or out-of-range edits, a moved member that is not removed by an edit, a block that is not found in its
    /// insertion). The caller should then fall back rather than trust any position.
    /// </summary>
    public static bool TryBuild(
        IReadOnlyDictionary<FilePathWrapper, SourceText> originalTexts,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> editsByDocument,
        IReadOnlyList<MovedMemberRegion> movedMembers,
        out MovePositionMap? map,
        out string? error)
    {
        map = null;
        error = null;
        var documents = new Dictionary<FilePathWrapper, DocumentEdits>();
        foreach (var (path, edits) in editsByDocument)
        {
            if (edits.Count == 0)
            {
                continue;
            }

            if (!originalTexts.TryGetValue(path, out var text))
            {
                error = $"No original text was supplied for '{Path.GetFileName(path.Absolute)}', which has edits.";
                return false;
            }

            var sorted = edits.OrderBy(c => c.Span.Start).ThenBy(c => c.Span.Length).ToArray();
            for (var i = 0; i < sorted.Length; i++)
            {
                if (sorted[i].Span.End > text.Length || (i + 1 < sorted.Length && sorted[i].Span.End > sorted[i + 1].Span.Start))
                {
                    error = $"The edits for '{Path.GetFileName(path.Absolute)}' overlap or fall outside the original text.";
                    return false;
                }
            }

            documents[path] = new DocumentEdits(text, sorted);
        }

        var moved = new List<MovedMember>();
        var cursors = new Dictionary<(FilePathWrapper Document, int EditIndex), int>();
        foreach (var region in movedMembers)
        {
            if (!TryBuildMoved(region, documents, cursors, out var member, out error))
            {
                return false;
            }

            moved.Add(member!);
        }

        map = new MovePositionMap(documents, moved);
        return true;
    }

    /// <summary>
    /// Maps an offset in <paramref name="document"/>'s ORIGINAL text to the trial text. A document with no edits maps
    /// every offset to itself. The mapped document differs from <paramref name="document"/> only for the moved member.
    /// </summary>
    public PositionMapResult MapOffset(FilePathWrapper document, int sourceOffset)
    {
        if (!_documents.TryGetValue(document, out var doc))
        {
            return PositionMapResult.Mapped(document, sourceOffset);
        }

        if (sourceOffset < 0 || sourceOffset > doc.Original.Length)
        {
            return PositionMapResult.Unmapped(document, PositionUnmappedReason.OutOfRange);
        }

        foreach (var member in _moved)
        {
            if (member.Region.SourceDocument == document && member.Region.SourceSpan.Start <= sourceOffset && sourceOffset < member.Region.SourceSpan.End)
            {
                return MapIntoMovedMember(member, sourceOffset);
            }
        }

        return doc.FindContainingEdit(sourceOffset) >= 0
            ? PositionMapResult.Unmapped(document, PositionUnmappedReason.InsideEditedRegion)
            : PositionMapResult.Mapped(document, sourceOffset + doc.ShiftBefore(sourceOffset));
    }

    /// <summary>
    /// Maps an offset given relative to the start of moved member <paramref name="memberIndex"/> (index into the list given
    /// to <see cref="TryBuild"/>). Equivalent to <c>MapOffset(member.SourceDocument, member.SourceSpan.Start + relativeOffset)</c>.
    /// </summary>
    public PositionMapResult MapMovedMemberOffset(int memberIndex, int relativeOffset)
    {
        var region = _moved[memberIndex].Region;
        return relativeOffset < 0 || relativeOffset >= region.SourceSpan.Length
            ? PositionMapResult.Unmapped(region.SourceDocument, PositionUnmappedReason.OutOfRange)
            : MapOffset(region.SourceDocument, region.SourceSpan.Start + relativeOffset);
    }

    /// <summary>
    /// Validates the map against the trial texts the engine actually produced (every document that had edits; keyed like
    /// the edits). For each source line the map claims, the destination line's text must equal the source line's text
    /// with the in-line edits applied, ignoring leading and trailing whitespace. The first mismatch makes the map
    /// untrusted (<c>Lines</c> is then all unmapped) and is reported so the caller can fall back to Ambiguous with a note.
    /// </summary>
    public PositionMapValidation Validate(IReadOnlyDictionary<FilePathWrapper, string> trialTexts)
    {
        var trials = new Dictionary<FilePathWrapper, SourceText>();
        foreach (var path in _documents.Keys)
        {
            if (!trialTexts.TryGetValue(path, out var text))
            {
                return Untrusted(new PositionMapLineMismatch(path, 0, path, 0, string.Empty, string.Empty, "no trial text was supplied for an edited document"));
            }

            trials[path] = SourceText.From(text);
        }

        var tables = new Dictionary<FilePathWrapper, LineMapResult[]>();
        foreach (var (path, doc) in _documents)
        {
            var table = new LineMapResult[doc.Original.Lines.Count];
            for (var i = 0; i < table.Length; i++)
            {
                var mismatch = ClassifyLine(path, doc, i, trials, out table[i]);
                if (mismatch != null)
                {
                    return Untrusted(mismatch);
                }
            }

            tables[path] = table;
        }

        return new PositionMapValidation(true, null, new PositionMapLines(tables, false));
    }

    private static PositionMapValidation Untrusted(PositionMapLineMismatch mismatch) =>
        new(false, mismatch, new PositionMapLines(new Dictionary<FilePathWrapper, LineMapResult[]>(), true));

    private PositionMapResult MapIntoMovedMember(MovedMember member, int sourceOffset)
    {
        var region = member.Region;
        var shift = 0;
        foreach (var edit in member.InMemberEdits)
        {
            if (edit.Span.Length > 0 && edit.Span.Start <= sourceOffset && sourceOffset < edit.Span.End)
            {
                return PositionMapResult.Unmapped(region.SourceDocument, PositionUnmappedReason.InsideEditedRegion);
            }

            if (edit.Span.End <= sourceOffset)
            {
                shift += (edit.NewText?.Length ?? 0) - edit.Span.Length;
            }
        }

        var rawPosition = sourceOffset - region.SourceSpan.Start + shift;
        var rawLine = member.RawLines.FindLastIndex(l => l.Start <= rawPosition);
        if (rawLine < member.Lead || rawLine > member.LastKept)
        {
            return PositionMapResult.Unmapped(region.SourceDocument, PositionUnmappedReason.InsideTrimmedRegion);
        }

        var raw = member.RawLines[rawLine];
        var block = member.BlockLines[rawLine - member.Lead];
        var column = rawPosition - raw.Start;
        var rawIndent = LeadingWhitespace(member.Raw, raw);
        var blockIndent = LeadingWhitespace(region.MovedText, block);
        var blockColumn = column >= rawIndent ? column - rawIndent + blockIndent : Math.Min(column, blockIndent);
        blockColumn = Math.Min(blockColumn, block.Length);
        return PositionMapResult.Mapped(region.TargetDocument, member.DestinationBlockStart + block.Start + blockColumn);
    }

    private PositionMapLineMismatch? ClassifyLine(FilePathWrapper path, DocumentEdits doc, int lineIndex, Dictionary<FilePathWrapper, SourceText> trials, out LineMapResult result)
    {
        var line = doc.Original.Lines[lineIndex];
        var member = _moved.FirstOrDefault(m => m.Region.SourceDocument == path && m.Region.SourceSpan.Start <= line.Start && line.Start < m.Region.SourceSpan.End);
        if (member != null)
        {
            return ClassifyMovedLine(path, member, lineIndex, trials, out result);
        }

        var start = line.Start;
        var end = line.End;
        var endWithBreak = line.EndIncludingLineBreak;
        var inLine = new List<TextChange>();
        var removed = false;
        var structural = false;
        foreach (var edit in doc.Edits)
        {
            var span = edit.Span;
            if (span.Length == 0)
            {
                if (span.Start < start || span.Start > end || (span.Start == start && EndsWithBreak(edit.NewText)))
                {
                    continue;
                }

                if (HasBreak(edit.NewText))
                {
                    structural = true;
                    continue;
                }

                inLine.Add(edit);
                continue;
            }

            if (span.End <= start || span.Start >= endWithBreak)
            {
                continue;
            }

            if (span.Start <= start && span.End >= endWithBreak)
            {
                removed = true;
            }
            else if (span.Start >= start && span.End <= end && !HasBreak(edit.NewText))
            {
                inLine.Add(edit);
            }
            else
            {
                structural = true;
            }
        }

        if (removed || structural)
        {
            result = LineMapResult.Unmapped(path, removed ? PositionUnmappedReason.LineRemoved : PositionUnmappedReason.LineStructurallyEdited);
            return null;
        }

        var expected = doc.Original.ToString(line.Span);
        foreach (var edit in inLine.OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.Length))
        {
            var offset = edit.Span.Start - start;
            expected = expected.Remove(offset, edit.Span.Length).Insert(offset, edit.NewText ?? string.Empty);
        }

        var trial = trials[path];
        var destination = start + doc.ShiftBefore(start);
        if (destination > trial.Length)
        {
            result = default;
            return new PositionMapLineMismatch(path, lineIndex + 1, path, 0, Excerpt(expected), string.Empty, "its mapped position is outside the trial text");
        }

        var destinationLine = trial.Lines.GetLineFromPosition(destination);
        var actual = trial.ToString(destinationLine.Span);
        if (!string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal))
        {
            result = default;
            return new PositionMapLineMismatch(path, lineIndex + 1, path, destinationLine.LineNumber + 1, Excerpt(expected), Excerpt(actual), "its text differs");
        }

        result = LineMapResult.Mapped(path, destinationLine.LineNumber + 1);
        return null;
    }

    private static PositionMapLineMismatch? ClassifyMovedLine(FilePathWrapper path, MovedMember member, int lineIndex, Dictionary<FilePathWrapper, SourceText> trials, out LineMapResult result)
    {
        var region = member.Region;
        var rawLine = lineIndex - member.StartLineIndex;
        if (rawLine < member.Lead || rawLine > member.LastKept)
        {
            result = LineMapResult.Unmapped(path, PositionUnmappedReason.InsideTrimmedRegion);
            return null;
        }

        var block = member.BlockLines[rawLine - member.Lead];
        var expected = member.Raw.Substring(member.RawLines[rawLine].Start, member.RawLines[rawLine].Length);
        var trial = trials[region.TargetDocument];
        var destination = member.DestinationBlockStart + block.Start;
        if (destination > trial.Length)
        {
            result = default;
            return new PositionMapLineMismatch(path, lineIndex + 1, region.TargetDocument, 0, Excerpt(expected), string.Empty, "its mapped position is outside the trial text");
        }

        var destinationLine = trial.Lines.GetLineFromPosition(destination);
        var actual = trial.ToString(destinationLine.Span);
        if (destinationLine.Start != destination || !string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal))
        {
            result = default;
            return new PositionMapLineMismatch(path, lineIndex + 1, region.TargetDocument, destinationLine.LineNumber + 1, Excerpt(expected), Excerpt(actual), destinationLine.Start != destination ? "the moved line does not start a line" : "its text differs");
        }

        result = LineMapResult.Mapped(region.TargetDocument, destinationLine.LineNumber + 1);
        return null;
    }

    private static bool TryBuildMoved(MovedMemberRegion region, Dictionary<FilePathWrapper, DocumentEdits> documents, Dictionary<(FilePathWrapper Document, int EditIndex), int> cursors, out MovedMember? moved, out string? error)
    {
        moved = null;
        var description = $"The moved member at {region.SourceSpan.Start}..{region.SourceSpan.End} of '{Path.GetFileName(region.SourceDocument.Absolute)}'";
        var full = region.SourceSpan;
        if (!documents.TryGetValue(region.SourceDocument, out var source) || full.Length == 0 || full.End > source.Original.Length)
        {
            error = $"{description} is not covered by an edited source document.";
            return false;
        }

        if (!source.Edits.Any(e => e.Span.Contains(full) && string.IsNullOrEmpty(e.NewText)))
        {
            error = $"{description} is not removed by any edit.";
            return false;
        }

        var startLine = source.Original.Lines.GetLineFromPosition(full.Start);
        if (startLine.Start != full.Start)
        {
            error = $"{description} does not start at the start of a line.";
            return false;
        }

        var fullText = source.Original.ToString(full);
        var inMember = new List<TextChange>();
        foreach (var edit in region.InMemberEdits ?? Array.Empty<TextChange>())
        {
            if (!full.Contains(edit.Span) || HasBreak(edit.NewText) || HasBreak(fullText.Substring(edit.Span.Start - full.Start, edit.Span.Length)))
            {
                error = $"{description} has an in-member edit that is outside the member or adds or removes a line break.";
                return false;
            }

            inMember.Add(edit);
        }

        inMember = inMember.OrderBy(e => e.Span.Start).ThenBy(e => e.Span.Length).ToList();
        string raw;
        try
        {
            raw = CollateralChangeGuard.ApplyEdits(fullText, inMember.Select(e => new TextChange(new TextSpan(e.Span.Start - full.Start, e.Span.Length), e.NewText)).ToList());
        }
        catch (ArgumentException)
        {
            error = $"{description} has overlapping in-member edits.";
            return false;
        }

        var rawLines = SplitLines(raw);
        var lead = 0;
        while (lead < rawLines.Count && IsBlank(raw, rawLines[lead]))
        {
            lead++;
        }

        var lastKept = rawLines.Count - 1;
        while (lastKept >= 0 && IsBlank(raw, rawLines[lastKept]))
        {
            lastKept--;
        }

        if (lead > lastKept)
        {
            error = $"{description} has no non-blank text.";
            return false;
        }

        var blockLines = SplitLines(region.MovedText);
        if (blockLines.Count != lastKept - lead + 1)
        {
            error = $"{description} does not match its moved text (line count differs).";
            return false;
        }

        for (var k = 0; k < blockLines.Count; k++)
        {
            if (!string.Equals(raw.Substring(rawLines[lead + k].Start, rawLines[lead + k].Length).Trim(), region.MovedText.Substring(blockLines[k].Start, blockLines[k].Length).Trim(), StringComparison.Ordinal))
            {
                error = $"{description} does not match its moved text (line {k + 1} differs).";
                return false;
            }
        }

        if (!documents.TryGetValue(region.TargetDocument, out var target))
        {
            error = $"{description} has a target document with no insertion edit.";
            return false;
        }

        for (var i = 0; i < target.Edits.Length; i++)
        {
            var edit = target.Edits[i];
            if (edit.Span.Length != 0 || edit.Span.Start != region.TargetInsertionOffset || edit.NewText == null)
            {
                continue;
            }

            cursors.TryGetValue((region.TargetDocument, i), out var cursor);
            var blockOffset = edit.NewText.IndexOf(region.MovedText, cursor, StringComparison.Ordinal);
            if (blockOffset < 0)
            {
                continue;
            }

            cursors[(region.TargetDocument, i)] = blockOffset + region.MovedText.Length;
            moved = new MovedMember(region, raw, rawLines, lead, lastKept, blockLines, startLine.LineNumber, target.NewStartOf(i) + blockOffset, inMember);
            error = null;
            return true;
        }

        error = $"{description} was not found in an insertion edit at offset {region.TargetInsertionOffset} of the target document.";
        return false;
    }

    private static List<TextSpan> SplitLines(string text)
    {
        var lines = new List<TextSpan>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
            {
                lines.Add(TextSpan.FromBounds(start, i));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        lines.Add(TextSpan.FromBounds(start, text.Length));
        return lines;
    }

    private static bool IsBlank(string text, TextSpan line) => text.AsSpan(line.Start, line.Length).IsWhiteSpace();

    private static int LeadingWhitespace(string text, TextSpan line)
    {
        var count = 0;
        while (count < line.Length && (text[line.Start + count] == ' ' || text[line.Start + count] == '\t'))
        {
            count++;
        }

        return count;
    }

    private static bool HasBreak(string? text) => text != null && text.IndexOfAny(['\r', '\n']) >= 0;

    private static bool EndsWithBreak(string? text) => !string.IsNullOrEmpty(text) && (text[^1] == '\n' || text[^1] == '\r');

    private static string Excerpt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= CollateralChangeGuard.MaxExcerptChars ? trimmed : trimmed.Substring(0, CollateralChangeGuard.MaxExcerptChars);
    }

    private sealed class DocumentEdits
    {
        private readonly int[] _cumulativeBefore;

        public DocumentEdits(SourceText original, TextChange[] sortedEdits)
        {
            Original = original;
            Edits = sortedEdits;
            _cumulativeBefore = new int[sortedEdits.Length];
            var total = 0;
            for (var i = 0; i < sortedEdits.Length; i++)
            {
                _cumulativeBefore[i] = total;
                total += (sortedEdits[i].NewText?.Length ?? 0) - sortedEdits[i].Span.Length;
            }
        }

        public SourceText Original { get; }

        public TextChange[] Edits { get; }

        /// <summary>Start of edit <paramref name="index"/>'s text in the trial text.</summary>
        public int NewStartOf(int index) => Edits[index].Span.Start + _cumulativeBefore[index];

        /// <summary>Net length delta of every edit that ends at or before <paramref name="position"/>.</summary>
        public int ShiftBefore(int position)
        {
            var shift = 0;
            foreach (var edit in Edits)
            {
                if (edit.Span.End <= position)
                {
                    shift += (edit.NewText?.Length ?? 0) - edit.Span.Length;
                }
            }

            return shift;
        }

        /// <summary>Index of the non-empty edit whose span contains <paramref name="position"/>, or -1.</summary>
        public int FindContainingEdit(int position)
        {
            for (var i = 0; i < Edits.Length; i++)
            {
                if (Edits[i].Span.Length > 0 && Edits[i].Span.Start <= position && position < Edits[i].Span.End)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    private sealed class MovedMember
    {
        public MovedMember(MovedMemberRegion region, string raw, List<TextSpan> rawLines, int lead, int lastKept, List<TextSpan> blockLines, int startLineIndex, int destinationBlockStart, List<TextChange> inMemberEdits)
        {
            Region = region;
            Raw = raw;
            RawLines = rawLines;
            Lead = lead;
            LastKept = lastKept;
            BlockLines = blockLines;
            StartLineIndex = startLineIndex;
            DestinationBlockStart = destinationBlockStart;
            InMemberEdits = inMemberEdits;
        }

        public MovedMemberRegion Region { get; }

        /// <summary>The member's text with the in-member edits applied (what BuildMovedMemberText starts from).</summary>
        public string Raw { get; }

        public List<TextSpan> RawLines { get; }

        /// <summary>Count of leading blank raw lines the engine trims.</summary>
        public int Lead { get; }

        /// <summary>Index of the last non-blank raw line (later lines are trimmed).</summary>
        public int LastKept { get; }

        public List<TextSpan> BlockLines { get; }

        /// <summary>0-based line index in the source original text of the member's first line.</summary>
        public int StartLineIndex { get; }

        /// <summary>Offset of the block's first character in the target document's trial text.</summary>
        public int DestinationBlockStart { get; }

        /// <summary>In-member edits, sorted, in the source document's original coordinates.</summary>
        public List<TextChange> InMemberEdits { get; }
    }
}
