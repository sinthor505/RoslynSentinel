using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;

public class RefinementEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public RefinementEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Pulls a member (method/property) up to the base class or interface.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> PullUpMemberAsync(FilePath filePath, string className, string memberName, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"File '{filePath}' not found." } };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Failed to get syntax root for '{filePath}'." } };
            }

            var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
            if (classNode == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Class '{className}' not found." } };
            }

            var member = classNode.Members.FirstOrDefault(m =>
            (m is MethodDeclarationSyntax meth && meth.Identifier.Text == memberName) ||
            (m is PropertyDeclarationSyntax prop && prop.Identifier.Text == memberName));

            if (member == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Member '{memberName}' not found in class '{className}'." } };
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var classSymbol = semanticModel?.GetDeclaredSymbol(classNode, cancellationToken);
            var baseType = classSymbol?.BaseType;

            if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            {
                return new Dictionary<FilePath, string> { { "error", "No base class found to pull up to." } };
            }

            var baseFile = baseType.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (baseFile == null)
            {
                return new Dictionary<FilePath, string> { { "error", "Base class source file not found." } };
            }

            if (baseType.DeclaringSyntaxReferences.Length == 0)
            {
                return new Dictionary<FilePath, string> { { "error", "Base class is in an external assembly and cannot be modified." } };
            }

            var baseDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath == baseFile);
            if (baseDoc == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Base class source document not found at '{baseFile}'." } };
            }

            var baseRoot = await baseDoc.GetSyntaxRootAsync(cancellationToken);
            if (baseRoot == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Failed to get syntax root for base class file '{baseFile}'." } };
            }

            var baseClassNode = baseRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == baseType.Name);
            if (baseClassNode == null)
            {
                return new Dictionary<FilePath, string> { { "error", $"Base class '{baseType.Name}' not found in '{baseFile}'." } };
            }

            // Remove 'override', add 'virtual' (if not already abstract/virtual)
            SyntaxTokenList AdjustModifiers(SyntaxTokenList modifiers)
            {
                var overrideToken = modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.OverrideKeyword));
                if (overrideToken != default)
                {
                    modifiers = modifiers.Remove(overrideToken);
                }

                if (!modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
                {
                    modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithLeadingTrivia(SyntaxFactory.Space));
                }

                return modifiers;
            }

            MemberDeclarationSyntax memberForBase = member switch
            {
                MethodDeclarationSyntax m => m.WithModifiers(AdjustModifiers(m.Modifiers)),
                PropertyDeclarationSyntax p => p.WithModifiers(AdjustModifiers(p.Modifiers)),
                _ => member
            };

            var newDerivedRoot = root.RemoveNode(member, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            if (newDerivedRoot == null)
            {
                return new Dictionary<FilePath, string> { { "error", "Failed to remove member from derived class." } };
            }

            var newBaseClassNode = baseClassNode.AddMembers(memberForBase);
            var newBaseRoot = baseRoot.ReplaceNode(baseClassNode, newBaseClassNode);

            return new Dictionary<FilePath, string>
        {
            { filePath, newDerivedRoot.NormalizeWhitespace().ToFullString() },
            { baseFile, newBaseRoot.NormalizeWhitespace().ToFullString() }
        };
        }
        catch (Exception ex)
        {
            return new Dictionary<FilePath, string> { { "error", ex.Message } };
        }
    }

    /// <summary>
    /// Inlines a simple method (expression-body or single-return-statement) by replacing ALL call sites
    /// solution-wide with the method's expression, then removing the method declaration.
    /// Returns a dictionary of filePath→updatedContent for every affected file.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> InlineMethodAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new Dictionary<FilePath, string>
            { { "__error__", $"File '{Path.GetFileName(filePath)}' not found in solution." } };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new Dictionary<FilePath, string>
            { { "__error__", $"Failed to get syntax root for '{filePath}'." } };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null)
        {
            return new Dictionary<FilePath, string>
                { { "__error__", $"Method '{methodName}' not found in '{filePath}'." } };
        }

        ExpressionSyntax? expressionToInline = null;
        if (method.ExpressionBody != null)
        {
            expressionToInline = method.ExpressionBody.Expression;
        }
        else if (method.Body?.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax ret)
        {
            expressionToInline = ret.Expression;
        }

        if (expressionToInline == null)
        {
            return new Dictionary<FilePath, string>
            {
                { "__error__", $"Cannot inline '{methodName}': only expression-body or single-return-statement methods are supported. This method has a complex body with multiple statements." }
            };
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        if (methodSymbol == null)
        {
            return new Dictionary<FilePath, string>
                { { "__error__", $"Cannot inline '{methodName}': failed to resolve semantic symbol." } };
        }

        // Find ALL references across the solution grouped by document
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var byDocument = references
            .SelectMany(r => r.Locations)
            .Where(l => l.Document.FilePath != null)
            .GroupBy(l => l.Document.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<FilePath, string>();
        var expressionTemplate = expressionToInline; // capture once

        // Process each document that has call sites (including the defining document)
        foreach (var (docId, locations) in byDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var callSiteNodes = new List<InvocationExpressionSyntax>();
            foreach (var location in locations)
            {
                var node = docRoot.FindNode(location.Location.SourceSpan)
                    .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (node != null)
                {
                    callSiteNodes.Add(node);
                }
            }

            if (callSiteNodes.Count == 0)
            {
                continue;
            }

            var updatedDocRoot = docRoot.ReplaceNodes(
                callSiteNodes,
                (original, _) => expressionTemplate.WithTriviaFrom(original));
            result[doc.FilePath] = updatedDocRoot.NormalizeWhitespace().ToFullString();
        }

        // Remove the method declaration from the defining document
        var definingFilePath = document.FilePath!;
        var definingRoot = result.TryGetValue(definingFilePath, out var already)
            ? Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(already, cancellationToken: cancellationToken).GetRoot(cancellationToken)
            : root;
        var methodToRemove = definingRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodToRemove != null)
        {
            var withoutMethod = definingRoot.RemoveNode(methodToRemove, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = withoutMethod?.NormalizeWhitespace().ToFullString() ?? definingRoot.ToFullString();
        }
        else if (!result.ContainsKey(definingFilePath))
        {
            // Method had no callers but still needs the declaration removed
            var withoutMethod = root.RemoveNode(method, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            result[definingFilePath] = withoutMethod?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
        }

        return result;
    }
}
