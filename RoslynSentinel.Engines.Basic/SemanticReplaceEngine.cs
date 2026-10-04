using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Engine for semantic find-replace operations on symbols.</summary>
public class SemanticReplaceEngine
{
    private readonly IWorkspaceReader _workspaceManager;

    /// <summary>Initializes a new instance of the SemanticReplaceEngine.</summary>
    public SemanticReplaceEngine(IWorkspaceReader workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>Resolves a docCommentId to a bool property or field symbol.</summary>
    /// <returns>A tuple of (ISymbol, ResultError); exactly one is non-null. Error cases: NotFound if the id does not resolve, InvalidArgument if the symbol is not a bool property/field, TargetIneligible if it is virtual/override/abstract or has a custom accessor.</returns>
    public async Task<(ISymbol? Symbol, ResultError? Error)> ResolveBoolMemberAsync(string docCommentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(docCommentId))
        {
            return (null, new ResultError(ToolErrorCode.InvalidArgument, "docCommentId cannot be empty."));
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, ct);
        ISymbol? symbol = null;

        // Try to resolve the symbol across all projects
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docCommentId, compilation);
            if (symbol is not null)
            {
                break;
            }
        }

        if (symbol is null)
        {
            return (null, new ResultError(ToolErrorCode.NotFound, $"No symbol found for docCommentId: {docCommentId}"));
        }

        // Check if symbol is a property or field
        if (symbol is not IPropertySymbol and not IFieldSymbol)
        {
            var kindName = symbol.Kind switch
            {
                SymbolKind.Method => "method",
                SymbolKind.NamedType => "type",
                SymbolKind.Namespace => "namespace",
                _ => symbol.Kind.ToString().ToLowerInvariant()
            };
            return (null, new ResultError(ToolErrorCode.InvalidArgument, $"Symbol is a {kindName}, not a property or field."));
        }

        // Check if it is a bool property or field
        var type = symbol switch
        {
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        if (type is null || type.SpecialType != SpecialType.System_Boolean)
        {
            var typeName = type?.Name ?? "unknown";
            return (null, new ResultError(ToolErrorCode.InvalidArgument, $"Symbol is not a bool type; it is {typeName}."));
        }

        // Check for virtual/override/abstract
        if (symbol is IPropertySymbol propSymbol)
        {
            if (propSymbol.IsVirtual || propSymbol.IsOverride || propSymbol.IsAbstract)
            {
                return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property is virtual, override, or abstract; coordinated edits would be needed."));
            }

            // Check for expression-bodied properties (public bool X => ...)
            if (propSymbol.DeclaringSyntaxReferences.Length > 0)
            {
                var propSyntax = await propSymbol.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (propSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax propDecl && propDecl.ExpressionBody != null)
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property is expression-bodied; inverting a computed property is not meaningful."));
                }
            }

            // Check for custom accessor bodies (includes expression-bodied accessors)
            if (propSymbol.GetMethod != null && propSymbol.GetMethod.DeclaringSyntaxReferences.Length > 0)
            {
                var getSyntax = await propSymbol.GetMethod.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (getSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax accessor &&
                    (accessor.Body != null || accessor.ExpressionBody != null))
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property has a custom getter body; inverting a computed property is not meaningful."));
                }
            }

            if (propSymbol.SetMethod != null && propSymbol.SetMethod.DeclaringSyntaxReferences.Length > 0)
            {
                var setSyntax = await propSymbol.SetMethod.DeclaringSyntaxReferences[0].GetSyntaxAsync(ct);
                if (setSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax accessor &&
                    (accessor.Body != null || accessor.ExpressionBody != null))
                {
                    return (null, new ResultError(ToolErrorCode.TargetIneligible, "Property has a custom setter body; inverting a computed property is not meaningful."));
                }
            }
        }

        return (symbol, null);
    }
}
