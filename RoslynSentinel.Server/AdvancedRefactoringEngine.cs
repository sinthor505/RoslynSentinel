using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class AdvancedRefactoringEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedRefactoringEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> ReplaceStringConcatWithInterpolationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        // Find top-level string concat chains — not a child of another string-concat-with-literal
        var topLevelConcats = root.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(b))
            .Where(b => !b.Ancestors().OfType<BinaryExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(a)))
            .ToList();

        if (!topLevelConcats.Any()) return root.ToFullString();

        var newRoot = root.ReplaceNodes(topLevelConcats, (original, _) =>
        {
            var segments = FlattenConcatTree(original);
            var contents = new List<InterpolatedStringContentSyntax>();
            foreach (var seg in segments)
            {
                if (seg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var tokenText = lit.Token.Text;
                    // Verbatim/raw strings have different escape semantics — wrap as interpolation hole
                    if (tokenText.StartsWith("@") || tokenText.StartsWith("\"\"\""))
                    {
                        contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                        continue;
                    }
                    // Strip surrounding quotes, double {{ and }} for the interpolated context
                    var innerText = tokenText.Length >= 2 ? tokenText.Substring(1, tokenText.Length - 2) : string.Empty;
                    var escapedText = innerText.Replace("{", "{{").Replace("}", "}}");
                    if (!string.IsNullOrEmpty(escapedText))
                    {
                        contents.Add(SyntaxFactory.InterpolatedStringText(
                            SyntaxFactory.Token(
                                SyntaxTriviaList.Empty,
                                SyntaxKind.InterpolatedStringTextToken,
                                escapedText,
                                lit.Token.ValueText,
                                SyntaxTriviaList.Empty)));
                    }
                }
                else
                {
                    contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                }
            }
            return SyntaxFactory.InterpolatedStringExpression(
                    SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
                    SyntaxFactory.List(contents),
                    SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken))
                .WithTriviaFrom(original);
        });

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> OptimizeTaskWaitAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return string.Empty;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var methodsToMakeAsync = new List<MethodDeclarationSyntax>();
        var methodSpans = new HashSet<int>();

        bool IsTaskType(ExpressionSyntax expr)
        {
            if (semanticModel == null) return true;
            var typeInfo = semanticModel.GetTypeInfo(expr, cancellationToken);
            var name = typeInfo.Type?.Name;
            return name is "Task" or "ValueTask";
        }

        void CollectMethod(SyntaxNode node)
        {
            var m = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (m != null && methodSpans.Add(m.SpanStart))
                methodsToMakeAsync.Add(m);
        }

        // Pattern 1: .GetAwaiter().GetResult()
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax maGet) continue;
            if (maGet.Name.Identifier.Text != "GetResult") continue;
            if (maGet.Expression is not InvocationExpressionSyntax getAwaiterCall) continue;
            if (getAwaiterCall.Expression is not MemberAccessExpressionSyntax maAwaiter) continue;
            if (maAwaiter.Name.Identifier.Text != "GetAwaiter") continue;
            if (!IsTaskType(maAwaiter.Expression)) continue;

            replacements[inv] = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    maAwaiter.Expression.WithoutTrivia())).WithTriviaFrom(inv);
            CollectMethod(inv);
        }

        // Pattern 2: .Wait() with no arguments
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (replacements.ContainsKey(inv)) continue;
            if (inv.Expression is not MemberAccessExpressionSyntax maWait) continue;
            if (maWait.Name.Identifier.Text != "Wait") continue;
            if (inv.ArgumentList.Arguments.Count != 0) continue;
            if (!IsTaskType(maWait.Expression)) continue;

            replacements[inv] = SyntaxFactory.AwaitExpression(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                maWait.Expression.WithoutTrivia()).WithTriviaFrom(inv);
            CollectMethod(inv);
        }

        // Pattern 3: .Result member access
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (ma.Name.Identifier.Text != "Result") continue;
            if (replacements.Keys.Any(k => k.Span.Contains(ma.Span))) continue;
            if (!IsTaskType(ma.Expression)) continue;

            replacements[ma] = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    ma.Expression.WithoutTrivia())).WithTriviaFrom(ma);
            CollectMethod(ma);
        }

        if (!replacements.Any()) return root.NormalizeWhitespace().ToFullString();

        // Track all nodes that will be mutated before any tree modification
        var trackedRoot = root.TrackNodes(replacements.Keys.Concat(methodsToMakeAsync.Cast<SyntaxNode>()));

        var trackedReplacements = replacements
            .Select(kvp => (Tracked: trackedRoot.GetCurrentNode(kvp.Key), Value: kvp.Value))
            .Where(t => t.Tracked != null)
            .ToDictionary(t => t.Tracked!, t => t.Value);

        var newRoot = trackedRoot.ReplaceNodes(trackedReplacements.Keys, (original, _) => trackedReplacements[original]);

        // Make affected methods async
        var methodUpdates = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var method in methodsToMakeAsync)
        {
            var current = newRoot.GetCurrentNode(method);
            if (current == null || current.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) continue;

            var asyncToken = SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newModifiers = current.Modifiers.Add(asyncToken);
            TypeSyntax newReturn = current.ReturnType;
            if (current.ReturnType is PredefinedTypeSyntax pred && pred.Keyword.IsKind(SyntaxKind.VoidKeyword))
                newReturn = SyntaxFactory.IdentifierName("Task").WithTriviaFrom(current.ReturnType);
            else if (!IsTaskReturnType(current.ReturnType))
                newReturn = SyntaxFactory.GenericName(SyntaxFactory.Identifier("Task"))
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(current.ReturnType.WithoutTrivia())))
                    .WithTriviaFrom(current.ReturnType);

            methodUpdates[current] = current.WithModifiers(newModifiers).WithReturnType(newReturn);
        }

        if (methodUpdates.Any())
            newRoot = newRoot.ReplaceNodes(methodUpdates.Keys, (original, _) => methodUpdates[original]);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static bool ContainsStringLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
        || expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression)
           && (ContainsStringLiteral(bin.Left) || ContainsStringLiteral(bin.Right));

    private static List<ExpressionSyntax> FlattenConcatTree(ExpressionSyntax expr)
    {
        if (expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression))
            return FlattenConcatTree(bin.Left).Concat(FlattenConcatTree(bin.Right)).ToList();
        return new List<ExpressionSyntax> { expr };
    }

    private static bool IsTaskReturnType(TypeSyntax type) =>
        type is IdentifierNameSyntax id && id.Identifier.Text is "Task" or "ValueTask"
        || type is GenericNameSyntax gn && gn.Identifier.Text is "Task" or "ValueTask";

    public async Task<Dictionary<string, string>> ExtractServiceFromControllerAsync(string filePath, string controllerName, string serviceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var controller = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == controllerName);
        if (controller == null) throw new Exception("Controller not found.");

        // Extract private methods and complex logic from public endpoints
        var methodsToMove = controller.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PrivateKeyword) || m.Identifier.Text.StartsWith("Process") || m.Identifier.Text.StartsWith("Calculate")))
            .ToList();

        var serviceClass = SyntaxFactory.ClassDeclaration(serviceName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(methodsToMove.Select(m => m.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))).ToArray());

        var newController = controller.RemoveNodes(methodsToMove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        
        // In a real scenario, we'd inject the IService into the controller constructor here.
        var updatedRoot = root!.ReplaceNode(controller, newController!);

        var ns = controller.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var serviceRoot = SyntaxFactory.CompilationUnit().WithUsings(root.Usings);
        
        if (ns != null)
        {
             var newNs = ns is FileScopedNamespaceDeclarationSyntax 
                ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name)
                : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
             serviceRoot = serviceRoot.AddMembers(newNs.AddMembers(serviceClass));
        }
        else
        {
            serviceRoot = serviceRoot.AddMembers(serviceClass);
        }

        return new Dictionary<string, string>
        {
            { filePath, updatedRoot.ToFullString() },
            { Path.Combine(Path.GetDirectoryName(filePath)!, $"{serviceName}.cs"), serviceRoot.NormalizeWhitespace().ToFullString() }
        };
    }
}
