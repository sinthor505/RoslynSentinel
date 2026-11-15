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
    public static Task<NewContentIndentResult> ReindentAsync(
        Document document, IReadOnlyList<TextChange> edits, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 6)
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
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    /// <summary>
    /// Derives per-line indents by shifting each inserted line by the indent delta between the edit's original anchor
    /// line and its new position (fallback when the edited text does not parse).
    /// </summary>
    internal static IReadOnlyDictionary<int, string> IndentsFromShift(
        SourceText originalText, SourceText splicedText, IReadOnlyList<TextChange> edits,
        IReadOnlyList<TextSpan> insertedSpans, IReadOnlyList<int> eligibleLines, (bool UseTabs, int Width) unit)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
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
