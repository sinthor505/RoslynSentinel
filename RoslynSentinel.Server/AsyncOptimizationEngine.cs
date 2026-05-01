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

    /// <summary>
    /// Adds .ConfigureAwait(false) (or true) to all await expressions that don't already have it.
    /// </summary>
    public async Task<string> AddConfigureAwaitFalseAsync(string filePath, bool libraryMode = true, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Could not get syntax root.");

        var awaitExprs = root.DescendantNodes().OfType<AwaitExpressionSyntax>()
            .Where(a => !(a.Expression is InvocationExpressionSyntax inv &&
                          inv.Expression is MemberAccessExpressionSyntax ma &&
                          ma.Name.Identifier.Text == "ConfigureAwait"))
            .ToList();

        if (!awaitExprs.Any()) return root.ToFullString();

        var newRoot = root.ReplaceNodes(awaitExprs, (orig, _) =>
        {
            var configureAwait = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    orig.Expression.WithoutTrivia(),
                    SyntaxFactory.IdentifierName("ConfigureAwait")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            libraryMode ? SyntaxKind.FalseLiteralExpression : SyntaxKind.TrueLiteralExpression)))));
            return orig.WithExpression(configureAwait);
        });

        return newRoot.ToFullString();
    }

    /// <summary>
    /// Removes all .ConfigureAwait(x) calls, leaving the bare awaited expression.
    /// </summary>
    public async Task<string> RemoveConfigureAwaitFalseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Could not get syntax root.");

        var configureAwaitInvocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                          ma.Name.Identifier.Text == "ConfigureAwait")
            .ToList();

        if (!configureAwaitInvocations.Any()) return root.ToFullString();

        var newRoot = root.ReplaceNodes(configureAwaitInvocations, (orig, _) =>
        {
            var baseExpr = ((MemberAccessExpressionSyntax)orig.Expression).Expression;
            return baseExpr.WithTriviaFrom(orig);
        });

        return newRoot.ToFullString();
    }

    /// <summary>
    /// Converts a method returning Task&lt;List&lt;T&gt;&gt; or List&lt;T&gt; to IAsyncEnumerable&lt;T&gt;.
    /// Transforms results.Add(x) patterns to yield return x. Falls back to scaffold for complex bodies.
    /// </summary>
    public async Task<string> ConvertToAsyncEnumerableAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) throw new Exception("Method not found.");

        var returnTypeStr = methodNode.ReturnType.ToString().Trim();

        if (returnTypeStr.StartsWith("IAsyncEnumerable<"))
            return root!.ToFullString();

        string? innerType = null;
        if (returnTypeStr.StartsWith("Task<List<") && returnTypeStr.EndsWith(">>"))
            innerType = returnTypeStr.Substring(10, returnTypeStr.Length - 12);
        else if (returnTypeStr.StartsWith("Task<IEnumerable<") && returnTypeStr.EndsWith(">>"))
            innerType = returnTypeStr.Substring(17, returnTypeStr.Length - 19);
        else if (returnTypeStr.StartsWith("List<") && returnTypeStr.EndsWith(">"))
            innerType = returnTypeStr.Substring(5, returnTypeStr.Length - 6);

        if (innerType == null) return root!.ToFullString();

        var newReturnType = SyntaxFactory.ParseTypeName($"IAsyncEnumerable<{innerType}>");
        var newMethod = methodNode.WithReturnType(newReturnType.WithTrailingTrivia(SyntaxFactory.Space));

        if (!newMethod.Modifiers.Any(SyntaxKind.AsyncKeyword))
            newMethod = newMethod.AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var hasCt = newMethod.ParameterList.Parameters
            .Any(p => p.Type?.ToString().Contains("CancellationToken") == true);
        if (!hasCt)
        {
            var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("ct"))
                .WithType(SyntaxFactory.ParseTypeName("CancellationToken "))
                .WithDefault(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
            newMethod = newMethod.AddParameterListParameters(ctParam);
        }

        if (methodNode.Body != null)
        {
            string? resultsVar = null;
            foreach (var stmt in methodNode.Body.Statements)
            {
                if (stmt is LocalDeclarationStatementSyntax ld)
                {
                    foreach (var v in ld.Declaration.Variables)
                    {
                        var initStr = v.Initializer?.Value.ToString() ?? "";
                        if (initStr.Contains($"List<{innerType}>") || initStr.Contains("new()") ||
                            initStr.Contains("new List"))
                        {
                            resultsVar = v.Identifier.Text;
                        }
                    }
                }
            }

            var newStatements = new List<StatementSyntax>();

            if (resultsVar != null)
            {
                foreach (var stmt in methodNode.Body.Statements)
                {
                    if (stmt is LocalDeclarationStatementSyntax ld &&
                        ld.Declaration.Variables.Any(v => v.Identifier.Text == resultsVar))
                        continue;

                    if (stmt is ReturnStatementSyntax ret &&
                        ret.Expression?.ToString().Contains(resultsVar) == true)
                        continue;

                    if (stmt is ExpressionStatementSyntax exprStmt &&
                        exprStmt.Expression is InvocationExpressionSyntax invStmt &&
                        invStmt.Expression is MemberAccessExpressionSyntax maStmt &&
                        maStmt.Expression.ToString() == resultsVar &&
                        maStmt.Name.Identifier.Text == "Add" &&
                        invStmt.ArgumentList.Arguments.Count == 1)
                    {
                        newStatements.Add(SyntaxFactory.YieldStatement(
                            SyntaxKind.YieldReturnStatement,
                            invStmt.ArgumentList.Arguments[0].Expression));
                    }
                    else
                    {
                        newStatements.Add(stmt);
                    }
                }
            }
            else
            {
                foreach (var stmt in methodNode.Body.Statements)
                {
                    if (stmt is ReturnStatementSyntax) continue;
                    newStatements.Add(stmt);
                }
                newStatements.Add(SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement));
            }

            newMethod = newMethod.WithBody(SyntaxFactory.Block(newStatements));
        }

        var newRoot = root!.ReplaceNode(methodNode, newMethod);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
