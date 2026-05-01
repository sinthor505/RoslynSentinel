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
        if (root == null) return "";

        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null) return root.ToFullString();

        var containingClass = expression.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (containingClass == null) return root.ToFullString();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax fieldType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            fieldType = typeInfo.Type != null
                ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            fieldType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var fieldDeclaration = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(fieldType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(newFieldName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

        var fieldRef = SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(expression);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingClass });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(expression)!, fieldRef);
        var currentClass = newRoot.GetCurrentNode(containingClass)!;
        var newClass = currentClass.WithMembers(currentClass.Members.Insert(0, fieldDeclaration));
        newRoot = newRoot.ReplaceNode(currentClass, newClass);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> IntroduceParameterAsync(string filePath, int line, int column, string newParamName, CancellationToken cancellationToken = default)
    {
        // NOTE: Single-file only — call sites in other files are not updated.
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null) return root.ToFullString();

        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null) return root.ToFullString();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax paramType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            paramType = typeInfo.Type != null
                ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            paramType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(newParamName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));
        var paramRef = SyntaxFactory.IdentifierName(newParamName).WithTriviaFrom(expression);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingMethod });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(expression)!, paramRef);
        var currentMethod = newRoot.GetCurrentNode(containingMethod)!;
        var updatedMethod = currentMethod.WithParameterList(currentMethod.ParameterList.AddParameters(newParameter));
        newRoot = newRoot.ReplaceNode(currentMethod, updatedMethod);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> IntroduceVariableAsync(string filePath, int line, int column, string newVariableName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null) return root.ToFullString();

        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null) return root.ToFullString();
        if (containingStatement.Parent is not BlockSyntax block) return root.ToFullString();

        var varDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(newVariableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))));

        var varRef = SyntaxFactory.IdentifierName(newVariableName).WithTriviaFrom(expression);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingStatement, block });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(expression)!, varRef);
        var currentStatement = newRoot.GetCurrentNode(containingStatement)!;
        var currentBlock = newRoot.GetCurrentNode(block)!;
        var idx = currentBlock.Statements.IndexOf(currentStatement);
        if (idx < 0) return root.ToFullString();
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        newRoot = newRoot.ReplaceNode(currentBlock, newBlock);

        return newRoot.NormalizeWhitespace().ToFullString();
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
