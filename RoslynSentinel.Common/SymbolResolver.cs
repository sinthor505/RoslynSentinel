using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public class SymbolResolver
{
    private readonly IWorkspaceManager _workspaceManager;

    public SymbolResolver(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == handle.ProjectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null) { return null; }
        ISymbol? resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId, compilation);
        return resolved;
    }

    public async Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null) { return null; }
        return DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
    }
}
