using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class CodeStyleEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public CodeStyleEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> FixDangerousLockAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("LockModernization")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        if (root == null) return string.Empty;

        var rewriter = new DangerousLockRewriter();
        var newRoot = rewriter.Visit(root) as CompilationUnitSyntax;

        if (newRoot != null && rewriter.MadeChanges)
        {
            var classes = newRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            foreach (var @class in classes)
            {
                if (!@class.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Type.ToString().Contains("Lock") && f.Declaration.Variables.Any(v => v.Identifier.Text == "_lockObj")))
                {
                    var lockField = SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("Lock"))
                        .AddVariables(SyntaxFactory.VariableDeclarator("_lockObj")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("Lock")).WithArgumentList(SyntaxFactory.ArgumentList())))))
                        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                    
                    var newClass = @class.WithMembers(@class.Members.Insert(0, lockField));
                    newRoot = newRoot.ReplaceNode(@class, newClass);

                    if (!newRoot.Usings.Any(u => u.Name.ToString() == "System.Threading"))
                    {
                        newRoot = newRoot.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Threading")));
                    }
                }
            }
        }

        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
    }

    public async Task<string> EnsureSemaphoreFinallyAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("SemaphoreLeaks")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertPropertyToMethodsAsync(string filePath, string propertyName, CancellationToken ct = default)
    {
        // No explicit toggle yet for this surgical one, but we could add it.
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        var prop = root?.DescendantNodes().OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == propertyName);
        if (prop == null) return root?.ToFullString() ?? "";

        var type = prop.Type;
        var fieldName = $"_{propertyName.ToLower()}";
        
        var getter = SyntaxFactory.MethodDeclaration(type, $"Get{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(fieldName))));

        var setter = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), $"Set{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(type))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(fieldName), SyntaxFactory.IdentifierName("value")))));

        var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(type).AddVariables(SyntaxFactory.VariableDeclarator(fieldName)))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        var classNode = prop.Ancestors().OfType<ClassDeclarationSyntax>().First();
        var newClass = classNode.RemoveNode(prop, SyntaxRemoveOptions.KeepNoTrivia)!
            .AddMembers(field, getter, setter);

        return root!.ReplaceNode(classNode, newClass).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> SimplifyVerbosityAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("SimplifyVerbosity")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new VerbosityRewriter(_config);
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseCollectionExpressionsAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("CollectionExpressions")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new CollectionExpressionRewriter();
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseTimeProviderAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("TimeProviderInjection")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        
        var rewriter = new TimeAbstractionRewriter();
        var cu = rewriter.Visit(root) as CompilationUnitSyntax;
        if (cu == null) return string.Empty;

        var classNode = cu.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classNode != null)
        {
            var hasProvider = classNode.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Type.ToString().Contains("TimeProvider"));
            if (!hasProvider)
            {
                var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("TimeProvider")).AddVariables(SyntaxFactory.VariableDeclarator("_timeProvider"))).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                cu = cu.ReplaceNode(classNode, classNode.WithMembers(classNode.Members.Insert(0, field)));
            }
        }
        return cu.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> SimplifyAllNamesAsync(string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new NameSimplifierRewriter();
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseThrowExpressionsAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ThrowExpressions")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> UpgradeThreadSafetyAsync(string filePath, CancellationToken ct = default) => "";

    private class DangerousLockRewriter : CSharpSyntaxRewriter
    {
        public bool MadeChanges { get; private set; }
        public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
        {
            var expr = node.Expression.ToString();
            if (expr is "this" or "typeof") { MadeChanges = true; return node.WithExpression(SyntaxFactory.IdentifierName("_lockObj")).WithTriviaFrom(node); }
            return base.VisitLockStatement(node);
        }
    }

    private class VerbosityRewriter : CSharpSyntaxRewriter
    {
        private readonly SentinelConfiguration _config;
        public VerbosityRewriter(SentinelConfiguration config) => _config = config;

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (node.Parent is VariableDeclarationSyntax vds && vds.Type.ToString() == node.Type.ToString())
                return SyntaxFactory.ImplicitObjectCreationExpression(node.ArgumentList ?? SyntaxFactory.ArgumentList(), node.Initializer).WithTriviaFrom(node);
            
            if (node.Parent is EqualsValueClauseSyntax evc && evc.Parent is VariableDeclaratorSyntax vdr && vdr.Parent is VariableDeclarationSyntax vdsField && vdsField.Type.ToString() == node.Type.ToString())
                return SyntaxFactory.ImplicitObjectCreationExpression(node.ArgumentList ?? SyntaxFactory.ArgumentList(), node.Initializer).WithTriviaFrom(node);

            return base.VisitObjectCreationExpression(node);
        }
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            if (!_config.IsFeatureEnabled("NullConditionalAssignment")) return base.VisitIfStatement(node);

            if (node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.EqualsExpression) && be.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                var assignment = node.Statement is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax asgn ? asgn : null;
                if (assignment != null && assignment.Left.ToString() == be.Left.ToString())
                {
                    var right = assignment.Right;
                    if (right is ObjectCreationExpressionSyntax oce) right = SyntaxFactory.ImplicitObjectCreationExpression(oce.ArgumentList ?? SyntaxFactory.ArgumentList(), oce.Initializer).WithTriviaFrom(oce);
                    return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, assignment.Left, right)).WithTriviaFrom(node);
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    private class CollectionExpressionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
        {
            if (node.Initializer == null || node.Initializer.Expressions.Count == 0) return SyntaxFactory.CollectionExpression().WithTriviaFrom(node);
            return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(node.Initializer.Expressions.Select(e => SyntaxFactory.ExpressionElement(e)))).WithTriviaFrom(node);
        }
        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var typeStr = node.Type.ToString();
            if ((typeStr.StartsWith("List<") || typeStr.StartsWith("HashSet<")) && (node.Initializer == null || node.Initializer.Expressions.Count >= 0))
            {
                if (node.Initializer == null) return SyntaxFactory.CollectionExpression().WithTriviaFrom(node);
                return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(node.Initializer.Expressions.Select(e => SyntaxFactory.ExpressionElement(e)))).WithTriviaFrom(node);
            }
            return base.VisitObjectCreationExpression(node);
        }
    }

    private class TimeAbstractionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var expr = node.Expression.ToString();
            if ((expr is "DateTime" or "DateTimeOffset") && node.Name.Identifier.Text is "UtcNow" or "Now" or "Today")
            {
                var classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var providerName = "_timeProvider";
                if (classNode != null)
                {
                    var field = classNode.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Type.ToString().Contains("TimeProvider"));
                    if (field != null) providerName = field.Declaration.Variables.First().Identifier.Text;
                }
                var method = node.Name.Identifier.Text == "UtcNow" ? "GetUtcNow()" : "GetLocalNow()";
                return SyntaxFactory.ParseExpression($"{providerName}.{method}").WithTriviaFrom(node);
            }
            return base.VisitMemberAccessExpression(node);
        }
    }

    private class NameSimplifierRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
        {
            if (node.Alias.Identifier.Text == "global") return node.Name;
            return base.VisitAliasQualifiedName(node);
        }
    }
}
