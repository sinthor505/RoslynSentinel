using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>
    /// Builds the source document's edit list for a move: reference edits that fall outside the moved members stay
    /// as they are, reference edits inside them are returned in <paramref name = "insideMovedMemberEdits"/> (they travel
    /// with the member text), and each moved member's removal span is added as a deletion.
    /// </summary>
    public static List<TextChange> BuildSourceEdits(SourceText sourceText, IReadOnlyList<MemberDeclarationSyntax> membersToMove, IEnumerable<TextChange> sourceReferenceEdits, out List<TextChange> insideMovedMemberEdits)
    {
        var all = sourceReferenceEdits.ToList();
        insideMovedMemberEdits = all.Where(e => membersToMove.Any(m => m.FullSpan.Contains(e.Span))).ToList();
        var edits = all.Except(insideMovedMemberEdits).ToList();
        foreach (var removal in MergeSpans(membersToMove.Select(m => GetRemovalSpan(sourceText, m))))
        {
            edits.Add(new TextChange(removal, string.Empty));
        }

        return edits;
    }

    /// <summary>
    /// Text edits (spans in the original document) that adjust a member's modifiers for a pull-up into a base
    /// type: <c>override</c> becomes <c>virtual</c> (or is dropped when the member is already virtual/abstract),
    /// and a member with neither gets <c>virtual</c>. Static members and member kinds other than methods and
    /// properties are left alone, because <c>static virtual</c> is not valid.
    /// </summary>
    public static List<TextChange> BuildPullUpModifierEdits(MemberDeclarationSyntax member)
    {
        var edits = new List<TextChange>();
        SyntaxTokenList modifiers;
        SyntaxNode typeNode;
        switch (member)
        {
            case MethodDeclarationSyntax method:
                modifiers = method.Modifiers;
                typeNode = method.ReturnType;
                break;
            case PropertyDeclarationSyntax property:
                modifiers = property.Modifiers;
                typeNode = property.Type;
                break;
            default:
                return edits;
        }

        if (modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword)))
        {
            return edits;
        }

        var overrideToken = modifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.OverrideKeyword));
        var hasOverride = overrideToken.RawKind != 0;
        var hasVirtualOrAbstract = modifiers.Any(t => t.IsKind(SyntaxKind.VirtualKeyword) || t.IsKind(SyntaxKind.AbstractKeyword));
        if (hasOverride)
        {
            edits.Add(hasVirtualOrAbstract ? new TextChange(TextSpan.FromBounds(overrideToken.SpanStart, overrideToken.FullSpan.End), string.Empty) : new TextChange(overrideToken.Span, "virtual"));
        }
        else if (!hasVirtualOrAbstract)
        {
            edits.Add(modifiers.Count > 0 ? new TextChange(new TextSpan(modifiers[^1].Span.End, 0), " virtual") : new TextChange(new TextSpan(typeNode.SpanStart, 0), "virtual "));
        }

        return edits;
    }

    /// <summary>
    /// The full text of a brand-new file holding one public class made of the moved members. There is no original
    /// text to preserve, so the file is written directly: the source file's usings, the source namespace (file-scoped
    /// or block-scoped as in the source), four-space indentation, <paramref name = "eol"/> line endings and a final line break.
    /// </summary>
    public static string BuildNewClassFileText(IReadOnlyList<string> usingLines, string? namespaceName, bool fileScopedNamespace, string className, SourceText sourceText, IReadOnlyList<MemberDeclarationSyntax> members, IReadOnlyList<TextChange> insideMovedMemberEdits, string eol)
    {
        var blockNamespace = namespaceName != null && !fileScopedNamespace;
        var classIndent = blockNamespace ? "    " : string.Empty;
        var memberIndent = classIndent + "    ";
        var sb = new StringBuilder();
        foreach (var usingLine in usingLines)
        {
            sb.Append(usingLine).Append(eol);
        }

        if (usingLines.Count > 0)
        {
            sb.Append(eol);
        }

        if (namespaceName != null)
        {
            if (fileScopedNamespace)
            {
                sb.Append("namespace ").Append(namespaceName).Append(';').Append(eol).Append(eol);
            }
            else
            {
                sb.Append("namespace ").Append(namespaceName).Append(eol).Append('{').Append(eol);
            }
        }

        var blocks = members.Select(m => BuildMovedMemberText(sourceText, m, insideMovedMemberEdits.Where(e => m.FullSpan.Contains(e.Span)).ToList(), GetMemberIndentation(sourceText, m), memberIndent, eol)).ToList();
        sb.Append(classIndent).Append("public class ").Append(className).Append(eol);
        sb.Append(classIndent).Append('{').Append(eol);
        sb.Append(JoinMovedMembers(members, blocks, eol)).Append(eol);
        sb.Append(classIndent).Append('}').Append(eol);
        if (blockNamespace)
        {
            sb.Append('}').Append(eol);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a position in a document's ORIGINAL text to the same position in the text produced by applying
    /// <paramref name = "edits"/>: every edit that ends at or before the position shifts it by its length delta.
    /// Used to report line numbers of rewritten sites against the final text. The position must not lie inside an edit's span.
    /// </summary>
    public static int MapPositionThroughEdits(IEnumerable<TextChange> edits, int position)
    {
        var shift = 0;
        foreach (var edit in edits)
        {
            if (edit.Span.End <= position && edit.Span.Start < position)
            {
                shift += (edit.NewText?.Length ?? 0) - edit.Span.Length;
            }
        }

        return position + shift;
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
