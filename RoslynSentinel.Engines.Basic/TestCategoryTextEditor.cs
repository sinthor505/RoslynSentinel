using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>A planned edit the editor could not apply, with the reason (the file is left unchanged for that edit).</summary>
public sealed record TestCategoryEditSkip(PlannedCategoryEdit Edit, string Reason);

/// <summary>Outcome of editing one file's text: the new text plus what was added, removed and skipped.</summary>
public sealed record TestCategoryTextEditResult(
    string NewText,
    int ClassLevelAdded,
    int MethodLevelAdded,
    int RemovedStale,
    IReadOnlyList<TestCategoryEditSkip> Skipped);

/// <summary>
/// Turns the <see cref="PlannedCategoryEdit"/>s of ONE file into new file text. Pure and in-memory: it never touches
/// disk; the caller writes the result through the validated write path.
/// </summary>
/// <remarks>
/// Placement: an added attribute becomes its own line directly above the declaration it belongs to, after any existing
/// attribute lists (so doc comments and the existing attribute block stay untouched), indented like that line and ended
/// with the file's own line ending. If the declaration's last attribute list shares a line with the declaration, the
/// new line goes above that line instead. A removal deletes the whole line(s) of a marked, single-attribute list that
/// stands alone on its line; a marked list that shares its line or its bracket with other attributes is skipped with a
/// reason rather than rewritten. Edits are located by re-parsing, and a removal must still match the planned text, so a
/// plan that no longer fits the file is skipped, never mis-applied.
/// </remarks>
public static class TestCategoryTextEditor
{
    public static TestCategoryTextEditResult Apply(string text, IReadOnlyList<PlannedCategoryEdit> edits)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();
        var source = tree.GetText();
        var eol = DetectEol(text);

        var inserts = new Dictionary<int, List<string>>();
        var removals = new HashSet<int>();
        var skipped = new List<TestCategoryEditSkip>();
        var classAdded = 0;
        var methodAdded = 0;
        var removed = 0;

        var ordered = edits
            .OrderBy(e => e.Line)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.CategoryName, StringComparer.Ordinal)
            .ToList();

        foreach (var edit in ordered)
        {
            if (edit.Kind == TestCategoryEditKind.Add)
            {
                var insertLine = FindInsertionLine(root, edit);
                if (insertLine is null)
                {
                    skipped.Add(new TestCategoryEditSkip(edit,
                        $"No {(edit.Level == TestCategoryLevel.Method ? "test method" : "type")} declaration was found at line {edit.Line}; the file changed since the plan. Re-run the dry run."));
                    continue;
                }

                var indent = LeadingWhitespace(source.Lines[insertLine.Value].ToString());
                if (!inserts.TryGetValue(insertLine.Value, out var list))
                {
                    list = [];
                    inserts[insertLine.Value] = list;
                }

                list.Add(indent + edit.AttributeText + eol);
                if (edit.Level == TestCategoryLevel.Class)
                {
                    classAdded++;
                }
                else
                {
                    methodAdded++;
                }

                continue;
            }

            var removal = TryPlanRemoval(root, source, edit, out var reason);
            if (removal is null)
            {
                skipped.Add(new TestCategoryEditSkip(edit, reason!));
                continue;
            }

            for (var line = removal.Value.First; line <= removal.Value.Last; line++)
            {
                removals.Add(line);
            }

            removed++;
        }

        if (inserts.Count == 0 && removals.Count == 0)
        {
            return new TestCategoryTextEditResult(text, 0, 0, 0, skipped);
        }

        var builder = new StringBuilder(text.Length + 128);
        foreach (var line in source.Lines)
        {
            if (inserts.TryGetValue(line.LineNumber, out var toInsert))
            {
                foreach (var insert in toInsert)
                {
                    builder.Append(insert);
                }
            }

            if (!removals.Contains(line.LineNumber))
            {
                builder.Append(source.ToString(line.SpanIncludingLineBreak));
            }
        }

        return new TestCategoryTextEditResult(builder.ToString(), classAdded, methodAdded, removed, skipped);
    }

    /// <summary>The dominant line ending of <paramref name="text"/>; the platform default when it has none.</summary>
    public static string DetectEol(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return Environment.NewLine;
        }

        if (crlf >= lf && crlf >= cr)
        {
            return "\r\n";
        }

        return lf >= cr ? "\n" : "\r";
    }

    private static int? FindInsertionLine(SyntaxNode root, PlannedCategoryEdit edit)
    {
        MemberDeclarationSyntax? declaration;
        if (edit.Level == TestCategoryLevel.Method)
        {
            declaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == edit.MethodName && LineOf(m.Identifier.GetLocation()) == edit.Line);
        }
        else
        {
            declaration = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault(t => LineOf(t.Identifier.GetLocation()) == edit.Line);
        }

        if (declaration is null)
        {
            return null;
        }

        var lists = declaration.AttributeLists;
        if (lists.Count == 0)
        {
            return ZeroBasedLine(declaration.GetFirstToken().GetLocation());
        }

        var last = lists[lists.Count - 1];
        var firstDeclarationToken = last.GetLastToken().GetNextToken();
        var tokenLine = ZeroBasedLine(firstDeclarationToken.GetLocation());
        var closeLine = ZeroBasedLine(last.CloseBracketToken.GetLocation());
        return closeLine == tokenLine ? ZeroBasedLine(last.OpenBracketToken.GetLocation()) : tokenLine;
    }

    private static (int First, int Last)? TryPlanRemoval(SyntaxNode root, SourceText source, PlannedCategoryEdit edit, out string? reason)
    {
        reason = null;
        var list = root.DescendantNodes().OfType<AttributeListSyntax>()
            .FirstOrDefault(l => LineOf(l.GetLocation()) == edit.Line
                && l.CloseBracketToken.TrailingTrivia.Any(t =>
                    t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().Contains(TestCategoryTaggingEngine.GeneratedMarker, StringComparison.Ordinal)));
        if (list is null)
        {
            reason = $"No marked ({TestCategoryTaggingEngine.GeneratedMarker}) attribute list was found at line {edit.Line}; the file changed since the plan. Re-run the dry run.";
            return null;
        }

        var currentText = (list.ToString() + list.GetTrailingTrivia().ToFullString()).Trim();
        if (!string.Equals(currentText, edit.AttributeText, StringComparison.Ordinal))
        {
            reason = $"The attribute list at line {edit.Line} no longer matches the plan; the file changed since the plan. Re-run the dry run.";
            return null;
        }

        if (list.Attributes.Count != 1)
        {
            reason = $"The marked attribute list at line {edit.Line} also contains other attributes, so it was left as is; remove the stale attribute by hand.";
            return null;
        }

        var span = list.GetLocation().GetLineSpan();
        var first = span.StartLinePosition.Line;
        var last = span.EndLinePosition.Line;
        var prefix = source.Lines[first].ToString()[..(list.SpanStart - source.Lines[first].Start)];
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            reason = $"The marked attribute at line {edit.Line} shares its line with other code, so it was left as is; remove the stale attribute by hand.";
            return null;
        }

        return (first, last);
    }

    private static int LineOf(Location location) => ZeroBasedLine(location) + 1;

    private static int ZeroBasedLine(Location location) => location.GetLineSpan().StartLinePosition.Line;

    private static string LeadingWhitespace(string line)
    {
        var end = 0;
        while (end < line.Length && (line[end] == ' ' || line[end] == '\t'))
        {
            end++;
        }

        return line[..end];
    }
}
