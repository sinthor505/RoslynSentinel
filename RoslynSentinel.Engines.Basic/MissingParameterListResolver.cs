using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Fallback for a method docCommentId that was written without its parameter list, for example
/// <c>M:Ns.Type.CalcDisc</c> where the real id is <c>M:Ns.Type.CalcDisc(System.Decimal,System.Decimal)</c>. The exact id resolves
/// nothing (a parameterless method's own id has no parentheses, but that id resolves before this runs), so a caller that
/// failed to resolve the id asks here. A unique method of that name on the containing type is used; several overloads are
/// listed with their full ids so the caller can pick one. Syntax-free: it uses the compilation of the named project only.
/// </summary>
/// <remarks>
/// Other tools that resolve through <c>IWorkspaceManager.ResolveFromWireAsync</c> (DeleteUnusedSymbol, PreviewRenameImpact)
/// could call this the same way; only RenameSymbol is wired so far.
/// </remarks>
public static class MissingParameterListResolver
{
    /// <summary>
    /// Looks for the method(s) a parameter-list-less <c>M:</c> id names. Returns (null, empty) when the id is not of that
    /// shape (not <c>M:</c>, already has a parameter list, project or type not found, or no such method), so the caller keeps
    /// its original not-found error.
    /// </summary>
    /// <param name="solution">The committed solution the id would have been resolved in.</param>
    /// <param name="projectName">The project the caller named.</param>
    /// <param name="docCommentId">The id the caller passed.</param>
    /// <returns>
    /// <c>Unique</c>: the single ordinary method of that name on the type. <c>OverloadIds</c>: the full docCommentId of each
    /// overload when there are several (then <c>Unique</c> is null).
    /// </returns>
    public static async Task<(IMethodSymbol? Unique, IReadOnlyList<string> OverloadIds)> FindAsync(
        Solution solution, string projectName, string docCommentId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> none = [];
        if (!docCommentId.StartsWith("M:", StringComparison.Ordinal) || docCommentId.Contains('('))
        {
            return (null, none);
        }

        var qualified = docCommentId[2..];
        var lastDot = qualified.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == qualified.Length - 1)
        {
            return (null, none);
        }

        var project = ProjectLookup.FindProject(solution, projectName);
        var compilation = project is null ? null : await project.GetCompilationAsync(cancellationToken);
        if (compilation is null
            || DocumentationCommentId.GetFirstSymbolForDeclarationId("T:" + qualified[..lastDot], compilation) is not INamedTypeSymbol type)
        {
            return (null, none);
        }

        // A generic method's id carries "``n" after the name; match on the name without it.
        var name = qualified[(lastDot + 1)..];
        var bareName = name.Split('`')[0];
        var methods = type.GetMembers(bareName).OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToList();
        if (methods.Count == 1)
        {
            return (methods[0], none);
        }

        var ids = methods.Select(m => m.GetDocumentationCommentId()).Where(id => id is not null).Select(id => id!).ToList();
        return (null, ids);
    }
}
