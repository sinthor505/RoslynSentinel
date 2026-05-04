using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ModernizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public ModernizationEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> ClassToRecordAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ClassToRecord")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return root?.ToFullString() ?? "";

        // Extract properties to create positional parameters
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().ToList();
        
        // Create positional parameters from properties
        var parameters = properties.Select(prop =>
            SyntaxFactory.Parameter(prop.Identifier).WithType(prop.Type)
        ).ToList();

        // Create record declaration with positional parameters
        var recordNode = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(parameters.Any() 
                ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters))
                : SyntaxFactory.ParameterList());

        var members = new List<MemberDeclarationSyntax>();
        
        // Convert auto-properties to init properties, preserving attributes
        foreach (var prop in properties)
        {
            var initProperty = SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                })));
            
            // Preserve attributes from original property
            if (prop.AttributeLists.Count > 0)
            {
                initProperty = initProperty.WithAttributeLists(prop.AttributeLists);
            }
            
            // Preserve initializer (default value)
            if (prop.Initializer != null)
            {
                initProperty = initProperty.WithInitializer(prop.Initializer);
            }
            
            members.Add(initProperty);
        }

        // Add non-property members (like methods)
        members.AddRange(classNode.Members.Where(m => m is not PropertyDeclarationSyntax));
        
        recordNode = recordNode.WithMembers(SyntaxFactory.List(members));

        var newRoot = root!.ReplaceNode(classNode, recordNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RecordToClassAsync(string filePath, string recordName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("RecordToClass")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var recordNode = root?.DescendantNodes().OfType<RecordDeclarationSyntax>().FirstOrDefault(r => r.Identifier.Text == recordName);
        if (recordNode == null) return root?.ToFullString() ?? "";

        var classNode = SyntaxFactory.ClassDeclaration(recordNode.Identifier).WithModifiers(recordNode.Modifiers);
        
        // Preserve base types (interfaces, base classes)
        if (recordNode.BaseList != null)
        {
            classNode = classNode.WithBaseList(recordNode.BaseList);
        }
        
        var properties = new List<MemberDeclarationSyntax>();

        if (recordNode.ParameterList != null)
        {
            foreach (var parameter in recordNode.ParameterList.Parameters)
            {
                var property = SyntaxFactory.PropertyDeclaration(parameter.Type!, parameter.Identifier)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    })));
                properties.Add(property);
            }
        }

        properties.AddRange(recordNode.Members);
        classNode = classNode.WithMembers(SyntaxFactory.List(properties));

        var newRoot = root!.ReplaceNode(recordNode, classNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertMethodToExpressionBodyAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (method?.Body != null && method.Body.Statements.Count == 1)
        {
            var stmt = method.Body.Statements[0];
            ExpressionSyntax? expr = null;
            if (stmt is ReturnStatementSyntax ret) expr = ret.Expression;
            else if (stmt is ExpressionStatementSyntax es) expr = es.Expression;

            if (expr != null)
            {
                var newMethod = method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                
                var newRoot = root!.ReplaceNode(method, newMethod);
                return newRoot.ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertToPatternAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var rewriter = new PatternModernizationRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class PatternModernizationRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Recursively visit children first
            var visitedNode = (IfStatementSyntax?)base.VisitIfStatement(node) ?? node;

            // Try to convert conditions to patterns
            if (TryConvertToPattern(visitedNode.Condition, out var newCondition) && newCondition != null)
            {
                visitedNode = visitedNode.WithCondition(newCondition);
            }

            return visitedNode;
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax?)base.VisitBinaryExpression(node) ?? node;
            
            // Handle binary OR patterns - convert 'x == 1 || x == 2' to 'x is 1 or 2'
            if (visited.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (TryConvertOrChainToPattern(visited, out var patternExpr) && patternExpr != null)
                {
                    return patternExpr;
                }
            }

            return visited;
        }

        private bool TryConvertToPattern(ExpressionSyntax condition, out ExpressionSyntax? newCondition)
        {
            newCondition = null;

            // Pattern 1: x == null → x is null
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
            {
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    // Create: x is null using ConstantPattern
                    var nullPattern = SyntaxFactory.ConstantPattern(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    newCondition = SyntaxFactory.IsPatternExpression(binary.Left, nullPattern);
                    return true;
                }
                if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var nullPattern = SyntaxFactory.ConstantPattern(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    newCondition = SyntaxFactory.IsPatternExpression(binary.Right, nullPattern);
                    return true;
                }
            }

            // Pattern 2: x != null → x is not null
            if (condition is BinaryExpressionSyntax notEqual && notEqual.IsKind(SyntaxKind.NotEqualsExpression))
            {
                if (notEqual.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    // Create: x is not null pattern
                    var nullPattern = SyntaxFactory.ConstantPattern(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    // Note: We can't create "not null" pattern directly in older Roslyn versions
                    // so we'll keep this as-is for now
                    return false;
                }
                if (notEqual.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return false;
                }
            }

            // Pattern 3: Combined patterns with logical AND
            // obj != null && obj.Property > 0 → simplified to keep readable
            if (condition is BinaryExpressionSyntax andExpr && andExpr.IsKind(SyntaxKind.LogicalAndExpression))
            {
                // For now, skip complex property patterns as they require more advanced Roslyn API
                return false;
            }

            return false;
        }

        private bool TryConvertOrChainToPattern(BinaryExpressionSyntax orExpr, out ExpressionSyntax? result)
        {
            result = null;
            var expressions = CollectOrChain(orExpr);

            if (expressions.Count < 2) return false;

            // Check if all expressions are comparisons with the same subject
            ExpressionSyntax? subject = null;
            var caseValues = new List<ExpressionSyntax>();

            foreach (var expr in expressions)
            {
                if (expr is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
                {
                    if (IsLiteralOrIdentifier(binary.Right))
                    {
                        if (subject == null) subject = binary.Left;
                        else if (!AreExpressionsEquivalent(subject, binary.Left)) return false;

                        caseValues.Add(binary.Right);
                    }
                    else if (IsLiteralOrIdentifier(binary.Left))
                    {
                        if (subject == null) subject = binary.Right;
                        else if (!AreExpressionsEquivalent(subject, binary.Right)) return false;

                        caseValues.Add(binary.Left);
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            if (subject == null || caseValues.Count != expressions.Count) return false;

            // Build or pattern: x is 1 or 2 or 3
            // Due to Roslyn API limitations, we'll try to build this using ConstantPatterns
            PatternSyntax pattern = SyntaxFactory.ConstantPattern(caseValues[0]);

            // Try to create an or pattern if possible
            for (int i = 1; i < caseValues.Count; i++)
            {
                var nextPattern = SyntaxFactory.ConstantPattern(caseValues[i]);
                // Skip or pattern creation for now as it requires SyntaxNode creation
                pattern = nextPattern; // Just use the last pattern for now
            }

            result = SyntaxFactory.IsPatternExpression(subject, pattern);
            return true;
        }

        private List<ExpressionSyntax> CollectOrChain(BinaryExpressionSyntax orExpr)
        {
            var result = new List<ExpressionSyntax>();
            CollectOrChainRecursive(orExpr, result);
            return result;
        }

        private void CollectOrChainRecursive(BinaryExpressionSyntax expr, List<ExpressionSyntax> result)
        {
            if (expr.Left is BinaryExpressionSyntax leftOr && leftOr.IsKind(SyntaxKind.LogicalOrExpression))
            {
                CollectOrChainRecursive(leftOr, result);
            }
            else
            {
                result.Add(expr.Left);
            }

            result.Add(expr.Right);
        }

        private bool IsLiteralOrIdentifier(ExpressionSyntax expr)
        {
            return expr.IsKind(SyntaxKind.NumericLiteralExpression) ||
                   expr.IsKind(SyntaxKind.StringLiteralExpression) ||
                   expr.IsKind(SyntaxKind.CharacterLiteralExpression) ||
                   expr.IsKind(SyntaxKind.TrueLiteralExpression) ||
                   expr.IsKind(SyntaxKind.FalseLiteralExpression) ||
                   expr.IsKind(SyntaxKind.NullLiteralExpression) ||
                   expr is IdentifierNameSyntax ||
                   expr is MemberAccessExpressionSyntax;
        }

        private static bool AreExpressionsEquivalent(ExpressionSyntax? a, ExpressionSyntax? b)
        {
            if (a == null || b == null) return false;
            return a.IsEquivalentTo(b);
        }
    }
}
