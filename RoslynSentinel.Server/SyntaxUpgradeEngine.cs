using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class SyntaxUpgradeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public SyntaxUpgradeEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> UpgradeToModernGuardsAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ModernGuardClauses")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new ModernGuardRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddBracesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IDE0011")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new BracesRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("PatternMatching")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new PatternMatchingRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseNameofExpressionAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("UnboundNameof")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (line < 1 || line > text.Lines.Count) return string.Empty;
        var span = text.Lines[line - 1].Span;
        var node = root?.FindNode(span).DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        if (node != null)
        {
            var nameofExpr = SyntaxFactory.ParseExpression($"nameof({node.Token.ValueText})").WithTriviaFrom(node);
            return root!.ReplaceNode(node, nameofExpr).NormalizeWhitespace().ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertSwitchToExpressionAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IfToSwitch")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null) return root?.ToFullString() ?? "";

        var switchStmt = method.DescendantNodes().OfType<SwitchStatementSyntax>().FirstOrDefault();
        if (switchStmt == null) return root?.ToFullString() ?? "";

        var arms = switchStmt.Sections.Select(s => {
            var label = s.Labels.FirstOrDefault();
            var pattern = label is CaseSwitchLabelSyntax c ? SyntaxFactory.ConstantPattern(c.Value) : (PatternSyntax)SyntaxFactory.DiscardPattern();
            var result = s.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression ?? SyntaxFactory.ParseExpression("default");
            return SyntaxFactory.SwitchExpressionArm(pattern, result);
        });

        var switchExpr = SyntaxFactory.SwitchExpression(switchStmt.Expression, SyntaxFactory.SeparatedList(arms));
        var newReturn = SyntaxFactory.ReturnStatement(switchExpr);
        var newRoot = root!.ReplaceNode(switchStmt, newReturn);
        
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertSwitchExpressionToStatementAsync(string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> CleanupImplicitSpansAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ImplicitSpanCleanup")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new ImplicitSpanRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseFieldBackedPropertiesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("FieldBackedProperties")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    private class BracesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var newNode = base.VisitIfStatement(node) as IfStatementSyntax;
            if (newNode == null) return node;
            if (newNode.Statement is not BlockSyntax) newNode = newNode.WithStatement(SyntaxFactory.Block(newNode.Statement));
            if (newNode.Else != null && newNode.Else.Statement is not BlockSyntax && newNode.Else.Statement is not IfStatementSyntax)
                newNode = newNode.WithElse(newNode.Else.WithStatement(SyntaxFactory.Block(newNode.Else.Statement)));
            return newNode;
        }
    }

    private class ModernGuardRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var throwStmt = node.Statement is ThrowStatementSyntax t ? t : 
                            node.Statement is BlockSyntax b && b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax t2 ? t2 : null;

            if (throwStmt != null && throwStmt.Expression is ObjectCreationExpressionSyntax oce)
            {
                var type = oce.Type.ToString();
                var args = oce.ArgumentList?.Arguments;
                var varName = args?.FirstOrDefault()?.Expression.ToString().Replace("nameof(", "").Replace(")", "") ?? "";

                if (type == "ArgumentNullException" && node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.EqualsExpression) && be.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(varName)))))).WithTriviaFrom(node);
                }

                if (type == "ArgumentOutOfRangeException" && node.Condition is BinaryExpressionSyntax be2)
                {
                    string? helper = be2.Kind() switch {
                        SyntaxKind.LessThanExpression when be2.Right.ToString() == "0" => "ThrowIfNegative",
                        SyntaxKind.LessThanOrEqualExpression when be2.Right.ToString() == "0" => "ThrowIfNegativeOrZero",
                        SyntaxKind.EqualsExpression when be2.Right.ToString() == "0" => "ThrowIfZero",
                        SyntaxKind.GreaterThanExpression => "ThrowIfGreaterThan",
                        SyntaxKind.GreaterThanOrEqualExpression => "ThrowIfGreaterThanOrEqual",
                        SyntaxKind.LessThanExpression => "ThrowIfLessThan",
                        SyntaxKind.LessThanOrEqualExpression => "ThrowIfLessThanOrEqual",
                        _ => null
                    };

                    if (helper != null)
                    {
                        var argName = be2.Left.ToString();
                        var argsList = new List<ArgumentSyntax> { SyntaxFactory.Argument(SyntaxFactory.IdentifierName(argName)) };
                        if (helper.Contains("Greater") || helper.Contains("Less"))
                        {
                             argsList.Add(SyntaxFactory.Argument(be2.Right));
                        }

                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentOutOfRangeException"), SyntaxFactory.IdentifierName(helper)),
                            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argsList)))).WithTriviaFrom(node);
                    }
                }

                if (type == "ArgumentException" && node.Condition is InvocationExpressionSyntax ies && ies.Expression.ToString().Contains("IsNullOrEmpty"))
                {
                    var argName = ies.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? varName;
                    return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentException"), SyntaxFactory.IdentifierName("ThrowIfNullOrEmpty")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(argName)))))).WithTriviaFrom(node);
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    private class PatternMatchingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            if (node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.IsExpression))
            {
                var block = node.Statement as BlockSyntax;
                if (block != null && block.Statements.Count > 0)
                {
                    var first = block.Statements[0] as LocalDeclarationStatementSyntax;
                    if (first != null && first.Declaration.Variables.Count == 1)
                    {
                        var variable = first.Declaration.Variables[0];
                        if (variable.Initializer?.Value is CastExpressionSyntax cast && cast.Type.ToString() == be.Right.ToString() && cast.Expression.ToString() == be.Left.ToString())
                        {
                            var newCondition = SyntaxFactory.IsPatternExpression(be.Left, SyntaxFactory.DeclarationPattern((TypeSyntax)be.Right, SyntaxFactory.SingleVariableDesignation(variable.Identifier)));
                            var newBlock = block.WithStatements(block.Statements.RemoveAt(0));
                            return node.WithCondition(newCondition).WithStatement(newBlock);
                        }
                    }
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    private class ImplicitSpanRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "AsSpan")
            {
                return ma.Expression.WithTriviaFrom(node);
            }
            return base.VisitInvocationExpression(node);
        }
    }
}
