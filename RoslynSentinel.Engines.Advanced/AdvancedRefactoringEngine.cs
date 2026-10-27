using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Engines.Advanced;

public class AdvancedRefactoringEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<AdvancedRefactoringEngine> _logger;
    private readonly SentinelConfiguration _config;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    public AdvancedRefactoringEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<AdvancedRefactoringEngine>();
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<AdvancedRefactoringEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<AdvancedRefactoringEngine> logger, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = config;
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ILogger<AdvancedRefactoringEngine> logger, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = config;
        _symbolNavigationEngine = symbolNavigationEngine;
    }

    public async Task<DocumentEditResult> OptimizeTaskWaitAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException($"Could not parse '{filePath}' into a syntax tree, so task waits cannot be optimized.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var methodsToMakeAsync = new List<MethodDeclarationSyntax>();
        var methodSpans = new HashSet<int>();
        bool IsTaskType(ExpressionSyntax expr)
        {
            if (semanticModel == null)
            {
                return false;
            }

            var typeInfo = semanticModel.GetTypeInfo(expr, cancellationToken);
            var name = typeInfo.Type?.Name;
            return name is "Task" or "ValueTask";
        }

        void CollectMethod(SyntaxNode node)
        {
            var m = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (m != null && methodSpans.Add(m.SpanStart))
            {
                methodsToMakeAsync.Add(m);
            }
        }

        // Pattern 1: .GetAwaiter().GetResult()
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax maGet)
            {
                continue;
            }

            if (maGet.Name.Identifier.Text != "GetResult")
            {
                continue;
            }

            if (maGet.Expression is not InvocationExpressionSyntax getAwaiterCall)
            {
                continue;
            }

            if (getAwaiterCall.Expression is not MemberAccessExpressionSyntax maAwaiter)
            {
                continue;
            }

            if (maAwaiter.Name.Identifier.Text != "GetAwaiter")
            {
                continue;
            }

            if (!IsTaskType(maAwaiter.Expression))
            {
                continue;
            }

            replacements[inv] = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.AwaitExpression(SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space), maAwaiter.Expression.WithoutTrivia())).WithTriviaFrom(inv).WithAdditionalAnnotations(Formatter.Annotation);
            CollectMethod(inv);
        }

        // Pattern 2: .Wait() with no arguments
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (replacements.ContainsKey(inv))
            {
                continue;
            }

            if (inv.Expression is not MemberAccessExpressionSyntax maWait)
            {
                continue;
            }

            if (maWait.Name.Identifier.Text != "Wait")
            {
                continue;
            }

            if (inv.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }

            if (!IsTaskType(maWait.Expression))
            {
                continue;
            }

            replacements[inv] = SyntaxFactory.AwaitExpression(SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space), maWait.Expression.WithoutTrivia()).WithTriviaFrom(inv).WithAdditionalAnnotations(Formatter.Annotation);
            CollectMethod(inv);
        }

        // Pattern 3: .Result member access
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (ma.Name.Identifier.Text != "Result")
            {
                continue;
            }

            if (replacements.Keys.Any(k => k.Span.Contains(ma.Span)))
            {
                continue;
            }

            if (!IsTaskType(ma.Expression))
            {
                continue;
            }

            replacements[ma] = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.AwaitExpression(SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space), ma.Expression.WithoutTrivia())).WithTriviaFrom(ma).WithAdditionalAnnotations(Formatter.Annotation);
            CollectMethod(ma);
        }

        if (replacements.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoCandidatesFound,
                FilePath = filePath,
                UpdatedText = root.ToFullString()
            };
        }

        // Track all nodes that will be mutated before any tree modification
        var trackedRoot = root.TrackNodes(replacements.Keys.Concat(methodsToMakeAsync.Cast<SyntaxNode>()));
        var trackedReplacements = replacements.Select(kvp => (Tracked: trackedRoot.GetCurrentNode(kvp.Key), Value: kvp.Value)).Where(t => t.Tracked != null).ToDictionary(t => t.Tracked!, t => t.Value);
        var newRoot = trackedRoot.ReplaceNodes(trackedReplacements.Keys, (original, _) => trackedReplacements[original]);
        // Make affected methods async
        var methodUpdates = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var method in methodsToMakeAsync)
        {
            var current = newRoot.GetCurrentNode(method);
            if (current == null || current.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                continue;
            }

            var asyncToken = SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space).WithAdditionalAnnotations(Formatter.Annotation);
            var newModifiers = current.Modifiers.Add(asyncToken);
            TypeSyntax newReturn = current.ReturnType;
            if (current.ReturnType is PredefinedTypeSyntax pred && pred.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                newReturn = SyntaxFactory.IdentifierName("Task").WithTriviaFrom(current.ReturnType).WithAdditionalAnnotations(Formatter.Annotation);
            }
            else if (!IsTaskReturnType(current.ReturnType))
            {
                newReturn = SyntaxFactory.GenericName(SyntaxFactory.Identifier("Task")).WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(current.ReturnType.WithoutTrivia()))).WithTriviaFrom(current.ReturnType).WithAdditionalAnnotations(Formatter.Annotation);
            }

            methodUpdates[current] = current.WithModifiers(newModifiers).WithReturnType(newReturn);
        }

        if (methodUpdates.Count != 0)
        {
            newRoot = newRoot.ReplaceNodes(methodUpdates.Keys, (original, _) => methodUpdates[original]);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = (await (await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken)).GetTextAsync(cancellationToken)).ToString(),
            FilePath = filePath
        };
    }

    private static bool IsTaskReturnType(TypeSyntax type) => (type is IdentifierNameSyntax id && id.Identifier.Text is "Task" or "ValueTask") || (type is GenericNameSyntax gn && gn.Identifier.Text is "Task" or "ValueTask");

    public async Task<DocumentEditResult> SyncInterfaceToImplementationAsync(FilePathWrapper filePath, string className, string interfaceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        // Find the class document
        var classDocument = DocumentLookup.RequireDocument(solution, filePath);

        var classRoot = await classDocument.GetSyntaxRootAsync(cancellationToken);
        if (classRoot == null)
        {
            throw new ToolInvalidArgumentException($"Could not parse '{filePath}' into a syntax tree, so the interface cannot be synced.");
        }

        var classMatches = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == className).ToList();
        var classNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(classMatches, "Class", className);
        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' was not found in '{System.IO.Path.GetFileName(filePath)}'. Searched: class declarations in this file. Next: {ToolCallHint.Call("Search", ("mode", "class"), ("query", className))}", "class", className);
        }

        // Collect public non-static non-override methods and properties from the class
        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))).ToList();
        var publicProperties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))).ToList();
        // Find the interface -> first in same file, then in other documents
        Document? interfaceDocument = null;
        InterfaceDeclarationSyntax? interfaceNode = null;
        SyntaxNode? interfaceRoot = null;
        // Search same file first; only when it has none, collect across all documents. Each stage is resolved with RequireSingle.
        var sameFileInterfaces = classRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Where(i => i.Identifier.Text == interfaceName).ToList();
        if (sameFileInterfaces.Count > 0)
        {
            interfaceNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(sameFileInterfaces, "Interface", interfaceName);
            interfaceDocument = classDocument;
            interfaceRoot = classRoot;
        }
        else
        {
            // Search all documents
            var otherInterfaces = new List<InterfaceDeclarationSyntax>();
            foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
            {
                if (doc == classDocument)
                {
                    continue;
                }

                var r = await doc.GetSyntaxRootAsync(cancellationToken);
                if (r == null)
                {
                    continue;
                }

                otherInterfaces.AddRange(r.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Where(i => i.Identifier.Text == interfaceName));
            }

            interfaceNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(otherInterfaces, "Interface", interfaceName);
            if (interfaceNode != null)
            {
                interfaceRoot = interfaceNode.SyntaxTree.GetRoot(cancellationToken);
                interfaceDocument = solution.GetDocument(interfaceNode.SyntaxTree);
            }
        }

        if (interfaceNode == null || interfaceDocument == null || interfaceRoot == null)
        {
            throw new ToolNotFoundException($"Interface '{interfaceName}' was not found in '{System.IO.Path.GetFileName(filePath)}' or any other loaded document. Searched: all project documents. Next: {ToolCallHint.Call("Search", ("mode", "interface"), ("query", interfaceName))}", "interface", interfaceName);
        }

        // Collect existing interface member signatures (for deduplication)
        var existingMethodSigs = interfaceNode.Members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text + "|" + string.Join(",", m.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim()))).ToHashSet(StringComparer.Ordinal);
        var existingPropertyNames = interfaceNode.Members.OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var method in publicMethods)
        {
            var sig = method.Identifier.Text + "|" + string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim()));
            if (existingMethodSigs.Contains(sig))
            {
                continue;
            }

            // Build interface method: return type + name + params, no body. The parts reused from the class method are
            // stripped of ALL trivia (comments and newlines included) so the new member starts with no existing formatting,
            // the doc comment or an inline comment on the class method is dropped on purpose.
            var ifaceMethod = SyntaxFactory.MethodDeclaration(RoslynFormattingHelper.StripAllTrivia(method.ReturnType), method.Identifier.WithoutTrivia()).WithParameterList(RoslynFormattingHelper.StripAllTrivia(method.ParameterList)).WithTypeParameterList(method.TypeParameterList is null ? null : RoslynFormattingHelper.StripAllTrivia(method.TypeParameterList)).WithConstraintClauses(SyntaxFactory.List(method.ConstraintClauses.Select(c => RoslynFormattingHelper.StripAllTrivia(c)))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithModifiers(SyntaxFactory.TokenList()).WithAdditionalAnnotations(Formatter.Annotation);
            newMembers.Add(ifaceMethod);
        }

        foreach (var prop in publicProperties)
        {
            if (existingPropertyNames.Contains(prop.Identifier.Text))
            {
                continue;
            }

            // Build interface property
            var hasGetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true || prop.ExpressionBody != null;
            var hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
            var hasInit = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
            var accessors = new List<AccessorDeclarationSyntax>();
            if (hasGetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasSetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasInit)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            var ifaceProp = SyntaxFactory.PropertyDeclaration(RoslynFormattingHelper.StripAllTrivia(prop.Type), prop.Identifier.WithoutTrivia()).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))).WithModifiers(SyntaxFactory.TokenList()).WithAdditionalAnnotations(Formatter.Annotation);
            newMembers.Add(ifaceProp);
        }

        if (newMembers.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.AlreadyInTargetState,
                FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
                UpdatedText = interfaceRoot.ToFullString() // Already up to date
            };
        }

        var newInterfaceNode = interfaceNode.AddMembers(newMembers.ToArray());
        // Only the new members carry Formatter.Annotation, so only they are re-spaced; untouched interface members keep their text.
        var newRoot = interfaceRoot.ReplaceNode(interfaceNode, newInterfaceNode);
        var formatted = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(interfaceDocument, newRoot, await _workspaceManager.ResolveEolAsync(interfaceDocument, cancellationToken), cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }
}