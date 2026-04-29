using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class SyntaxUpgradeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SyntaxUpgradeEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> ConvertSwitchToExpressionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) throw new Exception("Method not found.");

        var switchStatements = methodNode.DescendantNodes().OfType<SwitchStatementSyntax>().ToList();
        if (!switchStatements.Any()) return root!.ToFullString();

        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var switchStmt in switchStatements)
        {
            var arms = new List<SwitchExpressionArmSyntax>();
            bool canConvert = true;

            foreach (var section in switchStmt.Sections)
            {
                var statements = section.Statements;
                if (statements.Count > 2) { canConvert = false; break; }

                var targetStatement = statements.FirstOrDefault(s => !(s is BreakStatementSyntax));
                if (targetStatement == null) { canConvert = false; break; }

                ExpressionSyntax? resultExpr = null;
                if (targetStatement is ReturnStatementSyntax ret)
                {
                    resultExpr = ret.Expression;
                }
                else if (targetStatement is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AssignmentExpressionSyntax assign)
                {
                    resultExpr = assign.Right;
                }

                if (resultExpr == null) { canConvert = false; break; }

                foreach (var label in section.Labels)
                {
                    PatternSyntax pattern;
                    if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        pattern = SyntaxFactory.ConstantPattern(caseLabel.Value);
                    }
                    else if (label is DefaultSwitchLabelSyntax)
                    {
                        pattern = SyntaxFactory.DiscardPattern();
                    }
                    else
                    {
                        canConvert = false; break;
                    }

                    arms.Add(SyntaxFactory.SwitchExpressionArm(pattern, resultExpr));
                }
            }

            if (canConvert && arms.Any())
            {
                var switchExpr = SyntaxFactory.SwitchExpression(switchStmt.Expression, SyntaxFactory.SeparatedList(arms));
                var sampleTarget = switchStmt.Sections.First().Statements.First(s => !(s is BreakStatementSyntax));
                StatementSyntax newStmt;

                if (sampleTarget is ReturnStatementSyntax)
                {
                    newStmt = SyntaxFactory.ReturnStatement(switchExpr);
                }
                else if (sampleTarget is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AssignmentExpressionSyntax assign)
                {
                    newStmt = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, assign.Left, switchExpr));
                }
                else
                {
                    continue;
                }

                replaceMap[switchStmt] = newStmt.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            }
        }

        var newRoot = root!.ReplaceNodes(replaceMap.Keys, (oldNode, _) => replaceMap[oldNode]);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseThrowExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        return root.ToFullString();
    }

    public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        return root.ToFullString();
    }

    public async Task<string> ConvertSwitchExpressionToStatementAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        return root.ToFullString();
    }

    public async Task<string> ConvertSwitchToIfAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        return root.ToFullString();
    }

    /// <summary>
    /// Replaces a string literal with a nameof() expression.
    /// </summary>
    public async Task<string> UseNameofExpressionAsync(string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines[line - 1].Start + (column - 1);
        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));
        
        var stringLiteral = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        if (stringLiteral != null)
        {
            var nameofExpr = SyntaxFactory.ParseExpression($"nameof({stringLiteral.Token.ValueText})")
                .WithTriviaFrom(stringLiteral);
            
            var newRoot = root.ReplaceNode(stringLiteral, nameofExpr);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root.ToFullString();
    }

    /// <summary>
    /// Upgrades legacy if-throw guard clauses to modern static Throw helpers.
    /// </summary>
    public async Task<string> UpgradeToModernGuardsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new GuardClauseRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class GuardClauseRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var throwStmt = node.Statement is ThrowStatementSyntax t ? t : 
                            node.Statement is BlockSyntax b && b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax t2 ? t2 : null;

            if (throwStmt == null || throwStmt.Expression is not ObjectCreationExpressionSyntax oce)
                return base.VisitIfStatement(node);

            var exceptionType = oce.Type.ToString();

            // 1. ArgumentNullException.ThrowIfNull
            if (exceptionType == "ArgumentNullException" && node.Condition is BinaryExpressionSyntax beNull && beNull.IsKind(SyntaxKind.EqualsExpression) && beNull.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return CreateThrowHelper(node, "ArgumentNullException", "ThrowIfNull", beNull.Left.ToString());
            }

            // 2. ArgumentException (String helpers)
            if (exceptionType == "ArgumentException" && node.Condition is InvocationExpressionSyntax ies)
            {
                var method = ies.Expression.ToString();
                var varName = ies.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
                if (varName != null)
                {
                    if (method is "string.IsNullOrEmpty" or "String.IsNullOrEmpty") return CreateThrowHelper(node, "ArgumentException", "ThrowIfNullOrEmpty", varName);
                    if (method is "string.IsNullOrWhiteSpace" or "String.IsNullOrWhiteSpace") return CreateThrowHelper(node, "ArgumentException", "ThrowIfNullOrWhiteSpace", varName);
                }
            }

            // 3. ArgumentOutOfRangeException Helpers
            if (exceptionType == "ArgumentOutOfRangeException" && node.Condition is BinaryExpressionSyntax beRange)
            {
                var varName = beRange.Left.ToString();
                var right = beRange.Right.ToString();
                var op = beRange.OperatorToken.ValueText;

                if (op == "<" && right == "0") return CreateThrowHelper(node, "ArgumentOutOfRangeException", "ThrowIfNegative", varName);
                if (op == "<=" && right == "0") return CreateThrowHelper(node, "ArgumentOutOfRangeException", "ThrowIfNegativeOrZero", varName);
                if (op == "==" && right == "0") return CreateThrowHelper(node, "ArgumentOutOfRangeException", "ThrowIfZero", varName);
                
                if (op == ">") return CreateThrowHelperWithArgs(node, "ArgumentOutOfRangeException", "ThrowIfGreaterThan", varName, beRange.Right);
                if (op == "<") return CreateThrowHelperWithArgs(node, "ArgumentOutOfRangeException", "ThrowIfLessThan", varName, beRange.Right);
                if (op == ">=") return CreateThrowHelperWithArgs(node, "ArgumentOutOfRangeException", "ThrowIfGreaterThanOrEqual", varName, beRange.Right);
                if (op == "<=") return CreateThrowHelperWithArgs(node, "ArgumentOutOfRangeException", "ThrowIfLessThanOrEqual", varName, beRange.Right);
            }

            // 4. ObjectDisposedException.ThrowIf
            if (exceptionType == "ObjectDisposedException")
            {
                var varName = node.Condition.ToString();
                // Assumes 'this' is the instance being checked.
                return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ObjectDisposedException"), SyntaxFactory.IdentifierName("ThrowIf")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { 
                            SyntaxFactory.Argument(SyntaxFactory.ParseExpression(varName)), 
                            SyntaxFactory.Argument(SyntaxFactory.ThisExpression()) 
                        }))
                    )).WithTriviaFrom(node);
            }

            return base.VisitIfStatement(node);
        }

        private SyntaxNode CreateThrowHelper(IfStatementSyntax originalIf, string type, string method, string varName)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(type), SyntaxFactory.IdentifierName(method)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(varName))))
                )).WithTriviaFrom(originalIf);
        }

        private SyntaxNode CreateThrowHelperWithArgs(IfStatementSyntax originalIf, string type, string method, string varName, ExpressionSyntax other)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(type), SyntaxFactory.IdentifierName(method)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { 
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(varName)), 
                        SyntaxFactory.Argument(other) 
                    }))
                )).WithTriviaFrom(originalIf);
        }
    }
}
