using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Text-edit building blocks for MoveMember. Every helper computes edits against a document's ORIGINAL
/// <see cref="SourceText"/> instead of re-serializing a syntax tree, so everything outside the edited spans
/// (blank lines, wrapped signatures, doc-comment spacing, line endings, final newline) stays byte-identical.
/// See docs/current/blockers/blocking_error_movemember_reformats_entire_source_and_caller_files.md.
/// </summary>
public static class MoveMemberTextEdits
{
    /// <summary>
    /// The span to delete when a member is removed: its full span (leading doc comment and trivia, trailing
    /// line break) plus, when the member had no blank line of its own above it, the one blank line that
    /// follows it - so removing the first member of a class does not leave a blank line after the open brace.
    /// </summary>
    public static TextSpan GetRemovalSpan(SourceText text, MemberDeclarationSyntax member)
    {
        var start = member.FullSpan.Start;
        var end = member.FullSpan.End;
        if (!IsBlankLineStartingAt(text, start) && end < text.Length && IsBlankLineStartingAt(text, end))
        {
            end = text.Lines.GetLineFromPosition(end).EndIncludingLineBreak;
        }

        return TextSpan.FromBounds(start, end);
    }

    /// <summary>Merges overlapping or touching spans so adjacent removals produce non-overlapping edits.</summary>
    public static List<TextSpan> MergeSpans(IEnumerable<TextSpan> spans)
    {
        var merged = new List<TextSpan>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenBy(s => s.Length))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = TextSpan.FromBounds(last.Start, Math.Max(last.End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>
    /// Builds the minimal edit that retargets one reference to a moved static member at <paramref name = "targetClassName"/>.
    /// A qualified reference (<c>A.Norm</c>, a qualified type name, a qualified cref) has only its qualifier span
    /// replaced; a bare reference gets <c>Target.</c> inserted (a zero-length span). Returns null when no safe edit exists
    /// or the qualifier already reads as the target.
    /// </summary>
    public static TextChange? BuildReferenceEdit(SyntaxNode root, TextSpan referenceSpan, string targetClassName)
    {
        var node = root.FindNode(referenceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
        var name = node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault(n => n.Identifier.Span == referenceSpan);
        if (name == null)
        {
            return null;
        }

        switch (name.Parent)
        {
            case MemberAccessExpressionSyntax memberAccess when memberAccess.Name == name:
                return ReplaceQualifier(memberAccess.Expression, targetClassName);
            case QualifiedNameSyntax qualifiedName when qualifiedName.Right == name:
                return ReplaceQualifier(qualifiedName.Left, targetClassName);
            case NameMemberCrefSyntax nameMemberCref:
                if (nameMemberCref.Parent is QualifiedCrefSyntax qualifiedCref && qualifiedCref.Member == nameMemberCref)
                {
                    return ReplaceQualifier(qualifiedCref.Container, targetClassName);
                }

                return new TextChange(new TextSpan(nameMemberCref.SpanStart, 0), targetClassName + ".");
            case MemberBindingExpressionSyntax:
                return null;
            default:
                return new TextChange(new TextSpan(name.SpanStart, 0), targetClassName + ".");
        }
    }

    /// <summary>
    /// The member's source text with <paramref name = "inMemberEdits"/> (spans in the original document) applied,
    /// trimmed of leading blank lines and trailing whitespace/line breaks, with line endings converted to <paramref name = "eol"/>
    /// and its indentation re-based from <paramref name = "fromIndent"/> to <paramref name = "toIndent"/>.
    /// </summary>
    public static string BuildMovedMemberText(SourceText sourceText, MemberDeclarationSyntax member, IReadOnlyList<TextChange> inMemberEdits, string fromIndent, string toIndent, string eol)
    {
        var full = member.FullSpan;
        var raw = sourceText.ToString(full);
        foreach (var edit in inMemberEdits.OrderByDescending(e => e.Span.Start))
        {
            var offset = edit.Span.Start - full.Start;
            raw = raw.Remove(offset, edit.Span.Length).Insert(offset, edit.NewText ?? string.Empty);
        }

        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0)
        {
            lines[^1] = lines[^1].TrimEnd();
        }

        if (fromIndent != toIndent)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > 0 && lines[i].StartsWith(fromIndent, StringComparison.Ordinal))
                {
                    lines[i] = toIndent + lines[i].Substring(fromIndent.Length);
                }
            }
        }

        return string.Join(eol, lines);
    }

    /// <summary>Joins moved-member blocks: a blank line between them, except between two consecutive fields.</summary>
    public static string JoinMovedMembers(IReadOnlyList<MemberDeclarationSyntax> members, IReadOnlyList<string> blocks, string eol)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(eol);
                if (!(members[i] is FieldDeclarationSyntax && members[i - 1] is FieldDeclarationSyntax))
                {
                    sb.Append(eol);
                }
            }

            sb.Append(blocks[i]);
        }

        return sb.ToString();
    }

    /// <summary>The leading whitespace of the line a member's first token sits on.</summary>
    public static string GetMemberIndentation(SourceText text, MemberDeclarationSyntax member)
    {
        return LeadingWhitespaceOfLine(text, member.SpanStart);
    }

    /// <summary>
    /// The indentation new members of <paramref name = "targetClass"/> should use: the first existing member's, or the
    /// class's own indentation plus one level (a tab when the class line is tab-indented, otherwise four spaces).
    /// </summary>
    public static string GetTargetMemberIndentation(SourceText text, ClassDeclarationSyntax targetClass)
    {
        if (targetClass.Members.Count > 0)
        {
            return GetMemberIndentation(text, targetClass.Members[0]);
        }

        var classIndent = LeadingWhitespaceOfLine(text, targetClass.SpanStart);
        return classIndent + (classIndent.Contains('\t') ? "\t" : "    ");
    }

    /// <summary>
    /// The insertion that appends <paramref name = "block"/> to <paramref name = "targetClass"/>: after its last member
    /// (preceded by a blank line) or straight after the open brace of an empty class. Touches nothing else.
    /// </summary>
    public static TextChange BuildInsertion(SourceText targetText, ClassDeclarationSyntax targetClass, string block, string eol)
    {
        var hasMembers = targetClass.Members.Count > 0;
        var position = hasMembers ? targetClass.Members[^1].FullSpan.End : targetClass.OpenBraceToken.FullSpan.End;
        var sb = new StringBuilder();
        if (position > 0 && targetText[position - 1] != '\n')
        {
            sb.Append(eol);
        }

        if (hasMembers)
        {
            sb.Append(eol);
        }

        sb.Append(block).Append(eol);
        return new TextChange(new TextSpan(position, 0), sb.ToString());
    }

    /// <summary>Applies non-overlapping edits to the original text and returns the new full text.</summary>
    public static string ApplyChanges(SourceText text, IEnumerable<TextChange> changes)
    {
        var ordered = changes.OrderBy(c => c.Span.Start).ThenBy(c => c.Span.Length).ToList();
        return ordered.Count == 0 ? text.ToString() : text.WithChanges(ordered).ToString();
    }

    private static TextChange? ReplaceQualifier(SyntaxNode qualifier, string targetClassName)
    {
        return qualifier.ToString() == targetClassName ? null : new TextChange(qualifier.Span, targetClassName);
    }

    private static bool IsBlankLineStartingAt(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        return line.Start == position && line.EndIncludingLineBreak > line.End && string.IsNullOrWhiteSpace(text.ToString(line.Span));
    }

    private static string LeadingWhitespaceOfLine(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var lineText = text.ToString(line.Span);
        var count = 0;
        while (count < lineText.Length && (lineText[count] == ' ' || lineText[count] == '\t'))
        {
            count++;
        }

        return lineText.Substring(0, count);
    }
}
