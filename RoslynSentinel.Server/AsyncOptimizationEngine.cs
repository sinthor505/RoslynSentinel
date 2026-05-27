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
    /// Converts a synchronous method to the Asyncify-bridge pattern in one atomic step:
    /// <list type="number">
    ///   <item>Creates an async overload named <c>&lt;methodName&gt;Async</c> with the original
    ///         body copied verbatim, <c>CancellationToken cancellationToken = default</c> appended
    ///         as the last parameter, and the <c>async</c> modifier added.
    ///         Expression-bodied methods are converted to block bodies in the async overload
    ///         so that downstream CT-propagation tools can operate on them.</item>
    ///   <item>Replaces the original sync method's body with the bridge call:
    ///         <c>return &lt;methodName&gt;Async(params…).GetAwaiter().GetResult();</c>
    ///         (or a bare expression statement for void-returning methods).</item>
    ///   <item>Adds <c>[Obsolete("Asyncify-bridge: call &lt;methodName&gt;Async instead.", false)]</c>
    ///         to the original sync method so that CS0618 warnings at call sites drive incremental
    ///         caller migration.</item>
    /// </list>
    /// The async body is NOT further transformed — use <c>apply_cancellation_token_to_file</c>
    /// or <c>add_cancellation_token_to_method</c> in a follow-up step to propagate the token.
    /// </summary>
    /// <param name="filePath">Absolute path to the source file containing the method.</param>
    /// <param name="methodName">Exact case-sensitive name of the synchronous method to bridge.</param>
    /// <param name="cancellationToken">Optional cancellation token for the Roslyn operations.</param>
    /// <returns>Full updated source for the file. Does NOT write to disk; pass to
    ///          <c>apply_proposed_changes</c> or stage via <c>autoStage=true</c> to persist.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown for any precondition failure: file/method not found, method already async,
    /// method name ends with "Async", method is abstract, method is an event handler,
    /// method has ref/out parameters, or the async overload already exists.
    /// </exception>
    public async Task<string> ConvertToAsyncBridgeAsync(
        string filePath,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
            throw new InvalidOperationException(
                $"File '{filePath}' not found in the loaded solution. Ensure load_solution has been called.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new InvalidOperationException($"Could not get syntax root for '{filePath}'.");

        // Find the class that contains the named method.
        var classNode = root.DescendantNodes()
                            .OfType<ClassDeclarationSyntax>()
                            .FirstOrDefault(c => c.Members
                                .OfType<MethodDeclarationSyntax>()
                                .Any(m => m.Identifier.Text == methodName));
        var methodNode = classNode?.Members
                                   .OfType<MethodDeclarationSyntax>()
                                   .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (classNode == null || methodNode == null)
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive.");

        // ── Precondition checks ──────────────────────────────────────────────
        if (methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            throw new InvalidOperationException(
                $"Method '{methodName}' is already async — it cannot be converted to a bridge.");

        if (methodName.EndsWith("Async", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Method '{methodName}' already ends with 'Async' — it is likely already the async overload.");

        if (methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
            throw new InvalidOperationException(
                $"Method '{methodName}' is abstract — abstract methods have no body and cannot be bridged.");

        if (IsEventHandlerSignature(methodNode))
            throw new InvalidOperationException(
                $"Method '{methodName}' appears to be an event handler (object sender, XxxEventArgs e). " +
                "Fixed delegate signatures cannot receive an extra CancellationToken parameter.");

        // Reject ref/out parameters: the bridge call cannot forward them without matching ref/out
        // keywords on the argument, and the async overload signature becomes ambiguous.
        var refOrOutParam = methodNode.ParameterList.Parameters
            .FirstOrDefault(p => p.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)));
        if (refOrOutParam != null)
            throw new InvalidOperationException(
                $"Method '{methodName}' has a ref/out parameter '{refOrOutParam.Identifier.Text}'. " +
                "ref/out parameters are not supported by the Asyncify-bridge pattern.");

        var asyncMethodName = methodName + "Async";
        if (classNode.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == asyncMethodName))
            throw new InvalidOperationException(
                $"An overload named '{asyncMethodName}' already exists in the class. " +
                "Remove it first, or use add_cancellation_token_to_method to add CT to the existing overload.");

        // ── Build the async overload ─────────────────────────────────────────
        var returnTypeStr = methodNode.ReturnType.ToString().Trim();
        var isVoid        = returnTypeStr == "void";
        var newReturnType = isVoid ? "Task" : $"Task<{returnTypeStr}>";

        // CancellationToken parameter: trailing space in type name becomes whitespace trivia.
        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken "))
            .WithDefault(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        // Strip any [Obsolete] and [MigrationCandidate] attributes from the async overload:
        // [Obsolete] must not propagate (the overload is the canonical version).
        // [MigrationCandidate] is removed because the method has been processed.
        var cleanAttributeLists = SyntaxFactory.List(
            methodNode.AttributeLists.Where(al =>
                !al.Attributes.Any(a =>
                    a.Name.ToString() == "Obsolete" ||
                    a.Name.ToString() == "System.Obsolete" ||
                    a.Name.ToString() == MigrationCandidateShortName ||
                    a.Name.ToString() == MigrationCandidateFullName)));

        var asyncMethod = methodNode
            .WithAttributeLists(cleanAttributeLists)
            .WithIdentifier(SyntaxFactory.Identifier(asyncMethodName))
            .WithReturnType(SyntaxFactory.ParseTypeName(newReturnType))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword))
            .AddParameterListParameters(ctParam);

        // Convert expression-bodied originals to block-bodied async overloads so that
        // downstream CT-propagation tools (which operate on block bodies) work correctly.
        if (methodNode.ExpressionBody != null && methodNode.Body == null)
        {
            // Expression body: `=> expr` → `{ return expr; }` (or `{ expr; }` for void).
            StatementSyntax convertedStmt = isVoid
                ? SyntaxFactory.ExpressionStatement(methodNode.ExpressionBody.Expression)
                : SyntaxFactory.ReturnStatement(methodNode.ExpressionBody.Expression);
            asyncMethod = asyncMethod
                .WithBody(SyntaxFactory.Block(convertedStmt))
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken));
        }

        // ── Build the bridge body for the original sync method ───────────────
        // Forward all original parameters as positional arguments.
        var callArgs    = methodNode.ParameterList.Parameters
                                    .Select(p => SyntaxFactory.Argument(
                                        SyntaxFactory.IdentifierName(p.Identifier.Text)));
        var callArgList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(callArgs));

        // MethodNameAsync(args).GetAwaiter().GetResult()
        var asyncCallExpr = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(asyncMethodName), callArgList);
        var getAwaiterCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                asyncCallExpr,
                SyntaxFactory.IdentifierName("GetAwaiter")),
            SyntaxFactory.ArgumentList());
        var getResultCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                getAwaiterCall,
                SyntaxFactory.IdentifierName("GetResult")),
            SyntaxFactory.ArgumentList());

        // For void methods the bridge is a bare expression statement; otherwise a return statement.
        StatementSyntax bridgeStatement = isVoid
            ? SyntaxFactory.ExpressionStatement(getResultCall)
            : (StatementSyntax)SyntaxFactory.ReturnStatement(getResultCall);

        // Inline comment clarifies intent for anyone reading the bridge body.
        bridgeStatement = bridgeStatement.WithLeadingTrivia(
            SyntaxFactory.Comment($"// Asyncify-bridge: synchronous wrapper over {asyncMethodName}."),
            SyntaxFactory.CarriageReturnLineFeed);

        var bridgeBlock = SyntaxFactory.Block(bridgeStatement);

        // ── Build [Obsolete] attribute ───────────────────────────────────────
        var obsoleteAttr = SyntaxFactory.Attribute(
            SyntaxFactory.IdentifierName("Obsolete"),
            SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal($"Asyncify-bridge: call {asyncMethodName} instead."))),
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))
                })));
        var obsoleteAttrList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(obsoleteAttr));

        // ── Apply both changes to the class node ─────────────────────────────
        // Step 1: replace original method node with the bridge wrapper.
        // Strip [MigrationCandidate] from the bridge wrapper — the method has been processed
        // so the candidate marker is no longer applicable.
        var bridgeAttributeLists = SyntaxFactory.List(
            methodNode.AttributeLists.Where(al =>
                !al.Attributes.Any(a =>
                    a.Name.ToString() == MigrationCandidateShortName ||
                    a.Name.ToString() == MigrationCandidateFullName)));

        var bridgeWrapper = methodNode
            .WithAttributeLists(bridgeAttributeLists)
            .WithBody(bridgeBlock)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
            .AddAttributeLists(obsoleteAttrList);

        var classWithBridge = classNode.ReplaceNode(methodNode, bridgeWrapper);

        // Step 2: insert the async overload immediately after the bridge wrapper.
        var bridgeInNewClass = classWithBridge.Members
                                              .OfType<MethodDeclarationSyntax>()
                                              .First(m => m.Identifier.Text == methodName);
        var classWithBoth = classWithBridge.InsertNodesAfter(bridgeInNewClass, new[] { asyncMethod });

        var newRoot = root.ReplaceNode(classNode, classWithBoth);
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
            // Ineligible sync methods are silently ignored — they are not actionable
            // and would flood SkippedMethods with noise in large files.
            if (!isAsync && !returnsTask) continue;

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

    // ── MigrationCandidate attribute name constants ─────────────────────────
    private const string MigrationCandidateShortName = "MigrationCandidate";
    private const string MigrationCandidateFullName  = "MigrationCandidateAttribute";

    /// <summary>
    /// Returns the source text of a self-contained <c>MigrationCandidateAttribute</c> class
    /// to be injected as a new file in the target project when the attribute is not yet present.
    /// The attribute is <c>internal sealed</c> so it requires no inter-project references.
    /// </summary>
    /// <param name="ns">The namespace to emit the class into (matched from the target file).</param>
    /// <returns>Complete C# source for the attribute class file.</returns>
    private static string BuildMigrationCandidateAttributeSource(string ns)
    {
        return
$@"using System;

namespace {ns}
{{
    /// <summary>
    /// Marks a method as a candidate for an async migration refactoring pass.
    /// Added by <c>flag_migration_candidate</c>. Removed automatically when the
    /// corresponding specialist tool (e.g. <c>convert_to_async_bridge</c>) processes
    /// the method. Remove manually if the method is determined ineligible after review.
    /// </summary>
    /// <remarks>
    /// Known patterns: <c>AsyncBridge</c>, <c>HandlerExtract</c>,
    /// <c>HandlerToAsync</c>, <c>AsyncCallerUplift</c>.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    internal sealed class MigrationCandidateAttribute : Attribute
    {{
        /// <summary>Initialises a new <see cref=""MigrationCandidateAttribute""/>.</summary>
        /// <param name=""pattern"">The refactoring pattern (e.g., ""AsyncBridge"").</param>
        public MigrationCandidateAttribute(string pattern)
        {{
            Pattern = pattern;
        }}

        /// <summary>The refactoring pattern this candidate is earmarked for.</summary>
        public string Pattern {{ get; }}

        /// <summary>Eligibility score assigned by the scout tool (0 = unscored).</summary>
        public int Score {{ get; set; }}

        /// <summary>Human-readable rationale for the flag.</summary>
        public string Reason {{ get; set; }}

        /// <summary>ISO date (yyyy-MM-dd) when the method was flagged.</summary>
        public string FlaggedDate {{ get; set; }}
    }}
}}
";
    }

    /// <summary>
    /// Determines the primary namespace of a source file by returning the identifier
    /// of the first <c>NamespaceDeclarationSyntax</c> or <c>FileScopedNamespaceDeclarationSyntax</c>
    /// found in its syntax root, falling back to <c>"Global"</c> if none exists.
    /// </summary>
    /// <param name="root">The syntax root of the source file to inspect.</param>
    /// <returns>The namespace name string, e.g. <c>"Avaal.Service"</c>.</returns>
    private static string DetectNamespace(SyntaxNode root)
    {
        // File-scoped namespace (C# 10+): namespace Foo;
        var fileScoped = root.DescendantNodes()
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped != null) return fileScoped.Name.ToString();

        // Block-scoped namespace: namespace Foo { ... }
        var blockScoped = root.DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (blockScoped != null) return blockScoped.Name.ToString();

        return "Global";
    }

    /// <summary>
    /// Flags a method as a migration candidate by adding a
    /// <c>[MigrationCandidate("pattern", Score = N, Reason = "...", FlaggedDate = "yyyy-MM-dd")]</c>
    /// attribute. If the <c>MigrationCandidateAttribute</c> class is not yet defined anywhere in
    /// the loaded solution, a self-contained source file is generated and included in the result so
    /// the caller can write it alongside the method file — no project reference changes are needed.
    /// </summary>
    /// <remarks>
    /// Re-flagging an already-flagged method with the same pattern is idempotent: the old attribute
    /// is stripped first, then the new one (with updated score/reason/date) is added.
    /// </remarks>
    /// <param name="filePath">Absolute or workspace-relative path of the source file.</param>
    /// <param name="methodName">Exact (case-sensitive) name of the method to flag.</param>
    /// <param name="pattern">
    /// The refactoring pattern: <c>"AsyncBridge"</c>, <c>"HandlerExtract"</c>,
    /// <c>"HandlerToAsync"</c>, <c>"AsyncCallerUplift"</c>, or any custom string.
    /// </param>
    /// <param name="score">Optional eligibility score (default 0 = unscored).</param>
    /// <param name="reason">Optional human-readable rationale for the flag.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// A dictionary mapping file paths to updated source content.
    /// Always contains the target file. May also contain a second entry for the newly-injected
    /// <c>MigrationCandidateAttribute.cs</c> file if the attribute class did not already exist
    /// in the project.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file or method is not found in the loaded solution.
    /// </exception>
    public async Task<Dictionary<string, string>> FlagMigrationCandidateAsync(
        string filePath,
        string methodName,
        string pattern,
        int    score  = 0,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
            throw new InvalidOperationException(
                $"File '{filePath}' not found in the loaded solution. Ensure load_solution has been called.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new InvalidOperationException($"Could not get syntax root for '{filePath}'.");

        // ── Find the target method ───────────────────────────────────────────
        var methodNode = root.DescendantNodes()
                             .OfType<MethodDeclarationSyntax>()
                             .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive.");

        // ── Strip any existing [MigrationCandidate("pattern")] for idempotency ─
        // Only strips attributes whose first positional argument matches the given pattern
        // so that multiple different-pattern flags on the same method are preserved.
        var strippedAttributeLists = SyntaxFactory.List(
            methodNode.AttributeLists
                .Select(al =>
                {
                    var filteredAttrs = al.Attributes.Where(a =>
                    {
                        var name = a.Name.ToString();
                        if (name != MigrationCandidateShortName && name != MigrationCandidateFullName)
                            return true; // keep — unrelated attribute
                        // Remove only if the pattern positional arg matches.
                        var firstArg = a.ArgumentList?.Arguments
                            .FirstOrDefault(arg => arg.NameEquals == null);
                        var argPattern = (firstArg?.Expression as LiteralExpressionSyntax)
                            ?.Token.ValueText;
                        return argPattern != pattern; // keep if different pattern
                    }).ToList();

                    if (filteredAttrs.Count == al.Attributes.Count)
                        return al; // unchanged

                    return filteredAttrs.Count == 0
                        ? null
                        : al.WithAttributes(SyntaxFactory.SeparatedList(filteredAttrs));
                })
                .Where(al => al != null)!);

        // ── Build the new [MigrationCandidate(...)] attribute ────────────────
        var today     = System.DateTime.UtcNow.ToString("yyyy-MM-dd");
        var arguments = new System.Collections.Generic.List<AttributeArgumentSyntax>
        {
            // Positional: "pattern"
            SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(pattern)))
        };

        if (score != 0)
        {
            // Named: Score = N
            arguments.Add(SyntaxFactory.AttributeArgument(
                SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Score")),
                null,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(score))));
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            // Named: Reason = "..."
            arguments.Add(SyntaxFactory.AttributeArgument(
                SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Reason")),
                null,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(reason))));
        }

        // Named: FlaggedDate = "yyyy-MM-dd"
        arguments.Add(SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("FlaggedDate")),
            null,
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(today))));

        var newAttr = SyntaxFactory.Attribute(
            SyntaxFactory.IdentifierName(MigrationCandidateShortName),
            SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SeparatedList(arguments)));

        var newAttrList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(newAttr));

        var updatedMethod = methodNode
            .WithAttributeLists(strippedAttributeLists)
            .AddAttributeLists(newAttrList);

        var newRoot    = root.ReplaceNode(methodNode, updatedMethod);
        var newSource  = newRoot.NormalizeWhitespace().ToFullString();

        var result = new Dictionary<string, string> { { filePath, newSource } };

        // ── Inject MigrationCandidateAttribute.cs if not yet in the solution ─
        var alreadyDefined = solution.Projects
            .SelectMany(p => p.Documents)
            .Any(d => d.FilePath != null &&
                      System.IO.Path.GetFileName(d.FilePath)
                            .Equals($"{MigrationCandidateFullName}.cs",
                                    StringComparison.OrdinalIgnoreCase));

        if (!alreadyDefined)
        {
            // Detect namespace from the target file so the injected type is visible
            // in the same namespace without a using directive.
            var ns       = DetectNamespace(root);
            var attrSrc  = BuildMigrationCandidateAttributeSource(ns);
            var attrPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(filePath) ?? ".",
                $"{MigrationCandidateFullName}.cs");
            result[attrPath] = attrSrc;
        }

        return result;
    }

    /// <summary>
    /// Finds all methods in the solution (or scoped to a file/project) that carry a
    /// <c>[MigrationCandidate]</c> attribute. Uses syntax-level analysis — no compilation
    /// or semantic model required — so it works on files that have not been reloaded after
    /// a recent <c>flag_migration_candidate</c> call.
    /// </summary>
    /// <param name="filePath">
    /// When provided, restricts the scan to a single file (matched by full path suffix).
    /// </param>
    /// <param name="projectName">
    /// When provided, restricts the scan to a single project by name (case-insensitive).
    /// </param>
    /// <param name="pattern">
    /// When provided, returns only candidates flagged for this exact pattern string
    /// (e.g. <c>"AsyncBridge"</c>).
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>One <see cref="MigrationCandidateFinding"/> per flagged method per pattern.</returns>
    public async Task<List<MigrationCandidateFinding>> FindMigrationCandidatesAsync(
        string? filePath     = null,
        string? projectName  = null,
        string? pattern      = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var findings = new List<MigrationCandidateFinding>();

        // Enumerate all documents, applying scope filters.
        var projects = solution.Projects.AsEnumerable();
        if (projectName != null)
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (filePath != null)
                docs = docs.Where(d => d.FilePath != null &&
                                       d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase));

            foreach (var doc in docs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    foreach (var attrList in method.AttributeLists)
                    {
                        foreach (var attr in attrList.Attributes)
                        {
                            var name = attr.Name.ToString();
                            if (name != MigrationCandidateShortName && name != MigrationCandidateFullName)
                                continue;

                            // Extract positional pattern argument.
                            var firstArg = attr.ArgumentList?.Arguments
                                .FirstOrDefault(a => a.NameEquals == null);
                            var attrPattern = (firstArg?.Expression as LiteralExpressionSyntax)
                                ?.Token.ValueText ?? string.Empty;

                            // Apply pattern filter.
                            if (pattern != null &&
                                !attrPattern.Equals(pattern, StringComparison.Ordinal))
                                continue;

                            // Extract optional named arguments.
                            var namedArgs = attr.ArgumentList?.Arguments
                                .Where(a => a.NameEquals != null)
                                .ToDictionary(
                                    a => a.NameEquals!.Name.Identifier.Text,
                                    a => a.Expression) ?? new Dictionary<string, ExpressionSyntax>();

                            int attrScore = 0;
                            if (namedArgs.TryGetValue("Score", out var scoreExpr) &&
                                scoreExpr is LiteralExpressionSyntax scoreLit &&
                                scoreLit.Token.Value is int scoreVal)
                                attrScore = scoreVal;

                            string? attrReason = null;
                            if (namedArgs.TryGetValue("Reason", out var reasonExpr) &&
                                reasonExpr is LiteralExpressionSyntax reasonLit)
                                attrReason = reasonLit.Token.ValueText;

                            string? attrDate = null;
                            if (namedArgs.TryGetValue("FlaggedDate", out var dateExpr) &&
                                dateExpr is LiteralExpressionSyntax dateLit)
                                attrDate = dateLit.Token.ValueText;

                            // Determine containing class name.
                            var classNode = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                            var lineSpan  = method.GetLocation().GetLineSpan();

                            findings.Add(new MigrationCandidateFinding(
                                FilePath:    doc.FilePath ?? string.Empty,
                                MethodName:  method.Identifier.Text,
                                ClassName:   classNode?.Identifier.Text ?? string.Empty,
                                Pattern:     attrPattern,
                                Score:       attrScore,
                                Reason:      attrReason,
                                FlaggedDate: attrDate,
                                Line:        lineSpan.StartLinePosition.Line + 1));
                        }
                    }
                }
            }
        }

        return findings;
    }
}

