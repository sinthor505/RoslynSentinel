using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
    /// </summary>
    public static TextEdit BuildAddEdit(int editIndex, SyntaxNode target, AttributeListSyntax newList, SourceText text, string eol)
    {
        var existing = GetAttributeLists(target);
        var anchor = existing.Count > 0 ? existing[existing.Count - 1].GetLastToken().GetNextToken() : target.GetFirstToken();
        var attributeText = newList.NormalizeWhitespace().ToString();

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

    /// <summary>Builds the in-place replacement of one attribute (the text between its brackets/commas only).</summary>
    public static TextEdit BuildReplaceEdit(int editIndex, AttributeSyntax oldAttribute, AttributeSyntax newAttribute)
    {
        return new TextEdit(oldAttribute.SpanStart, oldAttribute.Span.End, newAttribute.NormalizeWhitespace().ToString(), editIndex);
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
                error = $"edits[{maxEndOwner}] and edits[{edit.EditIndex}] change overlapping source text. Split these into separate calls.";
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
