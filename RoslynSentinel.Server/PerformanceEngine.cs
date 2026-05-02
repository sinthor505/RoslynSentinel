using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record PerformanceIssueReport(string FilePath, int Line, int Column, string IssueType, string Description);

public class PerformanceEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public PerformanceEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<PerformanceIssueReport>> AnalyzePerformanceAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();

        // 1. Find String Concatenations (especially in loops)
        var stringConcats = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && 
                       (b.Left.IsKind(SyntaxKind.StringLiteralExpression) || b.Right.IsKind(SyntaxKind.StringLiteralExpression)));

        foreach (var concat in stringConcats)
        {
            var inLoop = concat.Ancestors().Any(a => a is ForStatementSyntax || a is ForEachStatementSyntax || a is WhileStatementSyntax);
            if (inLoop)
            {
                var loc = concat.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "StringConcatenationInLoop", "Avoid using '+' for string concatenation inside a loop. Use StringBuilder instead."));
            }
        }

        // 2. Find Poor LINQ Usage (e.g., .Count() > 0 instead of .Any())
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Name.Identifier.Text == "Count" && inv.Parent is BinaryExpressionSyntax binExpr)
                {
                    if ((binExpr.IsKind(SyntaxKind.GreaterThanExpression) && binExpr.Right.ToString() == "0") ||
                        (binExpr.IsKind(SyntaxKind.EqualsExpression) && binExpr.Right.ToString() == "0") ||
                        (binExpr.IsKind(SyntaxKind.NotEqualsExpression) && binExpr.Right.ToString() == "0"))
                    {
                        var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                        issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "PoorLinqCountUsage", "Use .Any() instead of .Count() > 0 for better performance."));
                    }
                }

                if (memberAccess.Name.Identifier.Text == "FirstOrDefault" || memberAccess.Name.Identifier.Text == "Any")
                {
                    if (memberAccess.Expression is InvocationExpressionSyntax innerInv && innerInv.Expression is MemberAccessExpressionSyntax innerMemberAccess)
                    {
                        if (innerMemberAccess.Name.Identifier.Text == "Where")
                        {
                            var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "InefficientLinqWhere", $"Combine .Where() and .{memberAccess.Name.Identifier.Text}() into a single .{memberAccess.Name.Identifier.Text}(condition) call."));
                        }
                    }
                }
            }
        }

        return issues;
    }

    public async Task<List<PerformanceIssueReport>> OptimizeResourceDisposalAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();

        foreach (var creation in objectCreations)
        {
            var typeInfo = semanticModel.GetTypeInfo(creation, cancellationToken).Type;
            if (typeInfo != null && typeInfo.AllInterfaces.Any(i => i.Name == "IDisposable"))
            {
                var isDisposed = creation.Ancestors().Any(a => a is UsingStatementSyntax || a is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
                if (!isDisposed)
                {
                    var loc = creation.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "MissingDisposal", $"Type '{typeInfo.Name}' implements IDisposable but is not wrapped in a 'using' statement."));
                }
            }
        }

        return issues;
    }

    public async Task<List<PerformanceIssueReport>> DetectInefficientStringComparisonsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        var binaryExprs = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression));

        foreach (var bin in binaryExprs)
        {
            var left = bin.Left.ToString();
            var right = bin.Right.ToString();

            if (left.Contains(".ToLower()") || left.Contains(".ToUpper()") || 
                right.Contains(".ToLower()") || right.Contains(".ToUpper()"))
            {
                var loc = bin.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "InefficientStringComparison", "Avoid using .ToLower() or .ToUpper() for comparison. Use string.Equals with StringComparison.OrdinalIgnoreCase instead."));
            }
        }

        return issues;
    }

    public async Task<List<PerformanceIssueReport>> FindBoxingAllocationsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        // Simple heuristic: passing value type to method expecting object or dynamic
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                for (int i = 0; i < Math.Min(inv.ArgumentList.Arguments.Count, methodSymbol.Parameters.Length); i++)
                {
                    var arg = inv.ArgumentList.Arguments[i];
                    var param = methodSymbol.Parameters[i];
                    
                    if (param.Type.SpecialType == SpecialType.System_Object)
                    {
                        var argType = semanticModel.GetTypeInfo(arg.Expression, cancellationToken).Type;
                        if (argType != null && argType.IsValueType)
                        {
                            var loc = arg.GetLocation().GetLineSpan().StartLinePosition;
                            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "BoxingAllocation", $"Boxing detected: converting value type '{argType.Name}' to 'object' in parameter '{param.Name}'."));
                        }
                    }
                }
            }
        }

        return issues;
    }
}
