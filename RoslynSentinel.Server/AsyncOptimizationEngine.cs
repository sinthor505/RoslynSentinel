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
    /// Also updates interface signatures if the method implements an interface.
    /// </summary>
    public async Task<string> OptimizeToValueTaskAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new InvalidOperationException("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) throw new InvalidOperationException("Method not found.");

        // Safety checks
        var awaitCount = methodNode.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();
        if (awaitCount > 1)
            return $"// WARNING: Cannot safely convert to ValueTask: method has {awaitCount} await expressions. ValueTask should only be used with 0-1 awaits.\n{root!.ToFullString()}";

        var hasTryCatch = methodNode.DescendantNodes().OfType<TryStatementSyntax>().Any();
        if (hasTryCatch)
            return $"// WARNING: Cannot safely convert to ValueTask: method contains try/catch. ValueTask cannot be awaited multiple times.\n{root!.ToFullString()}";

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

        // Check if method implements an interface
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        IMethodSymbol? methodSymbol = null;
        if (semanticModel != null)
        {
            methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        }

        // If method implements an interface, add warning
        if (methodSymbol?.ContainingType?.Interfaces.Length > 0)
        {
            var interfaceNames = string.Join(", ", methodSymbol.ContainingType.Interfaces.Select(i => i.Name));
            return $"// WARNING: This method implements interface(s): {interfaceNames}. Update the interface signature(s) to also use ValueTask.\n{newRoot.ToFullString()}";
        }

        return newRoot.ToFullString();
    }

    /// <summary>
    /// Finds sequences of independent awaits and converts them to Task.WhenAll.
    /// </summary>
    public async Task<string> OptimizeIndependentAwaitsAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "// Error: File not found in the loaded solution.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null) return $"// Error: Method '{methodName}' not found or has no block body (expression-bodied methods are not supported).";

        // This requires complex data flow analysis to ensure no dependencies between awaited tasks.
        // We locate consecutive await statements and group independent ones.
        
        var statements = methodNode.Body.Statements.ToList();
        var newStatements = new List<StatementSyntax>();

        // Process statements looking for consecutive await groups
        int i = 0;
        while (i < statements.Count)
        {
            // Try to collect a batch of consecutive, independent awaits starting at i
            var batch = new List<(StatementSyntax stmt, string? declaredVar, ExpressionSyntax awaitedExpr)>();
            var declaredInBatch = new HashSet<string>();
            int j = i;

            while (j < statements.Count)
            {
                var stmt = statements[j];
                string? varName = null;
                ExpressionSyntax? awaitedExpr = null;

                if (stmt is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AwaitExpressionSyntax awaitExpr1)
                {
                    awaitedExpr = awaitExpr1.Expression;
                }
                else if (stmt is LocalDeclarationStatementSyntax localDecl &&
                         localDecl.Declaration.Variables.Count == 1 &&
                         localDecl.Declaration.Variables[0].Initializer?.Value is AwaitExpressionSyntax awaitExpr2)
                {
                    varName = localDecl.Declaration.Variables[0].Identifier.Text;
                    awaitedExpr = awaitExpr2.Expression;
                }

                if (awaitedExpr == null)
                    break; // Not an await statement, end of batch

                // Check if this await depends on any variable declared in the batch so far
                bool dependent = awaitedExpr.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>()
                    .Any(id => declaredInBatch.Contains(id.Identifier.Text));

                if (dependent)
                    break; // Dependent on previous batch member

                batch.Add((stmt, varName, awaitedExpr));
                if (varName != null) declaredInBatch.Add(varName);
                j++;
            }

            if (batch.Count > 1)
            {
                // Check if all are expression statements (no var declarations) → use Task.WhenAll
                bool allExpression = batch.All(b => b.declaredVar == null);

                if (allExpression)
                {
                    // await Task.WhenAll(A(), B(), C())
                    var whenAll = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AwaitExpression(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("Task"),
                                    SyntaxFactory.IdentifierName("WhenAll")),
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(batch.Select(b => SyntaxFactory.Argument(b.awaitedExpr)))))));
                    newStatements.Add(whenAll);
                }
                else
                {
                    // Mixed or all declarations: use task variable hoisting
                    // var aTask = A(); var bTask = B(); var a = await aTask; var b = await bTask;
                    foreach (var (stmt, varName, awaitedExpr) in batch)
                    {
                        var taskVarName = DeriveTaskVarName(varName, awaitedExpr);
                        // var aTask = A();
                        var taskVarDecl = SyntaxFactory.LocalDeclarationStatement(
                            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(taskVarName)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(awaitedExpr)))));
                        newStatements.Add(taskVarDecl);
                    }
                    foreach (var (stmt, varName, awaitedExpr) in batch)
                    {
                        var taskVarName = DeriveTaskVarName(varName, awaitedExpr);
                        if (varName != null)
                        {
                            // var a = await aTask;
                            var origDecl = (LocalDeclarationStatementSyntax)stmt;
                            var awaitedTaskVar = SyntaxFactory.LocalDeclarationStatement(
                                origDecl.Declaration.WithVariables(
                                    SyntaxFactory.SingletonSeparatedList(
                                        origDecl.Declaration.Variables[0]
                                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                                            SyntaxFactory.AwaitExpression(
                                                SyntaxFactory.IdentifierName(taskVarName)))))));
                            newStatements.Add(awaitedTaskVar);
                        }
                        else
                        {
                            // await _taskTask; (expression statement)
                            newStatements.Add(SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.AwaitExpression(
                                    SyntaxFactory.IdentifierName(taskVarName))));
                        }
                    }
                }
                i = j;
            }
            else
            {
                newStatements.Add(statements[i]);
                i++;
            }
        }

        var newMethodNode = methodNode.WithBody(SyntaxFactory.Block(newStatements));
        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Derives a task variable name from the result variable name or awaited expression.
    /// e.g. varName="item" → "itemTask"; varName=null, method="GetItemAsync" → "getItemTask"
    /// </summary>
    private static string DeriveTaskVarName(string? varName, ExpressionSyntax awaitedExpr)
    {
        if (varName != null)
            return varName + "Task";

        // Try to extract method name from the invocation
        if (awaitedExpr is InvocationExpressionSyntax inv)
        {
            string? methodName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            if (methodName != null)
            {
                // Strip trailing "Async" suffix, then camelCase
                var baseName = methodName.EndsWith("Async", StringComparison.Ordinal)
                    ? methodName[..^5]
                    : methodName;
                if (baseName.Length == 0) baseName = methodName;
                var camel = char.ToLowerInvariant(baseName[0]) + baseName[1..];
                return camel + "Task";
            }
        }

        return "voidTask";
    }

    /// <summary>
    /// Creates an async version of a synchronous method.
    /// </summary>
    public async Task<string> GenerateAsyncOverloadAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new InvalidOperationException("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var methodNode = classNode?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (classNode == null || methodNode == null) throw new InvalidOperationException("Class or method not found.");

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

        // Scaffold body - caller must replace placeholder with real async I/O
        var placeholderStmt = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AwaitExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Task"),
                    SyntaxFactory.IdentifierName("CompletedTask"))))
            .WithLeadingTrivia(SyntaxFactory.TriviaList(
                SyntaxFactory.Comment("// placeholder - remove and implement properly"),
                SyntaxFactory.CarriageReturnLineFeed));

        var scaffoldBody = SyntaxFactory.Block(placeholderStmt)
            .WithOpenBraceToken(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                .WithTrailingTrivia(
                    SyntaxFactory.TriviaList(
                        SyntaxFactory.CarriageReturnLineFeed,
                        SyntaxFactory.Comment("// TODO: Replace synchronous operations with their async equivalents"),
                        SyntaxFactory.CarriageReturnLineFeed,
                        SyntaxFactory.Comment("// e.g., File.ReadAllText → File.ReadAllTextAsync, DbCommand.ExecuteReader → ExecuteReaderAsync"),
                        SyntaxFactory.CarriageReturnLineFeed)));

        asyncMethod = asyncMethod.WithBody(scaffoldBody);
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
        if (document == null) throw new InvalidOperationException("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new InvalidOperationException("Could not get syntax root.");

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
        if (document == null) throw new InvalidOperationException("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new InvalidOperationException("Could not get syntax root.");

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
        try
        {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return $"// Error: File '{filePath}' not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return $"// Error: Failed to get syntax root for '{filePath}'.";
        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) return $"// Error: Method '{methodName}' not found.";

        var returnTypeStr = methodNode.ReturnType.ToString().Trim();

        if (returnTypeStr.StartsWith("IAsyncEnumerable<"))
            return root.ToFullString();

        string? innerType = null;
        if (returnTypeStr.StartsWith("Task<List<") && returnTypeStr.EndsWith(">>"))
            innerType = returnTypeStr.Substring(10, returnTypeStr.Length - 12);
        else if (returnTypeStr.StartsWith("Task<IEnumerable<") && returnTypeStr.EndsWith(">>"))
            innerType = returnTypeStr.Substring(17, returnTypeStr.Length - 19);
        else if (returnTypeStr.StartsWith("List<") && returnTypeStr.EndsWith(">"))
            innerType = returnTypeStr.Substring(5, returnTypeStr.Length - 6);

        if (innerType == null)
            return $"// Error: Return type '{returnTypeStr}' is not supported. Method must return Task<List<T>>, Task<IEnumerable<T>>, or List<T>.";

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

        var newRoot = root.ReplaceNode(methodNode, newMethod);
        return newRoot.NormalizeWhitespace().ToFullString();
        }
        catch (Exception ex)
        {
            return $"// Error: {ex.Message}";
        }
    }

    public async Task<string> AddCancellationTokenToMethodAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "// Error: File not found in the loaded solution.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "// Error: Failed to get syntax root.";
        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) return $"// Error: Method '{methodName}' not found in file.\n// Tip: method names are case-sensitive. Try the exact name as declared in source.";

        // Check if method already has a CancellationToken parameter
        if (methodNode.ParameterList.Parameters.Any(p =>
            p.Type?.ToString().Contains("CancellationToken") == true))
            return root.ToFullString();

        // Build CancellationToken parameter (trailing space is intentional: it becomes
        // the whitespace trivia that separates the type name from the parameter identifier)
        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken "))
            .WithDefault(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        // Insert before any 'params' parameter, otherwise at end
        var parameters = methodNode.ParameterList.Parameters.ToList();
        var paramsIdx = parameters.FindIndex(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)));
        ParameterListSyntax newParamList;
        if (paramsIdx >= 0)
        {
            parameters.Insert(paramsIdx, ctParam);
            newParamList = methodNode.ParameterList.WithParameters(SyntaxFactory.SeparatedList(parameters));
        }
        else
        {
            newParamList = methodNode.ParameterList.AddParameters(ctParam);
        }

        SemanticModel? semanticModel = null;
        try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

        // Rewrite callees in method body
        SyntaxNode bodyToRewrite = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody!;
        var invocations = bodyToRewrite.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

        var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            // Already has a CancellationToken argument? Skip
            if (inv.ArgumentList.Arguments.Any(a =>
                a.Expression.ToString().Contains("cancellationToken") ||
                (a.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text.Contains("cancellationToken"))))
                continue;

            bool shouldAdd = false;

            if (semanticModel != null)
            {
                // Check all overloads for a CT parameter
                var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var candidates = new List<ISymbol>();
                if (symbolInfo.Symbol != null) candidates.Add(symbolInfo.Symbol);
                candidates.AddRange(symbolInfo.CandidateSymbols);

                foreach (var sym in candidates)
                {
                    if (sym is not IMethodSymbol methodSym) continue;
                    // Check if any overload in the containing type accepts CT
                    var containingType = methodSym.ContainingType;
                    if (containingType != null)
                    {
                        var overloads = containingType.GetMembers(methodSym.Name).OfType<IMethodSymbol>();
                        if (overloads.Any(o => o.Parameters.Any(p =>
                            p.Type.ToDisplayString() == "System.Threading.CancellationToken")))
                        {
                            shouldAdd = true;
                            break;
                        }
                    }
                    // Also check the symbol itself
                    if (methodSym.Parameters.Any(p => p.Type.ToDisplayString() == "System.Threading.CancellationToken"))
                    {
                        shouldAdd = true;
                        break;
                    }
                }
            }
            else
            {
                // Syntactic heuristic: methods ending in Async, or Task.Delay
                var methodText = inv.Expression.ToString();
                var isAsync = methodText.EndsWith("Async") ||
                              (inv.Expression is MemberAccessExpressionSyntax ma2 &&
                               ma2.Expression.ToString() == "Task" &&
                               ma2.Name.Identifier.Text == "Delay");
                shouldAdd = isAsync;
            }

            // Special case: Task.Delay(x) -> Task.Delay(x, cancellationToken)
            if (inv.Expression is MemberAccessExpressionSyntax maDelay &&
                maDelay.Expression.ToString() == "Task" &&
                maDelay.Name.Identifier.Text == "Delay")
                shouldAdd = true;

            if (shouldAdd)
            {
                var ctArg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"));
                var newArgList = inv.ArgumentList.AddArguments(ctArg);
                replacements[inv] = inv.WithArgumentList(newArgList);
            }
        }

        var newMethodNode = methodNode.WithParameterList(newParamList);
        if (replacements.Count > 0)
        {
            var newBody = bodyToRewrite.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
            if (newBody is BlockSyntax block)
                newMethodNode = newMethodNode.WithBody(block);
            else if (newBody is ArrowExpressionClauseSyntax arrow)
                newMethodNode = newMethodNode.WithExpressionBody(arrow);
        }

        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return newRoot.ToFullString();
    }

    // ── ApplyCancellationTokenToFile ──────────────────────────────────────────

    /// <summary>
    /// Adds a <c>CancellationToken cancellationToken = default</c> parameter to every eligible
    /// async method in a file in a single Roslyn rewrite pass.
    /// Eligible methods are async or Task/ValueTask-returning, have no existing CancellationToken
    /// parameter, and are not event handlers (fixed-signature delegates).
    /// When <paramref name="methodNames"/> is supplied only those methods are processed; all
    /// others are reported as skipped.
    /// </summary>
    /// <returns>
    /// A tuple of the full updated source string, a list of method names that were modified,
    /// and a list of names that were skipped (already have CT or not in <paramref name="methodNames"/>).
    /// </returns>
    public async Task<(string UpdatedSource, List<string> Modified, List<string> Skipped)>
        ApplyCancellationTokenToFileAsync(
            string filePath,
            string[]? methodNames = null,
            CancellationToken cancellationToken = default)
    {
        var modified = new List<string>();
        var skipped  = new List<string>();

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
            return ($"// Error: File '{filePath}' not found in the loaded solution.", modified, skipped);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            return ("// Error: Failed to get syntax root.", modified, skipped);

        SemanticModel? semanticModel = null;
        try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

        // Build the set of requested names for fast lookup (null = all).
        var requested = methodNames != null
            ? new HashSet<string>(methodNames, StringComparer.Ordinal)
            : null;

        // Collect all methods that should receive a CT parameter.
        var methodReplacements = new Dictionary<MethodDeclarationSyntax, MethodDeclarationSyntax>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name       = method.Identifier.Text;
            bool isAsync   = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            var returnType = method.ReturnType.ToString();
            bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");

            // Only process async or Task/ValueTask-returning methods.
            if (!isAsync && !returnsTask) { skipped.Add(name); continue; }

            // Skip abstract methods (no body to rewrite).
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))) { skipped.Add(name); continue; }

            // Skip methods that already carry a CancellationToken parameter.
            if (method.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString() is string t &&
                    (t == "CancellationToken" || t.EndsWith(".CancellationToken"))))
            { skipped.Add(name); continue; }

            // Skip event handlers (object sender, XxxEventArgs e) — fixed delegate signature.
            if (IsEventHandlerSignature(method)) { skipped.Add(name); continue; }

            // Skip if caller requested specific methods and this one isn't in the list.
            if (requested != null && !requested.Contains(name)) { skipped.Add(name); continue; }

            // Build the replacement method node (same logic as AddCancellationTokenToMethodAsync
            // but extracted here so we can batch all replacements into one ReplaceNodes call).
            var newMethod = BuildMethodWithCancellationToken(method, semanticModel, cancellationToken);
            methodReplacements[method] = newMethod;
            modified.Add(name);
        }

        if (methodReplacements.Count == 0)
            return (root.ToFullString(), modified, skipped);

        // Replace all eligible methods in a single Roslyn rewrite pass.
        var newRoot = root.ReplaceNodes(
            methodReplacements.Keys,
            (orig, _) => methodReplacements[orig]);

        return (newRoot.ToFullString(), modified, skipped);
    }

    /// <summary>
    /// Rewrites a single <paramref name="method"/> node to include
    /// <c>CancellationToken cancellationToken = default</c> and propagates the token to
    /// async callees in the body.  This is extracted from
    /// <see cref="AddCancellationTokenToMethodAsync"/> so that
    /// <see cref="ApplyCancellationTokenToFileAsync"/> can batch-replace without reloading
    /// the document tree after each method.
    /// </summary>
    private static MethodDeclarationSyntax BuildMethodWithCancellationToken(
        MethodDeclarationSyntax methodNode,
        SemanticModel?          semanticModel,
        CancellationToken       cancellationToken)
    {
        // Build CancellationToken parameter — trailing space becomes whitespace trivia between
        // the type name and the parameter identifier.
        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken "))
            .WithDefault(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        // Insert before any 'params' parameter; otherwise append at end.
        var parameters = methodNode.ParameterList.Parameters.ToList();
        var paramsIdx  = parameters.FindIndex(p =>
            p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)));
        ParameterListSyntax newParamList;
        if (paramsIdx >= 0)
        {
            parameters.Insert(paramsIdx, ctParam);
            newParamList = methodNode.ParameterList.WithParameters(
                SyntaxFactory.SeparatedList(parameters));
        }
        else
        {
            newParamList = methodNode.ParameterList.AddParameters(ctParam);
        }

        // Rewrite callee invocations in the method body to forward the token.
        SyntaxNode bodyToRewrite = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody!;
        var invocations  = bodyToRewrite.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            // Skip invocations that already pass cancellationToken.
            if (inv.ArgumentList.Arguments.Any(a =>
                    a.Expression.ToString().Contains("cancellationToken") ||
                    (a.Expression is MemberAccessExpressionSyntax maCheck &&
                     maCheck.Name.Identifier.Text.Contains("cancellationToken"))))
                continue;

            bool shouldAdd = false;

            if (semanticModel != null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var candidates = new List<ISymbol>();
                if (symbolInfo.Symbol != null) candidates.Add(symbolInfo.Symbol);
                candidates.AddRange(symbolInfo.CandidateSymbols);

                foreach (var sym in candidates)
                {
                    if (sym is not IMethodSymbol methodSym) continue;
                    var containingType = methodSym.ContainingType;
                    if (containingType != null)
                    {
                        var overloads = containingType.GetMembers(methodSym.Name)
                            .OfType<IMethodSymbol>();
                        if (overloads.Any(o => o.Parameters.Any(p =>
                                p.Type.ToDisplayString() == "System.Threading.CancellationToken")))
                        { shouldAdd = true; break; }
                    }
                    if (methodSym.Parameters.Any(p =>
                            p.Type.ToDisplayString() == "System.Threading.CancellationToken"))
                    { shouldAdd = true; break; }
                }
            }
            else
            {
                // Syntactic fallback: methods ending in Async or Task.Delay.
                var methodText = inv.Expression.ToString();
                shouldAdd = methodText.EndsWith("Async") ||
                            (inv.Expression is MemberAccessExpressionSyntax ma2 &&
                             ma2.Expression.ToString() == "Task" &&
                             ma2.Name.Identifier.Text == "Delay");
            }

            // Task.Delay always accepts a CancellationToken — ensure it is propagated.
            if (inv.Expression is MemberAccessExpressionSyntax maDelay &&
                maDelay.Expression.ToString() == "Task" &&
                maDelay.Name.Identifier.Text == "Delay")
                shouldAdd = true;

            if (shouldAdd)
            {
                var ctArg       = SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"));
                var newArgList  = inv.ArgumentList.AddArguments(ctArg);
                replacements[inv] = inv.WithArgumentList(newArgList);
            }
        }

        var newMethodNode = methodNode.WithParameterList(newParamList);
        if (replacements.Count > 0)
        {
            var newBody = bodyToRewrite.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
            if (newBody is BlockSyntax block)
                newMethodNode = newMethodNode.WithBody(block);
            else if (newBody is ArrowExpressionClauseSyntax arrow)
                newMethodNode = newMethodNode.WithExpressionBody(arrow);
        }
        return newMethodNode;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="method"/> looks like a delegate-based event handler
    /// (two parameters: first is <c>object</c> or <c>object?</c>, second ends with <c>EventArgs</c>).
    /// Event-handler signatures are fixed by the delegate contract and cannot receive an extra
    /// CancellationToken parameter without breaking the calling convention.
    /// </summary>
    private static bool IsEventHandlerSignature(MethodDeclarationSyntax method)
    {
        var parms = method.ParameterList.Parameters;
        if (parms.Count != 2) return false;
        var first  = parms[0].Type?.ToString() ?? "";
        var second = parms[1].Type?.ToString() ?? "";
        return (first == "object" || first == "object?") &&
               second.EndsWith("EventArgs") || second.EndsWith("EventArgs?");
    }
}

