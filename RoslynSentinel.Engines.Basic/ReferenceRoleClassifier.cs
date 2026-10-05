using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Classifies syntax nodes by their role in reference contexts.</summary>
public static class ReferenceRoleClassifier
{
    /// <summary>Classifies a reference node by its syntactic role.</summary>
    /// <remarks>referenceNode is an IdentifierNameSyntax of the symbol reference.</remarks>
    public static SemanticReplaceRole Classify(SyntaxNode referenceNode)
    {
        return Classify(referenceNode, out _);
    }

    /// <summary>Classifies a reference node by its syntactic role, with optional reason for Unsupported.</summary>
    /// <remarks>referenceNode is an IdentifierNameSyntax of the symbol reference. unsupportedReason is set only when role is Unsupported.</remarks>
    public static SemanticReplaceRole Classify(SyntaxNode referenceNode, out string? unsupportedReason)
    {
        unsupportedReason = null;

        if (referenceNode is not IdentifierNameSyntax identifier)
        {
            unsupportedReason = "Node is not an identifier";
            return SemanticReplaceRole.Unsupported;
        }

        var parent = identifier.Parent;

        // Case 1: x.IsSuccess = ... (identifier is the Name of a MemberAccessExpressionSyntax)
        if (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier)
        {
            var grandparent = memberAccess.Parent;
            
            // Check for compound assignment on the member access
            if (grandparent is AssignmentExpressionSyntax compoundAssignment && compoundAssignment.Left == memberAccess)
            {
                if (!compoundAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    unsupportedReason = "Compound assignment operators are not supported";
                    return SemanticReplaceRole.Unsupported;
                }
                // Simple assignment
                return ClassifyWriteSite(compoundAssignment.Right);
            }
            
            // Check for increment/decrement on the member access
            if (grandparent is PostfixUnaryExpressionSyntax postfixUnary &&
                (postfixUnary.IsKind(SyntaxKind.PostIncrementExpression) || postfixUnary.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                unsupportedReason = "Increment/decrement operators are not supported";
                return SemanticReplaceRole.Unsupported;
            }

            if (grandparent is PrefixUnaryExpressionSyntax prefixUnaryOp &&
                (prefixUnaryOp.IsKind(SyntaxKind.PreIncrementExpression) || prefixUnaryOp.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                unsupportedReason = "Increment/decrement operators are not supported";
                return SemanticReplaceRole.Unsupported;
            }

            // Gap 1: Check for ref/out on member access (e.g., Foo(ref x.IsSuccess), Foo(out x.IsSuccess))
            if (grandparent is ArgumentSyntax memberArgument && 
                (memberArgument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || memberArgument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
            {
                unsupportedReason = "Ref/out arguments are not supported";
                return SemanticReplaceRole.Unsupported;
            }

            // Gap 3: Check for is patterns (e.g., x.IsSuccess is true, x.IsSuccess is false, x.IsSuccess is not true)
            if (grandparent is IsPatternExpressionSyntax isPattern && isPattern.Expression == memberAccess)
            {
                unsupportedReason = "'is' patterns are not supported";
                return SemanticReplaceRole.Unsupported;
            }
        }

        // Case 2: IsSuccess = ... (bare identifier on left of assignment)
        if (parent is AssignmentExpressionSyntax directAssignment && directAssignment.Left == identifier)
        {
            if (!directAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                unsupportedReason = "Compound assignment operators are not supported";
                return SemanticReplaceRole.Unsupported;
            }
            return ClassifyWriteSite(directAssignment.Right);
        }

        // Check for ref/out arguments on bare identifier
        if (parent is ArgumentSyntax argument && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            unsupportedReason = "Ref/out arguments are not supported";
            return SemanticReplaceRole.Unsupported;
        }

        // Deconstruction assignment target: (x.IsSuccess, y) = t is a write, not a read
        var reference = parent is MemberAccessExpressionSyntax targetAccess && targetAccess.Name == identifier
            ? (ExpressionSyntax)targetAccess
            : identifier;
        if (reference.Parent is ArgumentSyntax tupleArgument &&
            tupleArgument.Parent is TupleExpressionSyntax tuple &&
            tuple.Parent is AssignmentExpressionSyntax deconstruction &&
            deconstruction.Left == tuple)
        {
            unsupportedReason = "Deconstruction assignment targets are not supported";
            return SemanticReplaceRole.Unsupported;
        }

        // Check for is patterns on a bare identifier (IsSuccess is true)
        if (parent is IsPatternExpressionSyntax barePattern && barePattern.Expression == identifier)
        {
            unsupportedReason = "'is' patterns are not supported";
            return SemanticReplaceRole.Unsupported;
        }

        // Check for NameOf - identifier inside nameof(...)
        if (IsInsideNameOf(identifier))
        {
            return SemanticReplaceRole.NameOf;
        }

        // Check for NegatedRead - parent is ! unary prefix
        if (parent is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return SemanticReplaceRole.NegatedRead;
        }

        // Check for NegatedRead with member access - x.IsSuccess negated
        if (parent is MemberAccessExpressionSyntax memberAccessInNegation && memberAccessInNegation.Name == identifier)
        {
            var memberParent = memberAccessInNegation.Parent;
            if (memberParent is PrefixUnaryExpressionSyntax memberUnary && memberUnary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                return SemanticReplaceRole.NegatedRead;
            }
        }

        // Check for == true / == false / != true / != false comparisons
        if (parent is BinaryExpressionSyntax binaryExpr && 
            (binaryExpr.IsKind(SyntaxKind.EqualsExpression) || binaryExpr.IsKind(SyntaxKind.NotEqualsExpression)))
        {
            // Check if the other side is a boolean literal
            var otherOperand = binaryExpr.Left == identifier ? binaryExpr.Right : binaryExpr.Left;
            if (otherOperand.IsKind(SyntaxKind.TrueLiteralExpression) || otherOperand.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                unsupportedReason = "Equality comparisons with boolean literals are not supported";
                return SemanticReplaceRole.Unsupported;
            }
        }

        // Check for == true / == false with member access
        if (parent is MemberAccessExpressionSyntax memberAccessInComparison && memberAccessInComparison.Name == identifier)
        {
            var memberComparisonParent = memberAccessInComparison.Parent;
            if (memberComparisonParent is BinaryExpressionSyntax memberBinaryExpr &&
                (memberBinaryExpr.IsKind(SyntaxKind.EqualsExpression) || memberBinaryExpr.IsKind(SyntaxKind.NotEqualsExpression)))
            {
                var otherComparisionOperand = memberBinaryExpr.Left == memberAccessInComparison ? memberBinaryExpr.Right : memberBinaryExpr.Left;
                if (otherComparisionOperand.IsKind(SyntaxKind.TrueLiteralExpression) || otherComparisionOperand.IsKind(SyntaxKind.FalseLiteralExpression))
                {
                    unsupportedReason = "Equality comparisons with boolean literals are not supported";
                    return SemanticReplaceRole.Unsupported;
                }
            }
        }

        // Gap 2: Check for property patterns (e.g., x is { IsSuccess: true })
        if (parent is NameColonSyntax nameColon && nameColon.Parent is SubpatternSyntax)
        {
            unsupportedReason = "Property patterns are not supported";
            return SemanticReplaceRole.Unsupported;
        }

        // Default: plain Read usage
        return SemanticReplaceRole.Read;
    }

    private static SemanticReplaceRole ClassifyWriteSite(ExpressionSyntax rightSide)
    {
        // Check if RHS is a true/false literal
        var kind = rightSide.Kind();
        if (kind == SyntaxKind.TrueLiteralExpression || kind == SyntaxKind.FalseLiteralExpression)
        {
            return SemanticReplaceRole.WriteLiteral;
        }

        return SemanticReplaceRole.WriteExpression;
    }

    private static bool IsInsideNameOf(IdentifierNameSyntax identifier)
    {
        var current = identifier.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is IdentifierNameSyntax methodName && methodName.Identifier.Text == "nameof")
                {
                    return true;
                }
            }
            current = current.Parent;
        }
        return false;
    }
}
