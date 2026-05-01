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
            var classesToUpdate = newRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => !c.Members.OfType<FieldDeclarationSyntax>().Any(f =>
                    f.Declaration.Type.ToString().Contains("Lock") &&
                    f.Declaration.Variables.Any(v => v.Identifier.Text == "_lockObj")))
                .ToList();

            if (classesToUpdate.Count > 0)
            {
                var lockField = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("Lock"))
                    .AddVariables(SyntaxFactory.VariableDeclarator("_lockObj")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("Lock"))
                            .WithArgumentList(SyntaxFactory.ArgumentList())))))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

                newRoot = newRoot.ReplaceNodes(
                    classesToUpdate,
                    (_, current) => current.WithMembers(current.Members.Insert(0, lockField)));

                if (!newRoot.Usings.Any(u => u.Name.ToString() == "System.Threading"))
                {
                    newRoot = newRoot.AddUsings(
                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Threading")));
                }
            }
        }

        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
    }

    public async Task<string> ConvertPropertyToMethodsAsync(string filePath, string propertyName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ConvertPropertyToMethod")) return string.Empty;
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
        var newRoot = rewriter.Visit(root);
        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
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
        var newRoot = rewriter.Visit(root);
        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
    }

    public async Task<string> UseTimeProviderAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("TimeProviderInjection")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var providerField = classNode?.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Type.ToString().Contains("TimeProvider"));
        var fieldName = providerField?.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "_timeProvider";

        var rewriter = new TimeAbstractionRewriter(fieldName);
        var cu = rewriter.Visit(root) as CompilationUnitSyntax;
        if (cu == null) return string.Empty;

        var newClassNode = cu.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (newClassNode != null && providerField == null)
        {
            var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("TimeProvider")).AddVariables(SyntaxFactory.VariableDeclarator(fieldName))).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
            cu = cu.ReplaceNode(newClassNode, newClassNode.WithMembers(newClassNode.Members.Insert(0, field)));
        }
        return cu.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> SimplifyAllNamesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IDE0001")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new NameSimplifierRewriter();
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseIndexFromEndAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("LengthMinusOneToIndex")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new IndexFromEndRewriter();
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
    }

    public async Task<List<AntiPatternFinding>> FindUseFrozenCollectionsAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var results = new List<AntiPatternFinding>();
        var targetTypes = new[] { "Dictionary", "HashSet" };

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                // Must be private static readonly
                var mods = field.Modifiers;
                if (!mods.Any(SyntaxKind.PrivateKeyword)) continue;
                if (!mods.Any(SyntaxKind.StaticKeyword)) continue;
                if (!mods.Any(SyntaxKind.ReadOnlyKeyword)) continue;

                var typeStr = field.Declaration.Type.ToString();
                string? matchedType = null;
                foreach (var t in targetTypes)
                {
                    if (typeStr.StartsWith(t + "<") || typeStr.StartsWith($"System.Collections.Generic.{t}<"))
                    {
                        matchedType = t;
                        break;
                    }
                }
                if (matchedType == null) continue;

                // Must have an initializer
                var hasInit = field.Declaration.Variables.Any(v => v.Initializer != null);
                if (!hasInit) continue;

                var frozenType = matchedType == "Dictionary" ? "FrozenDictionary" : "FrozenSet";
                var lineSpan = field.GetLocation().GetLineSpan();
                var varName = field.Declaration.Variables.First().Identifier.Text;

                results.Add(new AntiPatternFinding(
                    "UseFrozenCollection",
                    $"Field '{varName}' is a private static readonly {matchedType} initialized inline. Consider using {frozenType} (System.Collections.Frozen) for better read performance.",
                    "Low",
                    doc.FilePath,
                    lineSpan.StartLinePosition.Line + 1,
                    field.ToString().Trim()));
            }
        }
        return results;
    }

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
            if (node.Parent is EqualsValueClauseSyntax evc && evc.Parent is VariableDeclaratorSyntax vd && vd.Parent is VariableDeclarationSyntax vds2 && vds2.Type.ToString() == node.Type.ToString())
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
            if (node.Type.ToString().StartsWith("List<") && node.Initializer != null)
            {
                return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(node.Initializer.Expressions.Select(e => SyntaxFactory.ExpressionElement(e)))).WithTriviaFrom(node);
            }
            return base.VisitObjectCreationExpression(node);
        }
    }

    private class TimeAbstractionRewriter : CSharpSyntaxRewriter
    {
        private readonly string _fieldName;
        public TimeAbstractionRewriter(string fieldName) => _fieldName = fieldName;

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var expr = node.Expression.ToString();
            if ((expr is "DateTime" or "DateTimeOffset") && node.Name.Identifier.Text is "UtcNow" or "Now" or "Today")
            {
                var method = node.Name.Identifier.Text == "UtcNow" ? "GetUtcNow()" : "GetLocalNow()";
                return SyntaxFactory.ParseExpression($"{_fieldName}.{method}").WithTriviaFrom(node);
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

    private class IndexFromEndRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            if (node.ArgumentList.Arguments.Count == 1)
            {
                var arg = node.ArgumentList.Arguments[0].Expression;
                if (arg is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.SubtractExpression) && be.Right.ToString() == "1")
                {
                    var left = be.Left.ToString();
                    if (left.EndsWith(".Length") || left.EndsWith(".Count"))
                    {
                        return node.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.IndexExpression, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)))))));
                    }
                }
            }
            return base.VisitElementAccessExpression(node);
        }
    }
}
