using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Builds minimal text-span edits for attribute add/remove/replace so that an attribute change touches only
/// the attribute text itself. Every edit is expressed against ONE original <see cref="SourceText"/>, so edits
/// whose targets are ancestor/descendant (a type and a member inside it) compose by construction, and nothing
/// outside the edited spans is re-formatted.
/// <para>
/// This replaces the earlier "rebuild the whole target node, then Formatter.Format it" approach, which
/// (a) rebuilt a type from its ORIGINAL node and so overwrote a nested member edit made in the same batch, and
/// (b) re-formatted every unrelated member of a type that only needed one attribute line inserted. See
/// docs/current/blockers/resolved/blocking_error_modifyattribute_batch_drops_nested_edit_and_reformats_type_body.md.
/// </para>
/// </summary>
public static class AttributeTextEditBuilder
{
    /// <summary>One replacement of <c>[Start, End)</c> in the original text; a zero-length span is a pure insertion.</summary>
    public readonly record struct TextEdit(int Start, int End, string NewText, int EditIndex);

    /// <summary>Parses attribute source (with or without surrounding brackets) into an attribute list, or null if none was produced.</summary>
    public static AttributeListSyntax? ParseAttributeList(string attributeSource)
    {
        var normalized = attributeSource.Trim();
        if (!normalized.StartsWith('['))
        {
            normalized = $"[{normalized}]";
        }

        var snippet = SyntaxFactory.ParseCompilationUnit($"{normalized}\npublic class __Dummy__ {{}}");
        return snippet.DescendantNodes().OfType<AttributeListSyntax>().FirstOrDefault();
    }

    /// <summary>
    /// Builds the insertion of <paramref name="newList"/> as a new attribute list on <paramref name="target"/>, after any
    /// existing attribute lists and after any doc comment. When the declaration keyword starts its own line the new
    /// list gets its own line at the same indentation; otherwise it is inserted inline in front of the keyword.
    /// Throws <see cref="ToolInvalidArgumentException"/> when the attribute text contains comments.
    /// </summary>
    public static TextEdit BuildAddEdit(int editIndex, SyntaxNode target, AttributeListSyntax newList, SourceText text, string eol)
    {
        var existing = GetAttributeLists(target);
        var anchor = existing.Count > 0 ? existing[existing.Count - 1].GetLastToken().GetNextToken() : target.GetFirstToken();
        var attributeText = NormalizeAttributeText(newList, eol);

        var line = text.Lines.GetLineFromPosition(anchor.SpanStart);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, anchor.SpanStart));
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return new TextEdit(line.Start, line.Start, prefix + attributeText + eol, editIndex);
        }

        return new TextEdit(anchor.SpanStart, anchor.SpanStart, attributeText + " ", editIndex);
    }

    /// <summary>
    /// Builds the edits that remove every attribute matching <paramref name="matches"/> from <paramref name="target"/>.
    /// A list left empty is removed whole (its line too, when it stood alone on the line); a list that keeps some
    /// attributes is rewritten in place. Returns an empty list when nothing matched.
    /// </summary>
    public static List<TextEdit> BuildRemoveEdits(int editIndex, MemberDeclarationSyntax target, Func<AttributeSyntax, bool> matches, SourceText text)
    {
        var edits = new List<TextEdit>();
        foreach (var list in target.AttributeLists)
        {
            var remaining = list.Attributes.Where(a => !matches(a)).ToList();
            if (remaining.Count == list.Attributes.Count)
            {
                continue;
            }

            if (remaining.Count == 0)
            {
                edits.Add(BuildRemoveWholeListEdit(editIndex, list, text));
                continue;
            }

            var targetSpecifier = list.Target != null ? list.Target.ToString() + " " : string.Empty;
            var rebuilt = "[" + targetSpecifier + string.Join(", ", remaining.Select(a => a.ToString())) + "]";
            edits.Add(new TextEdit(list.SpanStart, list.Span.End, rebuilt, editIndex));
        }

        return edits;
    }

    /// <summary>
    /// Builds the in-place replacement of one attribute (the text between its brackets/commas only), using the
    /// document's line ending <paramref name="eol"/> (detect it once per document, not per edit). Throws
    /// <see cref="ToolInvalidArgumentException"/> when the new attribute text contains comments. When
    /// <paramref name="newAttribute"/> still sits in the list it was parsed from, the whole list is checked, because a
    /// comment on the list's brackets or on a sibling attribute is not part of the attribute node and would otherwise
    /// be dropped silently.
    /// </summary>
    public static TextEdit BuildReplaceEdit(int editIndex, AttributeSyntax oldAttribute, AttributeSyntax newAttribute, string eol)
    {
        if (newAttribute.Parent is AttributeListSyntax parsedList)
        {
            _ = NormalizeAttributeText(parsedList, eol);
        }

        var attributeText = NormalizeAttributeText(newAttribute, eol);
        return new TextEdit(oldAttribute.SpanStart, oldAttribute.Span.End, attributeText, editIndex);
    }

    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="text"/>. Returns null with <paramref name="error"/> set when two
    /// edits touch overlapping spans (never a partial result).
    /// </summary>
    public static string? TryApply(SourceText text, IReadOnlyList<TextEdit> edits, out string? error)
    {
        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EditIndex).ToList();

        var maxEnd = -1;
        var maxEndOwner = -1;
        foreach (var edit in ordered)
        {
            if (edit.Start < maxEnd && edit.EditIndex != maxEndOwner)
            {
                error = $"batchEdits[{maxEndOwner}] and batchEdits[{edit.EditIndex}] change overlapping source text. Split these into separate calls.";
                return null;
            }

            if (edit.End > maxEnd)
            {
                maxEnd = edit.End;
                maxEndOwner = edit.EditIndex;
            }
        }

        var builder = new StringBuilder(text.Length + 64);
        var position = 0;
        foreach (var edit in ordered)
        {
            builder.Append(text.ToString(TextSpan.FromBounds(position, edit.Start)));
            builder.Append(edit.NewText);
            position = edit.End;
        }

        builder.Append(text.ToString(TextSpan.FromBounds(position, text.Length)));
        error = null;
        return builder.ToString();
    }

    private const string CommentsNotAllowedMessage = "attribute text must not contain comments; remove the comment and retry";

    /// <summary>
    /// Renders caller-supplied attribute text (parsed inside a dummy class) with canonical whitespace. The text never
    /// came from a file, so its whitespace and end-of-line trivia are discarded and <c>NormalizeWhitespace</c> rebuilds
    /// the spacing. Comments are kept by <see cref="RoslynFormattingHelper.StripFormattingTriviaForNormalize{T}"/>
    /// wherever they sit (boundary and inner alike), so the new-node guard rejects every one of them; the guard's
    /// <see cref="ArgumentException"/> is turned into a <see cref="ToolInvalidArgumentException"/> with an actionable
    /// message, so no internal API name reaches the caller. Only the guard's own rejection (parameter <c>node</c>) is
    /// translated; a bad <paramref name="eol"/> still surfaces as the programming error it is.
    /// </summary>
    private static string NormalizeAttributeText(SyntaxNode node, string eol)
    {
        var detached = RoslynFormattingHelper.StripFormattingTriviaForNormalize(node);
        try
        {
            return RoslynFormattingHelper.NormalizeNewChildWhitespace(detached, eol).ToString();
        }
        catch (ArgumentException ex) when (ex.ParamName == "node")
        {
            throw new ToolInvalidArgumentException(CommentsNotAllowedMessage);
        }
    }

    private static SyntaxList<AttributeListSyntax> GetAttributeLists(SyntaxNode target)
    {
        return target is MemberDeclarationSyntax member ? member.AttributeLists : default;
    }

    private static TextEdit BuildRemoveWholeListEdit(int editIndex, AttributeListSyntax list, SourceText text)
    {
        var startLine = text.Lines.GetLineFromPosition(list.SpanStart);
        var endLine = text.Lines.GetLineFromPosition(list.Span.End);
        var before = text.ToString(TextSpan.FromBounds(startLine.Start, list.SpanStart));
        var after = text.ToString(TextSpan.FromBounds(list.Span.End, endLine.End));
        if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
        {
            return new TextEdit(startLine.Start, endLine.EndIncludingLineBreak, string.Empty, editIndex);
        }

        var end = list.Span.End;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
        {
            end++;
        }

        return new TextEdit(list.SpanStart, end, string.Empty, editIndex);
    }
}
