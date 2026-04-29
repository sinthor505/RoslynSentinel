using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SearchResult(string FilePath, string MemberName, string Detail);

public class SemanticSearchEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SemanticSearchEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Finds all methods that have a specific return type.
    /// </summary>
    public async Task<List<SearchResult>> FindMethodsByReturnTypeAsync(string returnType, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<SearchResult>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var methods = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.ReturnType.ToString().Contains(returnType));

                if (methods != null)
                {
                    results.AddRange(methods.Select(m => new SearchResult(document.FilePath ?? "", m.Identifier.Text, $"Returns {m.ReturnType}")));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Finds all types decorated with a specific attribute.
    /// </summary>
    public async Task<List<SearchResult>> FindTypesByAttributeAsync(string attributeName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<SearchResult>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var types = root?.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .Where(t => t.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString().Contains(attributeName))));

                if (types != null)
                {
                    results.AddRange(types.Select(t => new SearchResult(document.FilePath ?? "", t.Identifier.Text, "Has target attribute")));
                }
            }
        }
        return results;
    }
}
