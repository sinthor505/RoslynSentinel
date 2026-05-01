using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

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
    public async Task<Dictionary<string, string>> PullUpMemberAsync(string filePath, string className, string memberName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        var member = classNode.Members.FirstOrDefault(m => 
            (m is MethodDeclarationSyntax meth && meth.Identifier.Text == memberName) ||
            (m is PropertyDeclarationSyntax prop && prop.Identifier.Text == memberName));

        if (member == null) throw new Exception("Member not found.");

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var classSymbol = semanticModel?.GetDeclaredSymbol(classNode);
        var baseType = classSymbol?.BaseType;

        if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            throw new Exception("No base class found to pull up to.");

        var baseFile = baseType.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        if (baseFile == null) throw new Exception("Base class source file not found.");

        if (baseType.DeclaringSyntaxReferences.Length == 0)
            throw new Exception("Base class is in an external assembly and cannot be modified.");

        var baseDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath == baseFile);
        if (baseDoc == null) throw new Exception($"Base class source document not found at '{baseFile}'.");

        var baseRoot = await baseDoc.GetSyntaxRootAsync(cancellationToken);
        var baseClassNode = baseRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == baseType.Name);
        if (baseClassNode == null) throw new Exception($"Base class '{baseType.Name}' not found in '{baseFile}'.");

        // Remove 'override', add 'virtual' (if not already abstract/virtual)
        SyntaxTokenList AdjustModifiers(SyntaxTokenList modifiers)
        {
            var overrideToken = modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.OverrideKeyword));
            if (overrideToken != default)
                modifiers = modifiers.Remove(overrideToken);
            if (!modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
                modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithLeadingTrivia(SyntaxFactory.Space));
            return modifiers;
        }

        MemberDeclarationSyntax memberForBase = member switch
        {
            MethodDeclarationSyntax m => m.WithModifiers(AdjustModifiers(m.Modifiers)),
            PropertyDeclarationSyntax p => p.WithModifiers(AdjustModifiers(p.Modifiers)),
            _ => member
        };

        var newDerivedRoot = root!.RemoveNode(member, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        var newBaseClassNode = baseClassNode.AddMembers(memberForBase);
        var newBaseRoot = baseRoot!.ReplaceNode(baseClassNode, newBaseClassNode);

        return new Dictionary<string, string>
        {
            { filePath, newDerivedRoot.NormalizeWhitespace().ToFullString() },
            { baseFile, newBaseRoot.NormalizeWhitespace().ToFullString() }
        };
    }

    /// <summary>
    /// Inlines a simple single-statement method by replacing all its call sites with the statement's expression.
    /// </summary>
    public async Task<string> InlineMethodAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null) return root?.ToFullString() ?? "";

        ExpressionSyntax? expressionToInline = null;
        if (method.ExpressionBody != null)
        {
            expressionToInline = method.ExpressionBody.Expression;
        }
        else if (method.Body?.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax ret)
        {
            expressionToInline = ret.Expression;
        }

        if (expressionToInline == null) return root!.ToFullString();

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
        if (methodSymbol == null) return root!.ToFullString();

        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var updatedRoot = root;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Document.Id == document.Id)
                {
                    var node = updatedRoot!.FindNode(location.Location.SourceSpan).AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (node != null)
                    {
                        updatedRoot = updatedRoot.ReplaceNode(node, expressionToInline.WithTriviaFrom(node));
                    }
                }
            }
        }

        updatedRoot = updatedRoot!.RemoveNode(updatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == methodName), SyntaxRemoveOptions.KeepUnbalancedDirectives);
        
        return updatedRoot!.NormalizeWhitespace().ToFullString();
    }
}
