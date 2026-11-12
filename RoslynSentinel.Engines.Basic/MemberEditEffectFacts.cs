using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Gathers the facts an <see cref="EditEffect"/> needs for Member add and remove, before the write: what the symbol was called and
/// where it lived. Never throws and never blocks the edit; when the target does not resolve to a symbol the names given by the
/// caller are used and the doc id stays null.
/// </summary>
public static class MemberEditEffectFacts
{
    /// <summary>
    /// Describes the declaration a Member(remove) call is about to remove, read from the committed <paramref name="solution"/>:
    /// the container-qualified display name, the documentation comment id (null when the declaration did not resolve to a symbol)
    /// and the simple name. A same-named overload set is resolved with the call's snippets exactly as the removal resolves it.
    /// </summary>
    public static async Task<(string Display, string? DocId, string SimpleName)> DescribeExistingAsync(
        Solution solution,
        FilePathWrapper filePath,
        string memberName,
        string? containerName,
        string? contextSnippet,
        string? lineBefore,
        string? lineAfter,
        CancellationToken cancellationToken)
    {
        var simple = EditEffectNames.SimpleName(memberName);
        var container = EditEffectNames.SimpleName(containerName);
        var fallback = (Display: container.Length == 0 ? simple : $"{container}.{simple}", DocId: (string?)null, SimpleName: simple);
        try
        {
            var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
            if (document is null)
            {
                return fallback;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var sourceText = await document.GetTextAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null)
            {
                return fallback;
            }

            var candidates = SyntaxTargetResolver.FilterByContainingType(
                SyntaxTargetResolver.ResolveCandidates(root, sourceText, memberName, cancellationToken), containerName);
            // A type's own constructors share its name; as in the removal itself, the type wins.
            if (candidates.Any(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum))
            {
                candidates = candidates.Where(c => c.Kind != CandidateKind.Constructor).ToList();
            }

            var node = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, m, mode) => string.Empty)?.Node;
            if (node is null)
            {
                return fallback;
            }

            ISymbol? symbol = node switch
            {
                // The declarators of one field declaration: a same-named pair is CS0102, so RequireSingle finds at most one; no match keeps the first.
                BaseFieldDeclarationSyntax field => model.GetDeclaredSymbol(
                    NameTargetLookup.RequireSingle(field.Declaration.Variables.Where(v => v.Identifier.Text == simple).ToList(), "field", simple) ?? field.Declaration.Variables[0], cancellationToken),
                _ => model.GetDeclaredSymbol(node, cancellationToken),
            };
            return symbol is null ? fallback : (EditEffectNames.Display(symbol), symbol.GetDocumentationCommentId(), simple);
        }
        catch (Exception ex) when (ex is ToolException or InvalidOperationException or ArgumentException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Describes what Member(addMember or addTopLevelType) is about to declare, parsed from the new member's source: one row per
    /// declared name (a field declaration can declare several). <paramref name="isEnumMember"/> parses the source as enum members.
    /// <paramref name="container"/> is the container the member is added to (the namespace for a top-level type, null when not given).
    /// The doc id is not known before the write, so it is null.
    /// </summary>
    // TODO an Added effect carries no SymbolDocId (the declaration is parsed from source before the write); slice 3 matches Added by simple name only, so resolve a doc id after the write if a later slice needs one
    public static IReadOnlyList<(string Display, string SimpleName, string? Container)> DescribeAdded(string? newMemberSource, string? container, bool isEnumMember)
    {
        if (string.IsNullOrWhiteSpace(newMemberSource))
        {
            return [];
        }

        var names = AddedNames(newMemberSource, isEnumMember);
        var containerSimple = string.IsNullOrWhiteSpace(container) ? null : container.Trim();
        var rows = new List<(string, string, string?)>(names.Count);
        foreach (var name in names)
        {
            var simple = EditEffectNames.SimpleName(name);
            if (simple.Length > 0)
            {
                rows.Add((containerSimple is null ? simple : $"{containerSimple}.{simple}", simple, containerSimple));
            }
        }

        return rows;
    }

    private static List<string> AddedNames(string source, bool isEnumMember)
    {
        if (isEnumMember)
        {
            var tree = SyntaxFactory.ParseCompilationUnit("enum E { " + source + " }");
            return tree.DescendantNodes().OfType<EnumMemberDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
        }

        return SyntaxFactory.ParseMemberDeclaration(source) switch
        {
            BaseTypeDeclarationSyntax type => [type.Identifier.Text],
            DelegateDeclarationSyntax del => [del.Identifier.Text],
            MethodDeclarationSyntax method => [method.Identifier.Text],
            ConstructorDeclarationSyntax ctor => [ctor.Identifier.Text],
            PropertyDeclarationSyntax property => [property.Identifier.Text],
            EventDeclarationSyntax evt => [evt.Identifier.Text],
            IndexerDeclarationSyntax => ["this"],
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToList(),
            _ => [],
        };
    }
}
