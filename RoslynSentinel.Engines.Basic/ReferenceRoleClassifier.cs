using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Classifies syntax nodes by their role in reference contexts.</summary>
public static class ReferenceRoleClassifier
{
    /// <summary>Classifies a reference node by its syntactic role.</summary>
    /// <remarks>referenceNode is an IdentifierNameSyntax of the symbol reference.</remarks>
    public static SemanticReplaceRole Classify(SyntaxNode referenceNode)
    {
        if (referenceNode is not IdentifierNameSyntax identifier)
        {
            return SemanticReplaceRole.Unsupported;
        }

        var parent = identifier.Parent;

        // Case 1: x.IsSuccess = ... (identifier is the Name of a MemberAccessExpressionSyntax)
        if (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier)
        {
            var grandparent = memberAccess.Parent;
            if (grandparent is AssignmentExpressionSyntax assignment && assignment.Left == memberAccess &&
                assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
            {
                return ClassifyWriteSite(assignment.Right);
            }
        }

        // Case 2: IsSuccess = ... (bare identifier on left of assignment)
        if (parent is AssignmentExpressionSyntax directAssignment && directAssignment.Left == identifier &&
            directAssignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
        {
            return ClassifyWriteSite(directAssignment.Right);
        }

        // Every other shape is unsupported for now (Step 4 adds Read, NegatedRead, NameOf)
        return SemanticReplaceRole.Unsupported;
    }

    private static SemanticReplaceRole ClassifyWriteSite(ExpressionSyntax rightSide)
    {
        // Check if RHS is a true/false literal
        var kind = rightSide.Kind();
        if (kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression ||
            kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.FalseLiteralExpression)
        {
            return SemanticReplaceRole.WriteLiteral;
        }

        return SemanticReplaceRole.WriteExpression;
    }
}
