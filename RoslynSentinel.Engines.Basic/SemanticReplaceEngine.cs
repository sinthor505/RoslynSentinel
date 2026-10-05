using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Information about a symbol's declaration, including identifier and initializer spans.</summary>
public sealed record DeclarationInfo(string FilePath, TextSpan IdentifierSpan, TextSpan? InitializerValueSpan, string? InitializerText);

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

    /// <summary>Describes a symbol's declaration, including identifier and initializer spans.</summary>
    /// <returns>DeclarationInfo if symbol has a single source declaration, null otherwise.</returns>
    public static DeclarationInfo? DescribeDeclaration(ISymbol symbol, CancellationToken cancellationToken = default)
    {
        if (symbol == null)
        {
            return null;
        }

        // Handle property symbols
        if (symbol is IPropertySymbol propSymbol)
        {
            if (propSymbol.DeclaringSyntaxReferences.Length != 1)
            {
                return null;
            }

            var syntaxRef = propSymbol.DeclaringSyntaxReferences[0];
            var syntax = syntaxRef.GetSyntax(cancellationToken);

            if (syntax is PropertyDeclarationSyntax propDecl)
            {
                var filePath = syntaxRef.SyntaxTree?.FilePath ?? string.Empty;
                var identifierSpan = propDecl.Identifier.Span;

                // Check for initializer (e.g., { get; set; } = true;)
                TextSpan? initializerValueSpan = null;
                string? initializerText = null;

                if (propDecl.Initializer?.Value != null)
                {
                    initializerValueSpan = propDecl.Initializer.Value.Span;
                    initializerText = propDecl.Initializer.Value.ToString();
                }

                return new DeclarationInfo(filePath, identifierSpan, initializerValueSpan, initializerText);
            }
        }

        // Handle field symbols
        if (symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.DeclaringSyntaxReferences.Length != 1)
            {
                return null;
            }

            var syntaxRef = fieldSymbol.DeclaringSyntaxReferences[0];
            var syntax = syntaxRef.GetSyntax(cancellationToken);

            if (syntax is VariableDeclaratorSyntax varDecl)
            {
                var filePath = syntaxRef.SyntaxTree?.FilePath ?? string.Empty;
                var identifierSpan = varDecl.Identifier.Span;

                // Check for initializer (e.g., = true;)
                TextSpan? initializerValueSpan = null;
                string? initializerText = null;

                if (varDecl.Initializer?.Value != null)
                {
                    initializerValueSpan = varDecl.Initializer.Value.Span;
                    initializerText = varDecl.Initializer.Value.ToString();
                }

                return new DeclarationInfo(filePath, identifierSpan, initializerValueSpan, initializerText);
            }
        }

        return null;
    }

    /// <summary>Validates that newName is a valid C# identifier and not already in use.</summary>
    /// <returns>null if valid; ResultError with InvalidArgument code if invalid.</returns>
    public static ResultError? ValidateNewName(ISymbol symbol, string newName)
    {
        // Check if newName is null, empty, or whitespace
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's a valid identifier
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's a keyword
        if (SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None)
        {
            return new ResultError(ToolErrorCode.InvalidArgument, "newName must be a valid C# identifier. Example: 'IsError'.");
        }

        // Check if it's identical to the current name
        if (newName == symbol.Name)
        {
            return new ResultError(ToolErrorCode.InvalidArgument, $"newName '{newName}' is identical to the current name; nothing to do.");
        }

        // Check if it collides with another member in the containing type
        if (symbol.ContainingType != null)
        {
            var collidingMembers = symbol.ContainingType.GetMembers(newName);
            if (collidingMembers.Length > 0)
            {
                var collidingMember = collidingMembers[0];
                return new ResultError(ToolErrorCode.InvalidArgument, 
                    $"newName '{newName}' collides with existing member '{collidingMember.Name}' in type '{symbol.ContainingType.Name}'.");
            }
        }

        return null;
    }
}
