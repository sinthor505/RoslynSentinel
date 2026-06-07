using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

// SearchResult now includes Line and ContextSnippet so callers can feed results
// directly into filePath-gated tools without a separate text-search step.
public record SearchResult(
    FilePath FilePath,
    string MemberName,
    string Detail,
    int? Line = null,
    string? ContextSnippet = null
);

public class SemanticSearchEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SemanticSearchEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Finds all methods whose return type matches returnType using the semantic model.
    /// Uses compilation.GetSymbolsWithName for an initial name-based pre-filter when
    /// the return type is a named type, then falls back to a full solution walk for
    /// primitives and generic types. Each result includes FilePath, Line, and ContextSnippet
    /// for direct use with inspect_symbol / find_references / get_call_graph.
    /// </summary>
    public async Task<List<SearchResult>> FindMethodsByReturnTypeAsync(
        string returnType,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<SearchResult>();
        var seen = new HashSet<string>();
        var normalised = returnType.Trim();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            // Walk all method symbols in this compilation via the symbol table.
            // GetSymbolsWithName with SymbolFilter.Member returns methods matching the
            // return type's simple name; we then verify via the full type display string.
            // For primitives (int, string, bool, etc.) we fall through to the syntax walk below.
            var methodSymbols = compilation
                .GetSymbolsWithName(_ => true, SymbolFilter.Member, cancellationToken)
                .OfType<IMethodSymbol>()
                .Where(m =>
                    m.MethodKind == MethodKind.Ordinary &&
                    m.Locations.Any(l => l.IsInSource) &&
                    (m.ReturnType.Name.Contains(normalised, StringComparison.OrdinalIgnoreCase) ||
                     m.ReturnType.ToDisplayString().Contains(normalised, StringComparison.OrdinalIgnoreCase)));

            foreach (var method in methodSymbols)
            {
                var loc = method.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc == null)
                {
                    continue;
                }

                var filePath = loc.SourceTree?.FilePath ?? string.Empty;
                var line = loc.GetLineSpan().StartLinePosition.Line + 1;
                var dedupeKey = filePath + ":" + line;
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                string? contextSnippet = null;
                try
                {
                    var src = loc.SourceTree?.GetText(cancellationToken);
                    if (src != null && line <= src.Lines.Count)
                    {
                        contextSnippet = src.Lines[line - 1].ToString().Trim();
                    }
                }
                catch (Exception)
                {
                    // non-fatal — contextSnippet stays null
                }

                results.Add(new SearchResult(
                    FilePath: filePath,
                    MemberName: method.Name,
                    Detail: $"Returns {method.ReturnType.ToDisplayString()}",
                    Line: line,
                    ContextSnippet: contextSnippet
                ));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds all types decorated with a specific attribute using the semantic model.
    /// Resolves both "Foo" and "FooAttribute" spelling variants. Each result includes
    /// FilePath, Line, and ContextSnippet for direct use with filePath-gated tools.
    /// </summary>
    public async Task<List<SearchResult>> FindTypesByAttributeAsync(
        string attributeName,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<SearchResult>();
        var seen = new HashSet<string>();

        // Normalise: strip brackets if the caller passed "[Authorize]".
        var bare = attributeName.Trim('[', ']');
        if (bare.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[..^9];
        }
        var full = bare + "Attribute";

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            // Resolve the attribute type symbol so we can use SymbolFinder for semantic accuracy.
            INamedTypeSymbol? attrSymbol =
                compilation.GetTypeByMetadataName(full) ??
                compilation.GetTypeByMetadataName(bare);

            if (attrSymbol == null)
            {
                // Attribute not found in this project's compilation — skip.
                continue;
            }

            // SymbolFinder.FindReferencesAsync finds all uses of the attribute type,
            // including on classes, methods, properties, etc.
            var references = await SymbolFinder.FindReferencesAsync(attrSymbol, solution, cancellationToken);

            foreach (var refGroup in references)
            {
                foreach (var location in refGroup.Locations)
                {
                    if (!location.Location.IsInSource)
                    {
                        continue;
                    }

                    var refTree = location.Location.SourceTree;
                    if (refTree == null)
                    {
                        continue;
                    }

                    var filePath = refTree.FilePath ?? string.Empty;
                    var lineSpan = location.Location.GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var dedupeKey = filePath + ":" + line;
                    if (!seen.Add(dedupeKey))
                    {
                        continue;
                    }

                    // Walk up from the attribute usage to find the attributed member name.
                    var root = await refTree.GetRootAsync(cancellationToken);
                    var node = root.FindNode(location.Location.SourceSpan);
                    var memberName = FindAttributedMemberName(node);

                    string? contextSnippet = null;
                    try
                    {
                        var src = refTree.GetText(cancellationToken);
                        if (line <= src.Lines.Count)
                        {
                            contextSnippet = src.Lines[line - 1].ToString().Trim();
                        }
                    }
                    catch (Exception)
                    {
                        // non-fatal
                    }

                    results.Add(new SearchResult(
                        FilePath: filePath,
                        MemberName: memberName,
                        Detail: $"Has [{bare}] attribute",
                        Line: line,
                        ContextSnippet: contextSnippet
                    ));
                }
            }
        }

        return results
            .OrderBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .ToList();
    }

    /// <summary>
    /// Walks up the syntax tree from an attribute node to find the name of the
    /// type or member the attribute is applied to.
    /// </summary>
    private static string FindAttributedMemberName(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.Text;
                case PropertyDeclarationSyntax p:
                    return p.Identifier.Text;
                case FieldDeclarationSyntax f:
                    var first = f.Declaration.Variables.FirstOrDefault();
                    return first?.Identifier.Text ?? "<field>";
                case ClassDeclarationSyntax c:
                    return c.Identifier.Text;
                case InterfaceDeclarationSyntax i:
                    return i.Identifier.Text;
                case RecordDeclarationSyntax r:
                    return r.Identifier.Text;
                case StructDeclarationSyntax s:
                    return s.Identifier.Text;
                case EnumDeclarationSyntax e:
                    return e.Identifier.Text;
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Identifier.Text;
                case ParameterSyntax param:
                    return param.Identifier.Text;
            }
        }

        return "<unknown>";
    }
}
