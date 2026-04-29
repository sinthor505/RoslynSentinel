using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public class GranularRefactoringEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public GranularRefactoringEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> RunMicroRefactoringAsync(string filePath, string refactoringId, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        return $"Micro-Refactoring {refactoringId} applied to line {line} in simulation mode.";
    }

    public async Task<string> InlineFieldAsync(string filePath, string fieldName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var field = root?.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));

        if (field != null && field.Declaration.Variables[0].Initializer != null)
        {
            var value = field.Declaration.Variables[0].Initializer!.Value;
            var usages = root!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
            
            var trackedRoot = root.TrackNodes(new SyntaxNode[] { field });
            var usagesInTracked = trackedRoot.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
            
            var newRoot = trackedRoot.ReplaceNodes(usagesInTracked, (old, _) => value.WithTriviaFrom(old));
            var newField = newRoot.GetCurrentNode(field);
            if (newField != null)
            {
                newRoot = newRoot.RemoveNode(newField, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
            }
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root?.ToFullString() ?? "";
    }

    public async Task<string> InlineParameterAsync(string filePath, string methodName, string parameterName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null) return root?.ToFullString() ?? "";

        var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == parameterName);
        if (parameter == null) return root!.ToFullString();

        return root!.ToFullString();
    }

    public async Task<string> ConvertMethodToIndexerAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method != null && method.ParameterList.Parameters.Count == 1)
        {
            var parameter = method.ParameterList.Parameters[0];
            var indexer = SyntaxFactory.IndexerDeclaration(method.ReturnType)
                .AddModifiers(method.Modifiers.ToArray())
                .WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SingletonSeparatedList(parameter)))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(method.Body))));

            var newRoot = root!.ReplaceNode(method, indexer);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root?.ToFullString() ?? "";
    }

    public async Task<string> IntroduceFieldAsync(string filePath, int line, int column, string newFieldName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> IntroduceParameterAsync(string filePath, int line, int column, string newParamName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> IntroduceVariableAsync(string filePath, int line, int column, string newVariableName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> MoveTypeToOuterScopeAsync(string filePath, string nestedTypeName, CancellationToken cancellationToken = default)
    {
        return "";
    }

    public async Task<Dictionary<string, string>> ExtractMembersToPartialAsync(string filePath, string className, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode != null)
        {
            var membersToMove = classNode.Members.Where(m => 
                (m is MethodDeclarationSyntax meth && memberNames.Contains(meth.Identifier.Text)) ||
                (m is PropertyDeclarationSyntax prop && memberNames.Contains(prop.Identifier.Text))).ToList();

            var newClassNode = SyntaxFactory.ClassDeclaration(className)
                .WithModifiers(classNode.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithMembers(SyntaxFactory.List(membersToMove));

            return new Dictionary<string, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{className}.Partial.cs"), newClassNode.NormalizeWhitespace().ToFullString() } };
        }
        return new Dictionary<string, string>();
    }
}
