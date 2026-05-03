using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public class AdvancedLogicEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedLogicEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<Dictionary<string, string>> InvertBooleanLogicAsync(string filePath, string boolName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        var variable = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == boolName);
        ISymbol? symbol = null;
        if (variable != null) symbol = semanticModel?.GetDeclaredSymbol(variable, cancellationToken);
        else 
        {
            var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == boolName);
            if (method != null) symbol = semanticModel?.GetDeclaredSymbol(method, cancellationToken);
        }

        if (symbol == null) return new Dictionary<string, string>();

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var updatedSolution = solution;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDocument = updatedSolution.GetDocument(location.Document.Id)!;
                var refRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);
                var node = refRoot?.FindNode(location.Location.SourceSpan) as ExpressionSyntax;
                
                if (node != null)
                {
                    var parent = node.Parent;
                    if (parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.LogicalNotExpression))
                    {
                        refRoot = refRoot!.ReplaceNode(p, node);
                    }
                    else
                    {
                        var inverted = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, node);
                        refRoot = refRoot!.ReplaceNode(node, inverted);
                    }
                    updatedSolution = updatedSolution.WithDocumentSyntaxRoot(refDocument.Id, refRoot!);
                }
            }
        }
        
        var changes = new Dictionary<string, string>();
        foreach (var projectChange in updatedSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var doc = updatedSolution.GetDocument(changedDocId)!;
                changes[doc.FilePath ?? doc.Name] = (await doc.GetTextAsync(cancellationToken)).ToString();
            }
        }
        return changes;
    }

    public async Task<string> ConvertIfToSwitchExpressionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertIfToSwitchStatementAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ExtensionToStaticAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Remove(firstParam.Modifiers.First(m => m.IsKind(SyntaxKind.ThisKeyword))));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                return root!.ReplaceNode(methodNode, newMethod).NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertStaticToExtensionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ThisKeyword)));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                var updatedRoot = root!.ReplaceNode(methodNode, newMethod);

                // Also ensure the containing class is marked as static
                // Find the class in the updated root
                var classNode = updatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == methodName));
                if (classNode != null && !classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    var newClass = classNode.WithModifiers(classNode.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
                    updatedRoot = updatedRoot.ReplaceNode(classNode, newClass);
                }

                return updatedRoot.NormalizeWhitespace().ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertForEachToForAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return "";

        var forEach = root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);

        if (forEach == null) return root.ToFullString();

        var collection = forEach.Expression;
        var varName = forEach.Identifier.Text;

        // Determine whether to use .Length or .Count
        string lengthProp = "Count";
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(collection, ct);
            if (typeInfo.Type?.TypeKind == TypeKind.Array)
                lengthProp = "Length";
        }
        else
        {
            // Fallback: if the type syntax has [] it's an array
            if (forEach.Type.ToString().Contains("[]"))
                lengthProp = "Length";
        }

        // Pick a safe index variable name
        var bodyText = forEach.Statement.ToFullString();
        string indexVar = "i";
        if (System.Text.RegularExpressions.Regex.IsMatch(bodyText, @"\bi\b"))
            indexVar = "j";
        if (indexVar == "j" && System.Text.RegularExpressions.Regex.IsMatch(bodyText, @"\bj\b"))
            indexVar = "k";

        var collectionStr = collection.ToString();

        // var varName = collection[indexVar];
        var elementAccessStmt = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(varName),
                        null,
                        SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ElementAccessExpression(
                                SyntaxFactory.ParseExpression(collectionStr),
                                SyntaxFactory.BracketedArgumentList(
                                    SyntaxFactory.SingletonSeparatedList(
                                        SyntaxFactory.Argument(
                                            SyntaxFactory.IdentifierName(indexVar))))))))));

        // Build the body block
        BlockSyntax newBody;
        if (forEach.Statement is BlockSyntax block)
        {
            newBody = block.WithStatements(block.Statements.Insert(0, elementAccessStmt));
        }
        else
        {
            newBody = SyntaxFactory.Block(elementAccessStmt, forEach.Statement);
        }

        // for (int i = 0; i < collection.LengthOrCount; i++)
        var initializer = SyntaxFactory.VariableDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(indexVar),
                    null,
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(0))))));

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.LessThanExpression,
            SyntaxFactory.IdentifierName(indexVar),
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression(collectionStr),
                SyntaxFactory.IdentifierName(lengthProp)));

        var incrementors = SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
            SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.PostIncrementExpression,
                SyntaxFactory.IdentifierName(indexVar)));

        var forStatement = SyntaxFactory.ForStatement(
            initializer,
            SyntaxFactory.SeparatedList<ExpressionSyntax>(),
            condition,
            incrementors,
            newBody);

        var newRoot = root.ReplaceNode(forEach, forStatement);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertForToForEachAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertWhileToForAsync(string filePath, int line, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }
}
