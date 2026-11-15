using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// Result of re-indenting ReplaceSnippet newContent: <see cref="Text"/> is the spliced, re-indented text,
/// <see cref="InsertedTexts"/> holds each edit's final inserted text in input order, <see cref="LinesAdjusted"/> counts
/// lines whose leading whitespace changed, and <see cref="UsedFallback"/> is true when the delta-shift path ran instead
/// of the formatter.
/// </summary>
public sealed record NewContentIndentResult(string Text, IReadOnlyList<string> InsertedTexts, int LinesAdjusted, bool UsedFallback);

/// <summary>
/// Re-indents the leading whitespace of inserted newContent lines to the file's indentation, so a replacement that
/// was written at one indent level lands at the level of the code it replaces.
/// </summary>
public static class NewContentIndenter
{
    /// <summary>
    /// Splices <paramref name="edits"/> into the pre-edit text of <paramref name="document"/> (edits are in original-text
    /// coordinates, non-overlapping, any order) and re-indents only the leading whitespace of the inserted lines. The
    /// formatter path runs when the edited text parses; the delta-shift fallback runs otherwise. The result keeps the
    /// edits' own line endings; the caller normalizes EOL afterwards.
    /// </summary>
    public static async Task<NewContentIndentResult> ReindentAsync(
        Document document, IReadOnlyList<TextChange> edits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edits);

        var originalText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        // Sort by original position; spans and inserted texts stay in input order via the index map.
        var sortedIndexes = Enumerable.Range(0, edits.Count).OrderBy(index => edits[index].Span.Start).ToArray();
        var spliced = edits.Count == 0 ? originalText : originalText.WithChanges(sortedIndexes.Select(index => edits[index]));
        var plainTexts = edits.Select(edit => edit.NewText ?? string.Empty).ToList();
        if (edits.Count == 0 || !document.SupportsSyntaxTree)
        {
            return new NewContentIndentResult(spliced.ToString(), plainTexts, 0, false);
        }

        // Inserted spans in spliced coordinates (running offset), indexed in input order.
        var insertedSpans = new TextSpan[edits.Count];
        var offset = 0;
        foreach (var index in sortedIndexes)
        {
            var edit = edits[index];
            var length = plainTexts[index].Length;
            insertedSpans[index] = new TextSpan(edit.Span.Start + offset, length);
            offset += length - edit.Span.Length;
        }

        var splicedDocument = document.WithText(spliced);
        var originalRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var splicedRoot = await splicedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (originalRoot is null || splicedRoot is null)
        {
            return new NewContentIndentResult(spliced.ToString(), plainTexts, 0, false);
        }

        IReadOnlyDictionary<int, string>? indents = null;
        if (CanUseFormatter(originalRoot, splicedRoot, insertedSpans))
        {
            var eligible = EligibleLines(spliced, splicedRoot, insertedSpans, skipBlockCommentLines: true);
            indents = await TryIndentsFromFormatterAsync(
                splicedDocument, spliced, splicedRoot, eligible, originalText, cancellationToken).ConfigureAwait(false);
        }

        var usedFallback = indents is null;
        if (indents is null)
        {
            var eligible = EligibleLines(spliced, splicedRoot, insertedSpans, skipBlockCommentLines: false);
            indents = IndentsFromShift(originalText, spliced, edits, insertedSpans, eligible, InferIndentUnit(originalText));
        }

        return ApplyIndents(spliced, insertedSpans, indents, usedFallback);
    }

    /// <summary>
    /// True when the spliced tree has no more syntax errors than the original tree and no syntax error touches an
    /// inserted span (the formatter path; otherwise the delta-shift fallback runs).
    /// </summary>
    private static bool CanUseFormatter(SyntaxNode originalRoot, SyntaxNode splicedRoot, IReadOnlyList<TextSpan> insertedSpans)
    {
        var originalErrors = originalRoot.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var splicedErrors = splicedRoot.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (splicedErrors.Count > originalErrors)
        {
            return false;
        }

        return !splicedErrors.Any(diagnostic => insertedSpans.Any(span => diagnostic.Location.SourceSpan.IntersectsWith(span)));
    }

    /// <summary>
    /// Runs the formatter over the eligible lines and returns the indents from its changes, or null when the formatter
    /// throws (the caller falls back to the delta shift). Cancellation propagates.
    /// </summary>
    private static async Task<IReadOnlyDictionary<int, string>?> TryIndentsFromFormatterAsync(
        Document splicedDocument, SourceText splicedText, SyntaxNode splicedRoot, IReadOnlyList<int> eligibleLines,
        SourceText originalText, CancellationToken cancellationToken)
    {
        if (eligibleLines.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            Microsoft.CodeAnalysis.Options.OptionSet options =
                await splicedDocument.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            // The file's own text is the authority on indentation: override the tab/space options with the inferred
            // unit whenever the file has indented lines; the document options (.editorconfig) apply only otherwise.
            if (HasIndentedLines(originalText))
            {
                var unit = InferIndentUnit(originalText);
                options = options
                    .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, unit.UseTabs)
                    .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, unit.Width)
                    .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, unit.Width);
            }

            // Indentation-only use of the formatter's text changes; nothing else from them is written. The whitespace
            // ratchet (WhitespaceRatchetTests) does not count this call, see the plan's Risks R3.
            var changes = Formatter.GetFormattedTextChanges(
                splicedRoot, EligibleLineSpans(splicedText, eligibleLines), splicedDocument.Project.Solution.Workspace, options, cancellationToken);
            return IndentsFromFormatterChanges(splicedText, eligibleLines, changes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Spans covering the eligible lines, each reaching back to the end of the previous line so the whitespace between
    /// the previous token and the line's first token (line break plus indentation) lies inside a formatted span.
    /// Overlapping spans are merged.
    /// </summary>
    private static List<TextSpan> EligibleLineSpans(SourceText splicedText, IReadOnlyList<int> eligibleLines)
    {
        var spans = new List<TextSpan>();
        foreach (var number in eligibleLines.OrderBy(number => number))
        {
            var line = splicedText.Lines[number];
            var start = number > 0 ? splicedText.Lines[number - 1].End : 0;
            var end = line.EndIncludingLineBreak;
            if (spans.Count > 0 && start <= spans[^1].End)
            {
                spans[^1] = TextSpan.FromBounds(spans[^1].Start, Math.Max(spans[^1].End, end));
            }
            else
            {
                spans.Add(TextSpan.FromBounds(start, end));
            }
        }

        return spans;
    }

    /// <summary>True when at least one non-blank line starts with whitespace.</summary>
    private static bool HasIndentedLines(SourceText text)
    {
        return text.Lines.Any(line => !IsBlank(line) && LeadingWhitespaceLength(line) > 0);
    }

    /// <summary>
    /// Infers the file's indent unit (tabs or spaces, and the width in spaces) from its leading whitespace.
    /// </summary>
    internal static (bool UseTabs, int Width) InferIndentUnit(SourceText originalText)
    {
        var tabLines = 0;
        var spaceLines = 0;
        var diffCounts = new Dictionary<int, int>();
        int? previousWidth = null;

        foreach (var line in originalText.Lines)
        {
            if (IsBlank(line))
            {
                continue;
            }

            if (LeadingWhitespaceLength(line) > 0)
            {
                if (originalText[line.Start] == '\t')
                {
                    tabLines++;
                }
                else
                {
                    spaceLines++;
                }
            }

            var width = LeadingWhitespaceWidth(line, DefaultIndentWidth);
            if (previousWidth is { } previous && width > previous)
            {
                var diff = width - previous;
                diffCounts[diff] = diffCounts.GetValueOrDefault(diff) + 1;
            }

            previousWidth = width;
        }

        if (tabLines + spaceLines == 0)
        {
            return (false, DefaultIndentWidth);
        }

        if (tabLines > spaceLines)
        {
            return (true, DefaultIndentWidth);
        }

        if (diffCounts.Count == 0)
        {
            return (false, DefaultIndentWidth);
        }

        // Most common positive difference; a tie goes to the smaller step.
        var best = diffCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        return (false, Math.Clamp(best, 1, 8));
    }

    /// <summary>
    /// Returns the 0-based line numbers of <paramref name="splicedText"/> whose leading whitespace may be re-indented:
    /// lines that start inside <paramref name="insertedSpans"/>, excluding lines inside string literals and, when
    /// <paramref name="skipBlockCommentLines"/> is set, lines inside block comments.
    /// </summary>
    internal static IReadOnlyList<int> EligibleLines(
        SourceText splicedText, SyntaxNode splicedRoot, IReadOnlyList<TextSpan> insertedSpans, bool skipBlockCommentLines)
    {
        var eligible = new SortedSet<int>();
        foreach (var span in insertedSpans)
        {
            var end = Math.Min(span.End, splicedText.Length);
            if (span.Start >= end)
            {
                continue;
            }

            var firstLine = splicedText.Lines.GetLineFromPosition(span.Start).LineNumber;
            var lastLine = splicedText.Lines.GetLineFromPosition(end - 1).LineNumber;
            for (var number = firstLine; number <= lastLine; number++)
            {
                var line = splicedText.Lines[number];
                if (IsBlank(line))
                {
                    continue;
                }

                // Line 1 of a mid-line insertion: only when the text before the insertion is whitespace-only.
                if (line.Start < span.Start && !IsWhitespaceRange(splicedText, line.Start, span.Start))
                {
                    continue;
                }

                if (LineStartsInsideToken(splicedRoot, line.Start))
                {
                    continue;
                }

                if (skipBlockCommentLines && LineStartsInsideMultiLineComment(splicedRoot, line.Start))
                {
                    continue;
                }

                eligible.Add(number);
            }
        }

        return eligible.ToList();
    }

    /// <summary>
    /// Applies <paramref name="indents"/> (0-based line number to new leading whitespace) to the spliced text and builds
    /// the <see cref="NewContentIndentResult"/>, including the per-edit inserted texts and the adjusted-line count.
    /// </summary>
    internal static NewContentIndentResult ApplyIndents(
        SourceText splicedText, IReadOnlyList<TextSpan> insertedSpans, IReadOnlyDictionary<int, string> indents, bool usedFallback)
    {
        // One change per adjusted line, in ascending line (so ascending position) order; each is the line's leading
        // whitespace, or for line 1 of a mid-line insertion only the inserted part of it.
        var changes = new List<(TextSpan Span, string NewText)>();
        foreach (var (lineNumber, target) in indents.OrderBy(pair => pair.Key))
        {
            if (lineNumber < 0 || lineNumber >= splicedText.Lines.Count)
            {
                continue;
            }

            var line = splicedText.Lines[lineNumber];
            if (IsBlank(line))
            {
                continue;
            }

            var wsEnd = line.Start + LeadingWhitespaceLength(line);
            int rangeStart;
            var newWhitespace = target;
            if (insertedSpans.Any(span => span.Start <= line.Start && line.Start < span.End))
            {
                rangeStart = line.Start;
            }
            else
            {
                // Line 1 of an insertion that starts inside this line's leading whitespace (prefix P before it).
                var span = insertedSpans.FirstOrDefault(s => s.Length > 0 && line.Start < s.Start && s.Start <= wsEnd);
                if (span.Length == 0)
                {
                    continue;
                }

                var prefix = splicedText.ToString(TextSpan.FromBounds(line.Start, span.Start));
                if (!target.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                rangeStart = span.Start;
                newWhitespace = target.Substring(prefix.Length);
            }

            var replaced = TextSpan.FromBounds(rangeStart, wsEnd);
            if (string.Equals(splicedText.ToString(replaced), newWhitespace, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add((replaced, newWhitespace));
        }

        var newText = splicedText.WithChanges(changes.Select(change => new TextChange(change.Span, change.NewText)));
        var insertedTexts = new List<string>(insertedSpans.Count);
        foreach (var span in insertedSpans)
        {
            var start = MapPosition(changes, span.Start);
            var end = Math.Max(start, MapPosition(changes, span.End));
            insertedTexts.Add(newText.ToString(TextSpan.FromBounds(start, end)));
        }

        return new NewContentIndentResult(newText.ToString(), insertedTexts, changes.Count, usedFallback);
    }

    /// <summary>
    /// Derives per-line indents from the formatter's text changes, keeping only changes on
    /// <paramref name="eligibleLines"/>.
    /// </summary>
    internal static IReadOnlyDictionary<int, string> IndentsFromFormatterChanges(
        SourceText splicedText, IReadOnlyList<int> eligibleLines, IEnumerable<TextChange> formatterChanges)
    {
        var indents = new Dictionary<int, string>();
        var eligible = new HashSet<int>(eligibleLines);
        foreach (var change in formatterChanges)
        {
            var span = change.Span;
            if (span.End > splicedText.Length)
            {
                continue;
            }

            var line = splicedText.Lines.GetLineFromPosition(span.End);
            if (!eligible.Contains(line.LineNumber) || IsBlank(line)
                || span.End != line.Start + LeadingWhitespaceLength(line))
            {
                continue;
            }

            var oldText = splicedText.ToString(span);
            var newText = change.NewText ?? string.Empty;
            var oldBreaks = CountLineBreaks(oldText);
            var newBreaks = CountLineBreaks(newText);
            var keepA = newBreaks == 0 && span.Start >= line.Start;
            var keepB = oldBreaks == newBreaks;
            if (!keepA && !keepB)
            {
                continue;
            }

            var lastBreak = newText.LastIndexOfAny(['\r', '\n']);
            var segment = newText.Substring(lastBreak + 1);
            if (segment.Any(character => character != ' ' && character != '\t'))
            {
                continue;
            }

            // A change that starts inside the line's indentation (no line break in the old text) leaves the
            // indentation before it in place. The plan text says "the new text's segment after its last line break";
            // this adds the preserved prefix so a partial-indent change still yields the whole indentation.
            var prefix = oldBreaks == 0 && span.Start > line.Start
                ? splicedText.ToString(TextSpan.FromBounds(line.Start, span.Start))
                : string.Empty;
            indents[line.LineNumber] = prefix + segment;
        }

        return indents;
    }

    /// <summary>
    /// Derives per-line indents by shifting each inserted line by the indent delta between the edit's original anchor
    /// line and its new position (fallback when the edited text does not parse).
    /// </summary>
    internal static IReadOnlyDictionary<int, string> IndentsFromShift(
        SourceText originalText, SourceText splicedText, IReadOnlyList<TextChange> edits,
        IReadOnlyList<TextSpan> insertedSpans, IReadOnlyList<int> eligibleLines, (bool UseTabs, int Width) unit)
    {
        var indents = new Dictionary<int, string>();
        var eligible = new HashSet<int>(eligibleLines);
        var unitWidth = unit.Width > 0 ? unit.Width : DefaultIndentWidth;
        var count = Math.Min(edits.Count, insertedSpans.Count);
        for (var i = 0; i < count; i++)
        {
            var span = insertedSpans[i];
            var end = Math.Min(span.End, splicedText.Length);
            if (span.Start >= end)
            {
                continue;
            }

            var editStart = Math.Min(edits[i].Span.Start, originalText.Length);
            var fileBase = LeadingWhitespaceWidth(originalText.Lines.GetLineFromPosition(editStart), unitWidth);
            var firstLine = splicedText.Lines.GetLineFromPosition(span.Start);
            var lastLineNumber = splicedText.Lines.GetLineFromPosition(end - 1).LineNumber;
            var startsAtLineIndent = IsWhitespaceRange(splicedText, firstLine.Start, span.Start);

            // Widths of the non-blank lines of the insertion; line 1 counts only its inserted part (newContent's own indent).
            var widths = new List<(int Number, int Width)>();
            for (var number = firstLine.LineNumber; number <= lastLineNumber; number++)
            {
                var line = splicedText.Lines[number];
                if (IsBlank(line))
                {
                    continue;
                }

                var lineWidth = number == firstLine.LineNumber
                    ? InsertedLeadingWidth(splicedText, line, span.Start, unitWidth)
                    : LeadingWhitespaceWidth(line, unitWidth);
                widths.Add((number, lineWidth));
            }

            if (startsAtLineIndent)
            {
                if (widths.Count == 0)
                {
                    continue;
                }

                var anchor = widths[0];
                var delta = fileBase - anchor.Width;
                foreach (var (number, lineWidth) in widths)
                {
                    if (eligible.Contains(number))
                    {
                        indents[number] = IndentTargetString(number == anchor.Number ? fileBase : lineWidth + delta, unit);
                    }
                }
            }
            else
            {
                // Mid-line start: line 1 is not touched. Later lines already at or beyond the file base are taken as absolute.
                var later = widths.Where(entry => entry.Number > firstLine.LineNumber && eligible.Contains(entry.Number)).ToList();
                if (later.Count == 0)
                {
                    continue;
                }

                var delta = later.Min(entry => entry.Width) >= fileBase ? 0 : fileBase;
                foreach (var (number, lineWidth) in later)
                {
                    indents[number] = IndentTargetString(lineWidth + delta, unit);
                }
            }
        }

        return indents;
    }

    /// <summary>Builds leading whitespace of <paramref name="width"/> columns in the file's style (tabs plus remaining spaces, or spaces).</summary>
    private static string IndentTargetString(int width, (bool UseTabs, int Width) unit)
    {
        width = Math.Max(width, 0);
        if (!unit.UseTabs)
        {
            return new string(' ', width);
        }

        var unitWidth = unit.Width > 0 ? unit.Width : DefaultIndentWidth;
        return new string('\t', width / unitWidth) + new string(' ', width % unitWidth);
    }

    /// <summary>Width of the whitespace that starts at <paramref name="spanStart"/> (or the line start, if later) on <paramref name="line"/>.</summary>
    private static int InsertedLeadingWidth(SourceText text, TextLine line, int spanStart, int tabWidth)
    {
        var width = 0;
        for (var position = Math.Max(line.Start, spanStart); position < line.End && char.IsWhiteSpace(text[position]); position++)
        {
            width += text[position] == '\t' ? tabWidth : 1;
        }

        return width;
    }

    /// <summary>Counts line breaks (CRLF counts once, lone CR and lone LF once each).</summary>
    private static int CountLineBreaks(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                count++;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (text[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Indent width (and tab width for width arithmetic) used when the file gives no evidence.</summary>
    internal const int DefaultIndentWidth = 4;

    /// <summary>True when the line (without its line break) is empty or whitespace-only.</summary>
    internal static bool IsBlank(TextLine line)
    {
        var text = line.Text!;
        for (var position = line.Start; position < line.End; position++)
        {
            if (!char.IsWhiteSpace(text[position]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Number of characters in the line's leading whitespace (all of it for a blank line).</summary>
    internal static int LeadingWhitespaceLength(TextLine line)
    {
        var text = line.Text!;
        var length = 0;
        while (line.Start + length < line.End && char.IsWhiteSpace(text[line.Start + length]))
        {
            length++;
        }

        return length;
    }

    /// <summary>Width of the line's leading whitespace in columns, each tab counting as <paramref name="tabWidth"/>.</summary>
    internal static int LeadingWhitespaceWidth(TextLine line, int tabWidth)
    {
        var text = line.Text!;
        var width = 0;
        for (var position = line.Start; position < line.End && char.IsWhiteSpace(text[position]); position++)
        {
            width += text[position] == '\t' ? tabWidth : 1;
        }

        return width;
    }

    private static bool IsWhitespaceRange(SourceText text, int start, int end)
    {
        for (var position = start; position < end; position++)
        {
            if (!char.IsWhiteSpace(text[position]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="position"/> lies strictly inside a token (string literals, any multi-line token).</summary>
    private static bool LineStartsInsideToken(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        return token.Span.Start < position && position < token.Span.End;
    }

    /// <summary>True when <paramref name="position"/> lies strictly inside a /* */ or /** */ comment trivia.</summary>
    private static bool LineStartsInsideMultiLineComment(SyntaxNode root, int position)
    {
        var trivia = root.FindTrivia(position);
        return trivia.Kind() is SyntaxKind.MultiLineCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
            && trivia.Span.Start < position && position < trivia.Span.End;
    }

    /// <summary>
    /// Maps a position of the pre-change text to the post-change text; <paramref name="sortedChanges"/> must be
    /// ascending and non-overlapping. A position strictly inside a replaced range maps to the end of its replacement.
    /// </summary>
    internal static int MapPosition(IReadOnlyList<(TextSpan Span, string NewText)> sortedChanges, int position)
    {
        var delta = 0;
        foreach (var (span, newText) in sortedChanges)
        {
            if (position <= span.Start)
            {
                break;
            }

            if (position >= span.End)
            {
                delta += newText.Length - span.Length;
            }
            else
            {
                return span.Start + delta + newText.Length;
            }
        }

        return position + delta;
    }
}
