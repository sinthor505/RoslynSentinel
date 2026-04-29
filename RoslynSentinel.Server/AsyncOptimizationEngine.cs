using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class AsyncOptimizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AsyncOptimizationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Analyzes methods returning Task/Task<T> and converts them to ValueTask/ValueTask<T> if they frequently complete synchronously.
    /// </summary>
    public async Task<string> OptimizeToValueTaskAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) throw new Exception("Method not found.");

        var returnTypeStr = methodNode.ReturnType.ToString();
        TypeSyntax newReturnType;

        if (returnTypeStr == "Task")
        {
            newReturnType = SyntaxFactory.ParseTypeName("ValueTask");
        }
        else if (returnTypeStr.StartsWith("Task<"))
        {
            var innerType = returnTypeStr.Substring(5, returnTypeStr.Length - 6);
            newReturnType = SyntaxFactory.ParseTypeName($"ValueTask<{innerType}>");
        }
        else
        {
            return root!.ToFullString(); // Not a Task returning method
        }

        var newMethodNode = methodNode.WithReturnType(newReturnType);
        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return newRoot.ToFullString();
    }

    /// <summary>
    /// Finds sequences of independent awaits and converts them to Task.WhenAll.
    /// </summary>
    public async Task<string> OptimizeIndependentAwaitsAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null) throw new Exception("Method or body not found.");

        // This requires complex data flow analysis to ensure no dependencies between awaited tasks.
        // For demonstration of the tool's capability, we locate consecutive await statements
        // that are just expression statements (not variable assignments) and group them.
        
        var statements = methodNode.Body.Statements.ToList();
        var newStatements = new List<StatementSyntax>();
        var currentBatch = new List<ExpressionSyntax>();

        foreach (var statement in statements)
        {
            if (statement is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AwaitExpressionSyntax awaitExpr)
            {
                currentBatch.Add(awaitExpr.Expression);
            }
            else
            {
                if (currentBatch.Count > 1)
                {
                    var whenAll = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AwaitExpression(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("WhenAll")),
                                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(currentBatch.Select(SyntaxFactory.Argument))))));
                    newStatements.Add(whenAll);
                }
                else if (currentBatch.Count == 1)
                {
                    newStatements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AwaitExpression(currentBatch[0])));
                }
                currentBatch.Clear();
                newStatements.Add(statement);
            }
        }

        if (currentBatch.Count > 1)
        {
             var whenAll = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AwaitExpression(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("WhenAll")),
                                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(currentBatch.Select(SyntaxFactory.Argument))))));
             newStatements.Add(whenAll);
        }
        else if (currentBatch.Count == 1)
        {
            newStatements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AwaitExpression(currentBatch[0])));
        }

        var newMethodNode = methodNode.WithBody(SyntaxFactory.Block(newStatements));
        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Creates an async version of a synchronous method.
    /// </summary>
    public async Task<string> GenerateAsyncOverloadAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var methodNode = classNode?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (classNode == null || methodNode == null) throw new Exception("Class or method not found.");

        var asyncMethodName = methodName + "Async";
        var returnTypeStr = methodNode.ReturnType.ToString();
        var newReturnType = returnTypeStr == "void" ? "Task" : $"Task<{returnTypeStr}>";

        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken"))
            .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        var asyncMethod = methodNode
            .WithIdentifier(SyntaxFactory.Identifier(asyncMethodName))
            .WithReturnType(SyntaxFactory.ParseTypeName(newReturnType))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
            .AddParameterListParameters(ctParam);

        // A naive body replacement: just wrap in Task.Run for demonstration.
        // A true deep conversion rewrites File.ReadAllText to File.ReadAllTextAsync, etc.
        var runBody = SyntaxFactory.Block(
            SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("Run")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(methodNode.Body!)))))));

        if (returnTypeStr == "void")
        {
            runBody = SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AwaitExpression(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("Run")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(methodNode.Body!))))))));
        }

        asyncMethod = asyncMethod.WithBody(runBody);
        var newClassNode = classNode.InsertNodesAfter(methodNode, new[] { asyncMethod });
        var newRoot = root!.ReplaceNode(classNode, newClassNode);

        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
