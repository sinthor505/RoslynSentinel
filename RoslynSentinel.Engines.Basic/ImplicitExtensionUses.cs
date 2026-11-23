using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The static classes of extension methods that a file reaches through syntax with no name node of their own: the GetEnumerator / GetAsyncEnumerator of a
/// foreach, the GetAwaiter of an await, the Add of a collection initializer and the Deconstruct of a deconstruction. A name walk (SimpleNameSyntax) never
/// sees these, so a using directive needed only for them would be dropped from a copy or from a rewritten caller.
/// </summary>
internal static class ImplicitExtensionUses
{
    /// <summary>The distinct static classes (original definitions) holding the extension methods <paramref name="root"/> reaches without a name node.</summary>
    public static List<INamedTypeSymbol> Collect(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
    {
        var methods = new List<IMethodSymbol?>();
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case ForEachVariableStatementSyntax forEachVariable:
                    methods.Add(model.GetForEachStatementInfo(forEachVariable).GetEnumeratorMethod);
                    AddDeconstruction(methods, model.GetDeconstructionInfo(forEachVariable));
                    break;
                case CommonForEachStatementSyntax forEach:
                    methods.Add(model.GetForEachStatementInfo(forEach).GetEnumeratorMethod);
                    break;
                case AwaitExpressionSyntax await:
                    methods.Add(model.GetAwaitExpressionInfo(await).GetAwaiterMethod);
                    break;
                case InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.CollectionInitializerExpression):
                    foreach (var element in initializer.Expressions)
                    {
                        methods.Add(model.GetCollectionInitializerSymbolInfo(element, cancellationToken).Symbol as IMethodSymbol);
                    }

                    break;
                case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: TupleExpressionSyntax or DeclarationExpressionSyntax } assignment:
                    AddDeconstruction(methods, model.GetDeconstructionInfo(assignment));
                    break;
            }
        }

        var result = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in methods)
        {
            // A reduced extension method reports its static method through ReducedFrom; a non-reduced one is that static method already.
            var definition = method?.ReducedFrom ?? method;
            if (definition is { IsExtensionMethod: true, ContainingType: { } type } && seen.Add(type.OriginalDefinition))
            {
                result.Add(type.OriginalDefinition);
            }
        }

        return result;
    }

    private static void AddDeconstruction(List<IMethodSymbol?> methods, DeconstructionInfo info)
    {
        methods.Add(info.Method);
        foreach (var nested in info.Nested)
        {
            AddDeconstruction(methods, nested);
        }
    }
}
