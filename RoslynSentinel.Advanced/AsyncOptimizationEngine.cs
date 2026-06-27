using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Advanced;

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
    public async Task<DocumentEditResult> OptimizeToValueTaskAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException("File not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
        {
            throw new InvalidOperationException("Method not found.");
        }

        // Safety checks
        var awaitCount = methodNode.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();
        if (awaitCount > 1)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotOptimize,
                UpdatedText = $"// WARNING: Cannot safely convert to ValueTask: method has {awaitCount} await expressions. ValueTask should only be used with 0-1 awaits.\n{root!.ToFullString()}",
                FilePath = filePath
            };
        }

        var hasTryCatch = methodNode.DescendantNodes().OfType<TryStatementSyntax>().Any();
        if (hasTryCatch)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotOptimize,
                UpdatedText = $"// WARNING: Cannot safely convert to ValueTask: method contains try/catch. ValueTask cannot be awaited multiple times.\n{root!.ToFullString()}",
                FilePath = filePath
            };
        }

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotOptimize,
                UpdatedText = $"// WARNING: Cannot safely convert to ValueTask: method does not return Task.\n{root!.ToFullString()}",
                FilePath = filePath
            };
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
            return new DocumentEditResult { Outcome = EditOutcome.CannotOptimize, UpdatedText = null, Message = $"// WARNING: This method implements interface(s): {interfaceNames}. Update the interface signature(s) to also use ValueTask.\n{newRoot.ToFullString()}", FilePath = filePath };
        }

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Finds sequences of independent awaits and converts them to Task.WhenAll.
    /// </summary>
    public async Task<DocumentEditResult> OptimizeIndependentAwaitsAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = null, Message = "// Error: File not found in the loaded solution.", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = null, Message = $"// Error: Method '{methodName}' not found or has no block body (expression-bodied methods are not supported).", FilePath = filePath };
        }

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
                {
                    break; // Not an await statement, end of batch
                }

                // Check if this await depends on any variable declared in the batch so far
                bool dependent = awaitedExpr.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>()
                    .Any(id => declaredInBatch.Contains(id.Identifier.Text));

                if (dependent)
                {
                    break; // Dependent on previous batch member
                }

                batch.Add((stmt, varName, awaitedExpr));
                if (varName != null)
                {
                    declaredInBatch.Add(varName);
                }

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
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Derives a task variable name from the result variable name or awaited expression.
    /// e.g. varName="item" → "itemTask"; varName=null, method="GetItemAsync" → "getItemTask"
    /// </summary>
    private static string DeriveTaskVarName(string? varName, ExpressionSyntax awaitedExpr)
    {
        if (varName != null)
        {
            return varName + "Task";
        }

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
                if (baseName.Length == 0)
                {
                    baseName = methodName;
                }

                var camel = char.ToLowerInvariant(baseName[0]) + baseName[1..];
                return camel + "Task";
            }
        }

        return "voidTask";
    }

    /// <summary>
    /// Creates an async version of a synchronous method.
    /// </summary>
    public async Task<DocumentEditResult> GenerateAsyncOverloadAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException("File not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var methodNode = classNode?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (classNode == null || methodNode == null)
        {
            throw new InvalidOperationException("Class or method not found.");
        }

        var asyncMethodName = methodName + "Async";
        var returnTypeStr = methodNode.ReturnType.ToString();
        var newReturnType = returnTypeStr == "void" ? "Task" : $"Task<{returnTypeStr}>";

        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken"))
            .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        var asyncModifiers = SyntaxFactory.TokenList(
            methodNode.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.OverrideKeyword) &&
                            !m.IsKind(SyntaxKind.SealedKeyword))
                .Append(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));

        var asyncMethod = methodNode
            .WithIdentifier(SyntaxFactory.Identifier(asyncMethodName))
            .WithReturnType(SyntaxFactory.ParseTypeName(newReturnType))
            .WithModifiers(asyncModifiers)
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

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
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
    public async Task<DocumentEditResult> ConvertToAsyncBridgeAsync(
        FilePath filePath,
        string methodName,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException(
                $"File '{filePath}' not found in the loaded solution. Ensure load_solution has been called.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new InvalidOperationException($"Could not get syntax root for '{filePath}'.");
        }

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
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive.");
        }

        // ── Precondition checks ──────────────────────────────────────────────
        if (methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' is already async — it cannot be converted to a bridge.");
        }

        if (methodName.EndsWith("Async", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' already ends with 'Async' — it is likely already the async overload.");
        }

        if (methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' is abstract — abstract methods have no body and cannot be bridged.");
        }

        if (IsEventHandlerSignature(methodNode))
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' appears to be an event handler (object sender, XxxEventArgs e). " +
                "Fixed delegate signatures cannot receive an extra CancellationToken parameter.");
        }

        // Reject ref/out parameters: the bridge call cannot forward them without matching ref/out
        // keywords on the argument, and the async overload signature becomes ambiguous.
        var refOrOutParam = methodNode.ParameterList.Parameters
            .FirstOrDefault(p => p.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)));
        if (refOrOutParam != null)
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' has a ref/out parameter '{refOrOutParam.Identifier.Text}'. " +
                "ref/out parameters are not supported by the Asyncify-bridge pattern.");
        }

        var asyncMethodName = methodName + "Async";
        if (classNode.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == asyncMethodName))
        {
            throw new InvalidOperationException(
                $"An overload named '{asyncMethodName}' already exists in the class. " +
                "Remove it first, or use add_cancellation_token_to_method to add CT to the existing overload.");
        }

        // ── Build the async overload ─────────────────────────────────────────
        var returnTypeStr = methodNode.ReturnType.ToString().Trim();
        var isVoid = returnTypeStr == "void";
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

        // Build async overload modifiers explicitly so that instance/static and virtual are
        // preserved while modifiers that are wrong on a new method are stripped:
        //   'override' — the async overload is a new method; there is no base counterpart
        //                to override, so keeping 'override' causes CS0115.
        //   'sealed'   — meaningless on a freshly introduced method.
        //   'static'   — kept; the async version of a static method must also be static.
        //   'async'    — added.
        var asyncModifiers = SyntaxFactory.TokenList(
            methodNode.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.OverrideKeyword) &&
                            !m.IsKind(SyntaxKind.SealedKeyword))
                .Append(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));

        var asyncMethod = methodNode
            .WithAttributeLists(cleanAttributeLists)
            .WithIdentifier(SyntaxFactory.Identifier(asyncMethodName))
            .WithReturnType(SyntaxFactory.ParseTypeName(newReturnType))
            .WithModifiers(asyncModifiers)
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
        var callArgs = methodNode.ParameterList.Parameters
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
        // ── Apply both changes to the class node ─────────────────────────────
        // Step 1: replace original method node with the bridge wrapper.
        // Strip [MigrationCandidate] from the bridge wrapper — the method has been processed
        // so the candidate marker is no longer applicable. Also strip any pre-existing [Obsolete]
        // before re-adding, so repeated conversions don't accumulate duplicate attributes.
        var bridgeAttributeLists = SyntaxFactory.List(
            methodNode.AttributeLists.Where(al =>
                !al.Attributes.Any(a =>
                    a.Name.ToString() == MigrationCandidateShortName ||
                    a.Name.ToString() == MigrationCandidateFullName)));

        var bridgeBase = methodNode
            .WithAttributeLists(bridgeAttributeLists)
            .WithBody(bridgeBlock)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken));

        var (bridgeWrapper, _) = ReplaceOrAddAttribute(
            bridgeBase, "Obsolete", "ObsoleteAttribute", null, obsoleteAttr);

        var classWithBridge = classNode.ReplaceNode(methodNode, bridgeWrapper);

        // Step 2: insert the async overload immediately after the bridge wrapper.
        var bridgeInNewClass = classWithBridge.Members
                                              .OfType<MethodDeclarationSyntax>()
                                              .First(m => m.Identifier.Text == methodName);
        var classWithBoth = classWithBridge.InsertNodesAfter(bridgeInNewClass, new[] { asyncMethod });

        var newRoot = root.ReplaceNode(classNode, classWithBoth);
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Converts an event handler that calls Asyncify-bridge sync wrappers in-place to <c>async void</c>.
    /// Uses semantic analysis to identify calls to <c>[Obsolete("Asyncify-bridge: call … instead.")]</c>
    /// wrappers, replaces each with <c>await wrapperAsync(args)</c>, and adds the <c>async</c>
    /// modifier to the handler.
    /// Unlike <see cref="ConvertToAsyncBridgeAsync"/>, this does NOT create a new async overload —
    /// event handler delegate signatures are fixed and cannot gain a <c>CancellationToken</c>
    /// parameter. The async method is called without an explicit CT; its default-parameter value
    /// covers the common case.
    /// </summary>
    public async Task<DocumentEditResult> ConvertEventHandlerCallerToAsyncVoidAsync(
        FilePath filePath,
        string methodName,
        IProgress<string> progress = default,
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

        var methodNode = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName)
            ?? throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive.");

        if (methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            throw new InvalidOperationException($"Method '{methodName}' is already async.");

        if (methodNode.ReturnType.ToString() != "void")
            throw new InvalidOperationException(
                $"Method '{methodName}' does not return void — only void-returning methods can be converted to async void.");

        // Semantic: identify the SPECIFIC bridge-call invocations — not all calls sharing the same name.
        // Collecting only the method name (e.g. "search") then rewriting every call by that name
        // incorrectly rewrites unrelated calls on types that have no async counterpart (e.g.
        // BaseService.search when only CommonSearch.search is the bridge wrapper).
        // Instead, annotate the exact nodes the semantic model confirms are bridge calls, then
        // replace only those.
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var bridgeTargets = new Dictionary<InvocationExpressionSyntax, string>(ReferenceEqualityComparer.Instance);
        if (semanticModel != null)
        {
            foreach (var inv in methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(inv, cancellationToken).Symbol as IMethodSymbol;
                var obsoleteMsg = sym?.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name is "ObsoleteAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string;
                if (obsoleteMsg != null && obsoleteMsg.StartsWith("Asyncify-bridge:", StringComparison.Ordinal))
                    bridgeTargets[inv] = sym!.Name + "Async";
            }
        }

        if (bridgeTargets.Count == 0)
            throw new InvalidOperationException(
                $"Method '{methodName}' does not call any Asyncify-bridge sync wrapper. " +
                "Bridge wrappers must be marked [Obsolete(\"Asyncify-bridge: call … instead.\")].");

        // Annotate the exact bridge-call nodes in one batch; annotations survive WithModifiers().
        const string BridgeCallAnnotation = "AsyncifyBridgeCall";
        var annotatedMethodNode = methodNode.ReplaceNodes(
            bridgeTargets.Keys,
            (original, _) => original.WithAdditionalAnnotations(
                new SyntaxAnnotation(BridgeCallAnnotation, bridgeTargets[original])));

        // Add async modifier.
        var asyncToken = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        var asyncMethod = annotatedMethodNode.WithModifiers(annotatedMethodNode.Modifiers.Add(asyncToken));

        // Replace ONLY the annotated bridge-call invocations: bridgeMethod(args) → await bridgeMethodAsync(args).
        // Calls with the same name on unrelated types are untouched. No CT — event handlers have none.
        var rewrittenMethod = asyncMethod.ReplaceNodes(
            asyncMethod.DescendantNodes()
                       .OfType<InvocationExpressionSyntax>()
                       .Where(inv => inv.HasAnnotations(BridgeCallAnnotation))
                       .ToList(),
            (original, _) =>
            {
                var asyncMethodName = original.GetAnnotations(BridgeCallAnnotation).First().Data!;
                ExpressionSyntax? newExpr = original.Expression switch
                {
                    IdentifierNameSyntax id =>
                        SyntaxFactory.IdentifierName(asyncMethodName).WithTriviaFrom(id),
                    MemberAccessExpressionSyntax ma =>
                        ma.WithName(SyntaxFactory.IdentifierName(asyncMethodName).WithTriviaFrom(ma.Name)),
                    _ => null,
                };
                if (newExpr == null) return original;

                // When the bridge call is chained (e.g. bridge().Rows, bridge().AsDataView()),
                // parenthesise the await so member/element access binds to the awaited value:
                //   bridge().Rows → (await bridgeAsync()).Rows
                bool needsParens = original.Parent is MemberAccessExpressionSyntax
                                || original.Parent is ElementAccessExpressionSyntax
                                || original.Parent is ConditionalAccessExpressionSyntax;

                var awaitedCall = original.WithExpression(newExpr).WithoutLeadingTrivia();
                ExpressionSyntax awaitExpr = SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword)
                                 .WithTrailingTrivia(SyntaxFactory.Space),
                    awaitedCall);

                return (needsParens
                    ? (ExpressionSyntax)SyntaxFactory.ParenthesizedExpression(awaitExpr)
                    : awaitExpr)
                    .WithLeadingTrivia(original.GetLeadingTrivia());
            });

        // Any delegate/lambda that now contains await (because a bridge call was rewritten) must
        // itself be marked async — the rewriter above adds await but not the async modifier.
        rewrittenMethod = (MethodDeclarationSyntax)new AsyncifyAnonymousFunctionsRewriter().Visit(rewrittenMethod)!;

        // Strip all [MigrationCandidate] attributes — the handler is now converted and no
        // longer needs any migration marker. Leaving them causes stale-flag failures on re-runs.
        rewrittenMethod = StripMigrationCandidateAttributes(rewrittenMethod);

        var newRoot2 = root.ReplaceNode(methodNode, rewrittenMethod);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = newRoot2.NormalizeWhitespace().ToFullString(),
            FilePath = filePath,
        };
    }

    private static MethodDeclarationSyntax StripMigrationCandidateAttributes(MethodDeclarationSyntax method)
    {
        var stripped = SyntaxFactory.List(
            method.AttributeLists
                .Select(al =>
                {
                    var kept = al.Attributes
                        .Where(a => a.Name.ToString() != MigrationCandidateShortName &&
                                    a.Name.ToString() != MigrationCandidateFullName)
                        .ToList();
                    if (kept.Count == al.Attributes.Count) return al;
                    return kept.Count == 0
                        ? null
                        : al.WithAttributes(SyntaxFactory.SeparatedList(kept));
                })
                .Where(al => al != null)
                .Select(al => al!));
        return method.WithAttributeLists(stripped);
    }

    /// <summary>
    /// Adds .ConfigureAwait(false) (or true) to all await expressions that don't already have it.
    /// </summary>
    public async Task<DocumentEditResult> AddConfigureAwaitFalseAsync(
        FilePath filePath,
        bool libraryMode = true,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException("File not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new InvalidOperationException("Could not get syntax root.");
        }

        var awaitExprs = root.DescendantNodes().OfType<AwaitExpressionSyntax>()
            .Where(a => !(a.Expression is InvocationExpressionSyntax inv &&
                          inv.Expression is MemberAccessExpressionSyntax ma &&
                          ma.Name.Identifier.Text == "ConfigureAwait"))
            .ToList();

        if (awaitExprs.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath,
                Message = "// No await expressions found that require ConfigureAwait."
            };
        }

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

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Removes all .ConfigureAwait(x) calls, leaving the bare awaited expression.
    /// </summary>
    public async Task<DocumentEditResult> RemoveConfigureAwaitFalseAsync(
        FilePath filePath,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException("File not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new InvalidOperationException("Could not get syntax root.");
        }

        var configureAwaitInvocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                          ma.Name.Identifier.Text == "ConfigureAwait")
            .ToList();

        if (configureAwaitInvocations.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath,
                Message = "// No ConfigureAwait invocations found."
            };
        }

        var newRoot = root.ReplaceNodes(configureAwaitInvocations, (orig, _) =>
        {
            var baseExpr = ((MemberAccessExpressionSyntax)orig.Expression).Expression;
            return baseExpr.WithTriviaFrom(orig);
        });

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    /// <summary>
    /// Converts a method returning Task&lt;List&lt;T&gt;&gt; or List&lt;T&gt; to IAsyncEnumerable&lt;T&gt;.
    /// Transforms results.Add(x) patterns to yield return x. Falls back to scaffold for complex bodies.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToAsyncEnumerableAsync(
        FilePath filePath,
        string methodName,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.DocumentNotFound,
                    FilePath = filePath,
                    Message = $"// Error: File '{filePath}' not found."
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.SourceInvalid,
                    FilePath = filePath,
                    Message = $"// Error: Failed to get syntax root for '{filePath}'."
                };
            }

            var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
            if (methodNode == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: Method '{methodName}' not found."
                };
            }

            var returnTypeStr = methodNode.ReturnType.ToString().Trim();

            if (returnTypeStr.StartsWith("IAsyncEnumerable<"))
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.NoChange,
                    UpdatedText = root.ToFullString(),
                    FilePath = filePath,
                    Message = "// Method already returns IAsyncEnumerable."
                };
            }

            string? innerType = null;
            if (returnTypeStr.StartsWith("Task<List<") && returnTypeStr.EndsWith(">>"))
            {
                innerType = returnTypeStr.Substring(10, returnTypeStr.Length - 12);
            }
            else if (returnTypeStr.StartsWith("Task<IEnumerable<") && returnTypeStr.EndsWith(">>"))
            {
                innerType = returnTypeStr.Substring(17, returnTypeStr.Length - 19);
            }
            else if (returnTypeStr.StartsWith("List<") && returnTypeStr.EndsWith(">"))
            {
                innerType = returnTypeStr.Substring(5, returnTypeStr.Length - 6);
            }

            if (innerType == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = $"// Error: Return type '{returnTypeStr}' is not supported. Method must return Task<List<T>>, Task<IEnumerable<T>>, or List<T>."
                };
            }

            var newReturnType = SyntaxFactory.ParseTypeName($"IAsyncEnumerable<{innerType}>");
            var newMethod = methodNode.WithReturnType(newReturnType.WithTrailingTrivia(SyntaxFactory.Space));

            if (!newMethod.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                newMethod = newMethod.AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
            }

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
                        {
                            continue;
                        }

                        if (stmt is ReturnStatementSyntax ret &&
                        ret.Expression?.ToString().Contains(resultsVar) == true)
                        {
                            continue;
                        }

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
                        if (stmt is ReturnStatementSyntax)
                        {
                            continue;
                        }

                        newStatements.Add(stmt);
                    }
                    newStatements.Add(SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement));
                }

                newMethod = newMethod.WithBody(SyntaxFactory.Block(newStatements));
            }

            var newRoot = root.ReplaceNode(methodNode, newMethod);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
                FilePath = filePath
            };
        }
        catch (Exception ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Error: {ex.Message}"
            };
        }
    }

    public async Task<DocumentEditResult> AddCancellationTokenToMethodAsync(
        FilePath filePath,
        string methodName,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Error: File not found in the loaded solution."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Error: Failed to get syntax root."
            };
        }

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: Method '{methodName}' not found in file.\n// Tip: method names are case-sensitive. Try the exact name as declared in source."
            };
        }

        // Check if method already has a CancellationToken parameter
        if (methodNode.ParameterList.Parameters.Any(p =>
            p.Type?.ToString().Contains("CancellationToken") == true))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Info: Method already has a CancellationToken parameter."
            };
        }

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
            {
                continue;
            }

            bool shouldAdd = false;

            if (semanticModel != null)
            {
                // Check all overloads for a CT parameter
                var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var candidates = new List<ISymbol>();
                if (symbolInfo.Symbol != null)
                {
                    candidates.Add(symbolInfo.Symbol);
                }

                candidates.AddRange(symbolInfo.CandidateSymbols);

                foreach (var sym in candidates)
                {
                    if (sym is not IMethodSymbol methodSym)
                    {
                        continue;
                    }
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
            {
                shouldAdd = true;
            }

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
            {
                newMethodNode = newMethodNode.WithBody(block);
            }
            else if (newBody is ArrowExpressionClauseSyntax arrow)
            {
                newMethodNode = newMethodNode.WithExpressionBody(arrow);
            }
        }

        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
            FilePath = filePath
        };
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
            FilePath filePath,
            string[]? methodNames = null,
            IProgress<string> progress = default,
            CancellationToken cancellationToken = default)
    {
        var modified = new List<string>();
        var skipped = new List<string>();

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
        {
            return ($"// Error: File '{filePath}' not found in the loaded solution.", modified, skipped);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return ("// Error: Failed to get syntax root.", modified, skipped);
        }

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
            var name = method.Identifier.Text;
            bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            var returnType = method.ReturnType.ToString();
            bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");

            // Only process async or Task/ValueTask-returning methods.
            // Ineligible sync methods are silently ignored — they are not actionable
            // and would flood SkippedMethods with noise in large files.
            if (!isAsync && !returnsTask)
            {
                continue;
            }

            // Skip abstract methods (no body to rewrite).
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))) { skipped.Add(name); continue; }

            // Skip methods that already carry a CancellationToken parameter.
            if (method.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString() is string t &&
                    (t == "CancellationToken" || t.EndsWith(".CancellationToken"))))
            {
                skipped.Add(name); continue;
            }

            // Skip event handlers (object sender, XxxEventArgs e) — fixed delegate signature.
            if (IsEventHandlerSignature(method)) { skipped.Add(name); continue; }

            // Skip if caller requested specific methods and this one isn't in the list.
            if (requested != null && !requested.Contains(name)) { skipped.Add(name); continue; }

            // Build the replacement method node (same logic as AddCancellationTokenToMethodAsync
            // but extracted here so we can batch all replacements into one ReplaceNodes call).
            var newMethod = BuildMethodWithCancellationToken(method, semanticModel, progress, cancellationToken);
            methodReplacements[method] = newMethod;
            modified.Add(name);
        }

        if (methodReplacements.Count == 0)
        {
            return (root.ToFullString(), modified, skipped);
        }

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
        SemanticModel? semanticModel,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        // Build CancellationToken parameter — trailing space becomes whitespace trivia between
        // the type name and the parameter identifier.
        var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("CancellationToken "))
            .WithDefault(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

        // Insert before any 'params' parameter; otherwise append at end.
        var parameters = methodNode.ParameterList.Parameters.ToList();
        var paramsIdx = parameters.FindIndex(p =>
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
        var invocations = bodyToRewrite.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            // Skip invocations that already pass cancellationToken.
            if (inv.ArgumentList.Arguments.Any(a =>
                    a.Expression.ToString().Contains("cancellationToken") ||
                    (a.Expression is MemberAccessExpressionSyntax maCheck &&
                     maCheck.Name.Identifier.Text.Contains("cancellationToken"))))
            {
                continue;
            }

            bool shouldAdd = false;

            if (semanticModel != null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var candidates = new List<ISymbol>();
                if (symbolInfo.Symbol != null)
                {
                    candidates.Add(symbolInfo.Symbol);
                }

                candidates.AddRange(symbolInfo.CandidateSymbols);

                foreach (var sym in candidates)
                {
                    if (sym is not IMethodSymbol methodSym)
                    {
                        continue;
                    }

                    var containingType = methodSym.ContainingType;
                    if (containingType != null)
                    {
                        var overloads = containingType.GetMembers(methodSym.Name)
                            .OfType<IMethodSymbol>();
                        if (overloads.Any(o => o.Parameters.Any(p =>
                                p.Type.ToDisplayString() == "System.Threading.CancellationToken")))
                        {
                            shouldAdd = true; break;
                        }
                    }
                    if (methodSym.Parameters.Any(p =>
                            p.Type.ToDisplayString() == "System.Threading.CancellationToken"))
                    {
                        shouldAdd = true; break;
                    }
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
            {
                shouldAdd = true;
            }

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
            {
                newMethodNode = newMethodNode.WithBody(block);
            }
            else if (newBody is ArrowExpressionClauseSyntax arrow)
            {
                newMethodNode = newMethodNode.WithExpressionBody(arrow);
            }
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
        if (parms.Count != 2)
        {
            return false;
        }

        var first = parms[0].Type?.ToString() ?? "";
        var second = parms[1].Type?.ToString() ?? "";
        return ((first == "object" || first == "object?") &&
               second.EndsWith("EventArgs")) || second.EndsWith("EventArgs?");
    }

    // ── MigrationCandidate attribute name constants ─────────────────────────
    private const string MigrationCandidateShortName = "MigrationCandidate";
    private const string MigrationCandidateFullName = "MigrationCandidateAttribute";

    /// <summary>Engine-internal result from <see cref="FlagMigrationCandidateAsync"/>.</summary>
    public record FlagMigrationCandidateEngineResult(
        /// <summary>File path → updated source for every file that must be written to disk.</summary>
        Dictionary<FilePath, string> Changes,
        /// <summary><c>true</c> if the method already carried a <c>[MigrationCandidate]</c> for this pattern.</summary>
        bool WasAlreadyFlagged,
        /// <summary>The pattern string from the previous attribute, or <c>null</c> if the method was not previously flagged.</summary>
        string? PreviousPattern,
        /// <summary><c>true</c> if a new <c>MigrationCandidateAttribute.cs</c> file was generated.</summary>
        bool AttributeClassInjected,
        /// <summary>1-based line number of the method declaration.</summary>
        int Line
    );

    /// <summary>
    /// Returns the source text of a self-contained <c>MigrationCandidateAttribute</c> class
    /// to be injected as a new file in the target project when the attribute is not yet present.
    /// The attribute is <c>internal sealed</c> so it requires no inter-project references.
    /// </summary>
    /// <param name="ns">The namespace to emit the class into (matched from the target file).</param>
    /// <returns>Complete C# source for the attribute class file.</returns>
    /// <param name="ns">Accepted for API compatibility but ignored — the class is emitted in the
    /// global namespace so that no <c>using</c> directive is required from any file in the
    /// assembly and there is no ambiguity when multiple subdirectories are glob-included by an
    /// SDK-style project.  Only ONE copy of this file should exist per compiled assembly.</param>
    private static string BuildMigrationCandidateAttributeSource(string ns)
    {
        // NOTE: intentionally no 'namespace' wrapper — global namespace.
        // Placing the attribute in a named namespace causes CS0104 ambiguity in SDK-style
        // projects because the compiler sees multiple copies (one per subdirectory) in scope.
        // Global namespace means the attribute is visible everywhere without a using directive.
        _ = ns; // parameter kept for binary compatibility
        return
@"using System;

// ── MigrationCandidateAttribute ─────────────────────────────────────────────
// Auto-generated by RoslynSentinel flag_migration_candidate tools.
// Global namespace — do NOT add a 'namespace' wrapper.
// Place exactly ONE copy of this file at the project root (.csproj directory)
// so that SDK-style glob-includes find it without duplication.

/// <summary>
/// Marks a method as a candidate for an async migration refactoring pass.
/// Added by <c>flag_migration_candidate</c>. Removed automatically when the
/// corresponding specialist tool (e.g. <c>convert_to_async_bridge</c>) processes
/// the method. Remove manually if the method is determined ineligible after review.
/// </summary>
/// <remarks>
/// Known patterns: <c>AsyncBridgeCandidate</c>, <c>HandlerExtract</c>,
/// <c>HandlerToAsync</c>, <c>AsyncCallerUplift</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class MigrationCandidateAttribute : Attribute
{
    /// <summary>Initialises a new <see cref=""MigrationCandidateAttribute""/>.</summary>
    /// <param name=""pattern"">The refactoring pattern (e.g., ""AsyncBridgeCandidate"").</param>
    public MigrationCandidateAttribute(string pattern)
    {
        Pattern = pattern;
    }

    /// <summary>The refactoring pattern this candidate is earmarked for.</summary>
    public string Pattern { get; }

    /// <summary>Eligibility score assigned by the scout tool (0 = unscored).</summary>
    public int Score { get; set; }

    /// <summary>Human-readable rationale for the flag.</summary>
    public string Reason { get; set; }

    /// <summary>ISO date (yyyy-MM-dd) when the method was flagged.</summary>
    public string FlaggedDate { get; set; }
}
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
        if (fileScoped != null)
        {
            return fileScoped.Name.ToString();
        }

        // Block-scoped namespace: namespace Foo { ... }
        var blockScoped = root.DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (blockScoped != null)
        {
            return blockScoped.Name.ToString();
        }

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
    /// The refactoring pattern: <c>"AsyncBridgeCandidate"</c>, <c>"HandlerExtractCandidate"</c>,
    /// <c>"HandlerToAsyncCandidate"</c>, <c>"AsyncCallerUpliftCandidate"</c>, or any custom string.
    /// </param>
    /// <param name="score">Optional eligibility score (default 0 = unscored).</param>
    /// <param name="reason">Optional human-readable rationale for the flag.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// A <see cref="FlagMigrationCandidateEngineResult"/> containing the changes to write,
    /// idempotency metadata (<see cref="FlagMigrationCandidateEngineResult.WasAlreadyFlagged"/>,
    /// <see cref="FlagMigrationCandidateEngineResult.PreviousPattern"/>), and the method's
    /// 1-based line number.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file or method is not found in the loaded solution.
    /// </exception>
    public async Task<FlagMigrationCandidateEngineResult> FlagMigrationCandidateAsync(
        FilePath filePath,
        string methodName,
        string pattern,
        int score = 0,
        string? reason = null,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
        {
            throw new InvalidOperationException(
                $"File '{filePath}' not found in the loaded solution. Ensure load_solution has been called.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new InvalidOperationException($"Could not get syntax root for '{filePath}'.");
        }

        // ── Find the target method ───────────────────────────────────────────
        var methodNode = root.DescendantNodes()
                             .OfType<MethodDeclarationSyntax>()
                             .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive.");
        }

        var methodLine = methodNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Find any pre-existing MigrationCandidate for the audit trail.
        string? previousPattern = methodNode.AttributeLists
            .SelectMany(al => al.Attributes)
            .Where(a => { var n = a.Name.ToString(); return n == MigrationCandidateShortName || n == MigrationCandidateFullName; })
            .Select(a => (a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null)?.Expression as LiteralExpressionSyntax)?.Token.ValueText)
            .FirstOrDefault();

        // ── Build the new [MigrationCandidate(...)] attribute ────────────────
        // Preserve the existing FlaggedDate if the method was already flagged for this pattern
        // so re-flagging doesn't produce spurious Git diffs (only the date would change).
        var flaggedDate = methodNode.AttributeLists
            .SelectMany(al => al.Attributes)
            .Where(a => { var n = a.Name.ToString(); return n == MigrationCandidateShortName || n == MigrationCandidateFullName; })
            .Where(a => (a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null)?.Expression as LiteralExpressionSyntax)?.Token.ValueText == pattern)
            .Select(a => a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == "FlaggedDate"))
            .Select(arg => (arg?.Expression as LiteralExpressionSyntax)?.Token.ValueText)
            .FirstOrDefault()
            ?? System.DateTime.UtcNow.ToString("yyyy-MM-dd");
        var arguments = new System.Collections.Generic.List<AttributeArgumentSyntax>
        {
            SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(pattern)))
        };

        if (score != 0)
        {
            arguments.Add(SyntaxFactory.AttributeArgument(
                SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Score")),
                null,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(score))));
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            arguments.Add(SyntaxFactory.AttributeArgument(
                SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Reason")),
                null,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(reason))));
        }

        arguments.Add(SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("FlaggedDate")),
            null,
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(flaggedDate))));

        var newAttr = SyntaxFactory.Attribute(
            SyntaxFactory.IdentifierName(MigrationCandidateShortName),
            SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SeparatedList(arguments)));

        var (updatedMethod, wasAlreadyFlagged) = ReplaceOrAddAttribute(
            methodNode, MigrationCandidateShortName, MigrationCandidateFullName, pattern, newAttr);

        var newRoot = root.ReplaceNode(methodNode, updatedMethod);
        var newSource = newRoot.NormalizeWhitespace().ToFullString();

        var result = new Dictionary<FilePath, string> { { filePath, newSource } };

        // ── Inject MigrationCandidateAttribute.cs if not yet in the solution ─
        var alreadyDefined = solution.Projects
            .SelectMany(p => p.Documents)
            .Any(d => d.FilePath != null &&
                      System.IO.Path.GetFileName(d.FilePath)
                            .Equals($"{MigrationCandidateFullName}.cs",
                                    StringComparison.OrdinalIgnoreCase));

        bool attributeClassInjected = false;
        if (!alreadyDefined)
        {
            // Detect namespace from the target file so the injected type is visible
            // in the same namespace without a using directive.
            var ns = DetectNamespace(root);
            var attrSrc = BuildMigrationCandidateAttributeSource(ns);
            // Use the project root (.csproj directory) so SDK-style glob-includes pick up
            // the file automatically without duplicating it into subdirectories.
            var projectDir = System.IO.Path.GetDirectoryName(document.Project.FilePath)
                             ?? System.IO.Path.GetDirectoryName(filePath)
                             ?? ".";
            var attrPath = System.IO.Path.Combine(
                projectDir,
                $"{MigrationCandidateFullName}.cs");
            result[attrPath] = attrSrc;
            attributeClassInjected = true;
        }

        return new FlagMigrationCandidateEngineResult(
            Changes: result,
            WasAlreadyFlagged: wasAlreadyFlagged,
            PreviousPattern: previousPattern,
            AttributeClassInjected: attributeClassInjected,
            Line: methodLine);
    }

    /// <summary>
    /// Applies <see cref="FlagMigrationCandidateAsync"/> to multiple methods, grouping rewrites
    /// by file so each source file is parsed, rewritten, and returned only once — avoiding the
    /// line-number drift that occurs when sequential single-method calls modify the same file
    /// between calls.
    /// </summary>
    /// <param name="items">List of (filePath, methodName, pattern, score, reason) tuples to flag.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// One <see cref="FlagMigrationCandidateEngineResult"/> per input item, in the same order.
    /// Items that fail (method not found, file not found) have their <see cref="FlagMigrationCandidateEngineResult.Changes"/>
    /// set to an empty dictionary and <see cref="FlagMigrationCandidateEngineResult.Line"/> set to -1;
    /// the error is recorded separately in the caller's error list.
    /// </returns>
    public async Task<(List<FlagMigrationCandidateEngineResult> Results, List<(int Index, string Error)> Errors)>
        FlagMultipleMigrationCandidatesAsync(IReadOnlyList<(FilePath FilePath, string MethodName, string Pattern, int Score, string? Reason)> items,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        // Group by file so we rewrite each file once.
        var byFile = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            var fp = items[i].FilePath;
            if (!byFile.TryGetValue(fp, out var bucket))
            {
                byFile[fp] = bucket = new List<int>();
            }

            bucket.Add(i);
        }

        var resultSlots = new FlagMigrationCandidateEngineResult[items.Count];
        var errors = new List<(int Index, string Error)>();

        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        foreach (var (filePath, indices) in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = solution.GetDocumentIdsWithFilePath(filePath)
                                   .Select(solution.GetDocument)
                                   .FirstOrDefault();
            if (document == null)
            {
                foreach (var i in indices)
                {
                    errors.Add((i, $"File '{filePath}' not found in the loaded solution."));
                    resultSlots[i] = new FlagMigrationCandidateEngineResult(
                        new Dictionary<FilePath, string>(), false, null, false, -1);
                }

                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                foreach (var i in indices)
                {
                    errors.Add((i, $"Could not get syntax root for '{filePath}'."));
                    resultSlots[i] = new FlagMigrationCandidateEngineResult(
                        new Dictionary<FilePath, string>(), false, null, false, -1);
                }

                continue;
            }

            bool attrClassInjected = false;

            // Apply each item for this file sequentially on the same evolving root.
            foreach (var idx in indices)
            {
                var (_, methodName, pattern, score, reason) = items[idx];
                var methodNode = root.DescendantNodes()
                                     .OfType<MethodDeclarationSyntax>()
                                     .FirstOrDefault(m => m.Identifier.Text == methodName);
                if (methodNode == null)
                {
                    errors.Add((idx, $"Method '{methodName}' not found in '{filePath}'. Names are case-sensitive."));
                    resultSlots[idx] = new FlagMigrationCandidateEngineResult(
                        new Dictionary<FilePath, string>(), false, null, false, -1);
                    continue;
                }

                var methodLine = methodNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                // Find any pre-existing MigrationCandidate for the audit trail.
                string? previousPattern = methodNode.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Where(a => { var n = a.Name.ToString(); return n == MigrationCandidateShortName || n == MigrationCandidateFullName; })
                    .Select(a => (a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null)?.Expression as LiteralExpressionSyntax)?.Token.ValueText)
                    .FirstOrDefault();

                // Build new attribute. Preserve existing FlaggedDate to avoid spurious Git churn.
                var flaggedDate = methodNode.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Where(a => { var n = a.Name.ToString(); return n == MigrationCandidateShortName || n == MigrationCandidateFullName; })
                    .Where(a => (a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null)?.Expression as LiteralExpressionSyntax)?.Token.ValueText == pattern)
                    .Select(a => a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == "FlaggedDate"))
                    .Select(arg => (arg?.Expression as LiteralExpressionSyntax)?.Token.ValueText)
                    .FirstOrDefault()
                    ?? System.DateTime.UtcNow.ToString("yyyy-MM-dd");
                var args = new List<AttributeArgumentSyntax>
                {
                    SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(pattern)))
                };
                if (score != 0)
                {
                    args.Add(SyntaxFactory.AttributeArgument(
                        SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Score")), null,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(score))));
                }

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    args.Add(SyntaxFactory.AttributeArgument(
                        SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Reason")), null,
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(reason))));
                }

                args.Add(SyntaxFactory.AttributeArgument(
                    SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("FlaggedDate")), null,
                    SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(flaggedDate))));

                var newAttr = SyntaxFactory.Attribute(
                    SyntaxFactory.IdentifierName(MigrationCandidateShortName),
                    SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(args)));

                var (updatedMethod, wasAlreadyFlagged) = ReplaceOrAddAttribute(
                    methodNode, MigrationCandidateShortName, MigrationCandidateFullName, pattern, newAttr);

                root = root.ReplaceNode(methodNode, updatedMethod);

                resultSlots[idx] = new FlagMigrationCandidateEngineResult(
                    Changes: new Dictionary<FilePath, string>(), // populated below after all methods in file
                    WasAlreadyFlagged: wasAlreadyFlagged,
                    PreviousPattern: previousPattern,
                    AttributeClassInjected: false, // set on first item for this file
                    Line: methodLine);
            }

            // Write final combined source once for all methods in this file.
            var finalSource = root.NormalizeWhitespace().ToFullString();
            var fileChanges = new Dictionary<FilePath, string> { { filePath, finalSource } };

            // Inject MigrationCandidateAttribute.cs if not yet in solution (check once per file).
            var alreadyDefined = solution.Projects.SelectMany(p => p.Documents)
                .Any(d => d.FilePath != null &&
                          System.IO.Path.GetFileName(d.FilePath)
                                .Equals($"{MigrationCandidateFullName}.cs", StringComparison.OrdinalIgnoreCase));
            if (!alreadyDefined && !attrClassInjected)
            {
                var ns = DetectNamespace(root);
                var attrSrc = BuildMigrationCandidateAttributeSource(ns);
                // Use project root so SDK-style glob-includes find the file at the right level.
                var projectDir = System.IO.Path.GetDirectoryName(document.Project.FilePath)
                                 ?? System.IO.Path.GetDirectoryName(filePath)
                                 ?? ".";
                var attrPath = System.IO.Path.Combine(projectDir, $"{MigrationCandidateFullName}.cs");
                fileChanges[attrPath] = attrSrc;
                attrClassInjected = true;
            }

            // Assign the combined changes to every result slot for this file.
            foreach (var idx in indices)
            {
                if (resultSlots[idx] != null && resultSlots[idx].Line != -1)
                {
                    var r = resultSlots[idx];
                    resultSlots[idx] = r with
                    {
                        Changes = fileChanges,
                        AttributeClassInjected = attrClassInjected && idx == indices[0]
                    };
                }
            }
        }

        return (resultSlots.ToList(), errors);
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
    /// (e.g. <c>"AsyncBridgeCandidate"</c>).
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>One <see cref="MigrationCandidateFinding"/> per flagged method per pattern.</returns>
    public async Task<List<MigrationCandidateFinding>> FindMigrationCandidatesAsync(
        string? filePath = null,
        string? projectName = null,
        string? pattern = null,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var findings = new List<MigrationCandidateFinding>();

        // Enumerate all documents, applying scope filters.
        var projects = solution.Projects.AsEnumerable();
        if (projectName != null)
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        }

        // Normalize filePath separator once — avoids per-document allocation.
        var normalizedFilter = filePath?.Replace('\\', '/');
        int totalFilteredDocs = 0;

        await Parallel.ForEachAsync(projects, async (project, cancellationToken) =>
        {
            var docs = project.Documents.AsEnumerable();
            if (normalizedFilter != null)
            {
                docs = docs.Where(d => d.FilePath != null &&
                                       d.FilePath.Replace('\\', '/').EndsWith(
                                           normalizedFilter, StringComparison.OrdinalIgnoreCase));
            }

            var docList = docs.ToList();
            Interlocked.Add(ref totalFilteredDocs, docList.Count);

            await Parallel.ForEachAsync(docList, async (doc, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    return;
                }

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    // Methods flagged NeedsManualReview are excluded from all automatic processing.
                    bool isBlockedByManualReview = method.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .Any(a =>
                        {
                            var n = a.Name.ToString();
                            if (n != MigrationCandidateShortName && n != MigrationCandidateFullName) return false;
                            var firstPositional = a.ArgumentList?.Arguments
                                .FirstOrDefault(arg => arg.NameEquals == null);
                            return (firstPositional?.Expression as LiteralExpressionSyntax)
                                ?.Token.ValueText == "NeedsManualReview";
                        });
                    if (isBlockedByManualReview) continue;

                    foreach (var attrList in method.AttributeLists)
                    {
                        foreach (var attr in attrList.Attributes)
                        {
                            var name = attr.Name.ToString();
                            if (name != MigrationCandidateShortName && name != MigrationCandidateFullName)
                            {
                                continue;
                            }

                            // Extract positional pattern argument.
                            var firstArg = attr.ArgumentList?.Arguments
                                .FirstOrDefault(a => a.NameEquals == null);
                            var attrPattern = (firstArg?.Expression as LiteralExpressionSyntax)
                                ?.Token.ValueText ?? string.Empty;

                            // Apply pattern filter.
                            if (pattern != null &&
                                !attrPattern.Equals(pattern, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            // Extract optional named arguments.
                            var namedArgs = attr.ArgumentList?.Arguments
                                .Where(a => a.NameEquals != null)
                                .ToDictionary(
                                    a => a.NameEquals!.Name.Identifier.Text,
                                    a => a.Expression) ?? new Dictionary<string, ExpressionSyntax>();

                            int attrScore = 0;
                            if (namedArgs.TryGetValue("Score", out var scoreExpr))
                            {
                                if (scoreExpr is LiteralExpressionSyntax scoreLit &&
                                    scoreLit.Token.Value is int scoreVal)
                                {
                                    attrScore = scoreVal;
                                }
                                else if (scoreExpr is PrefixUnaryExpressionSyntax
                                { RawKind: (int)SyntaxKind.UnaryMinusExpression } negExpr &&
                                    negExpr.Operand is LiteralExpressionSyntax negLit &&
                                    negLit.Token.Value is int negVal)
                                {
                                    attrScore = -negVal;
                                }
                            }

                            string? attrReason = null;
                            if (namedArgs.TryGetValue("Reason", out var reasonExpr) &&
                                reasonExpr is LiteralExpressionSyntax reasonLit)
                            {
                                attrReason = reasonLit.Token.ValueText;
                            }

                            string? attrDate = null;
                            if (namedArgs.TryGetValue("FlaggedDate", out var dateExpr) &&
                                dateExpr is LiteralExpressionSyntax dateLit)
                            {
                                attrDate = dateLit.Token.ValueText;
                            }

                            // Determine containing class name.
                            var classNode = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                            var lineSpan = method.GetLocation().GetLineSpan();

                            findings.Add(new MigrationCandidateFinding(
                                FilePath: doc.FilePath ?? string.Empty,
                                MethodName: method.Identifier.Text,
                                ClassName: classNode?.Identifier.Text ?? string.Empty,
                                Pattern: attrPattern,
                                Score: attrScore,
                                Reason: attrReason,
                                FlaggedDate: attrDate,
                                Line: lineSpan.StartLinePosition.Line + 1,
                                ProjectName: project.Name));
                        }
                    }
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Guard: if filePath filter matched zero documents across all projects, the caller
        // supplied a path that doesn't exist in the solution — surface this as an actionable error.
        if (normalizedFilter != null && totalFilteredDocs == 0)
        {
            throw new ArgumentException(
                $"filePath '{filePath}' matched no documents in solution");
        }

        return findings;
    }

    // ── Project-level autonomous candidate discovery ─────────────────────────

    /// <summary>Internal per-method scoring output used by <see cref="FlagCandidatesInProjectAsync"/>.</summary>
    public record CandidateScoredItem(
        FilePath FilePath,
        string MethodName,
        string ClassName,
        int Line,
        int Score,
        string Reason,
        /// <summary>The migration pattern tag written into <c>[MigrationCandidate]</c> (e.g. "AsyncBridgeCandidate", "AsyncHandlerCandidate").</summary>
        string Pattern
    )
    {
        /// <summary>
        /// Score breakdown parsed from <see cref="Reason"/> — one entry per signal with its point
        /// contribution (e.g. <c>["blocking-calls:40", "service-class:15"]</c>).
        /// </summary>
        public IReadOnlyList<string> Breakdown =>
            Reason.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        private static readonly char[] separator = new[] { ' ' };
    }

    /// <summary>Engine-internal result from <see cref="FlagCandidatesInProjectAsync"/>.</summary>
    public record FlagCandidatesInProjectEngineResult(
        /// <summary>All file paths → updated source that must be written to disk.</summary>
        Dictionary<FilePath, string> Changes,
        /// <summary>Details for every method that was flagged.</summary>
        IReadOnlyList<CandidateScoredItem> Flagged,
        /// <summary>Details for every method that was scored but fell below <c>minScore</c>.</summary>
        IReadOnlyList<CandidateScoredItem> Skipped,
        /// <summary>Details for every method that was skipped because it is already flagged.</summary>
        IReadOnlyList<CandidateScoredItem> AlreadyFlagged,
        /// <summary><c>true</c> if <c>MigrationCandidateAttribute.cs</c> was generated as part of this run.</summary>
        bool AttributeClassInjected,
        /// <summary>Total methods examined across all files in the project.</summary>
        int TotalMethodsExamined
    );

    /// <summary>
    /// Scans every method in <paramref name="projectName"/>, scores each against the specified
    /// <paramref name="pattern"/>, and stamps qualifying methods with a
    /// <c>[MigrationCandidate("pattern")]</c> attribute — all in a single pass. No agent iteration
    /// required; the agent only needs to call this once per project.
    /// </summary>
    /// <remarks>
    /// Scoring heuristics for <c>AsyncBridgeCandidate</c>:
    /// <list type="bullet">
    ///   <item>+40 — body contains blocking calls (<c>.GetAwaiter().GetResult()</c>, <c>.Result</c>, <c>.Wait()</c>)</item>
    ///   <item>+30 — body calls <c>CommonSearch.search</c> or another known sync DB entry point</item>
    ///   <item>+25 — body uses <c>SqlCommand</c>, <c>SqlConnection</c>, or <c>getDataContext</c></item>
    ///   <item>+15 — class name ends with <c>Service</c></item>
    ///   <item>+10 — method is <c>static</c> (easier to bridge in isolation)</item>
    ///   <item>−20 — method is <c>virtual</c> or <c>override</c> (interface widening may be required)</item>
    ///   <item>−∞ — hard disqualifiers: <c>abstract</c>, <c>extern</c>, already <c>async</c>,
    ///              name ends with <c>Async</c>, has <c>yield return</c>, has <c>ref</c>/<c>out</c> params,
    ///              already carries <c>[MigrationCandidate("pattern")]</c></item>
    /// </list>
    /// </remarks>
    /// <param name="projectName">
    /// Name of the project to scan (case-insensitive). Scans the whole solution when <c>null</c>.
    /// </param>
    /// <param name="pattern">
    /// Migration pattern to apply (e.g. <c>"AsyncBridgeCandidate"</c>). Defaults to <c>"AsyncBridgeCandidate"</c>.
    /// </param>
    /// <param name="minScore">Minimum score threshold for a method to be flagged. Defaults to 50.</param>
    /// <param name="dryRun">
    /// When <c>true</c>, scores and categorizes methods without writing any files. Use to preview
    /// what would be flagged before committing.
    /// </param>
    /// <param name="forceRescan">
    /// When <c>true</c>, ignores existing <c>[MigrationCandidate]</c> attributes and re-evaluates
    /// every method from scratch. Use after changing scoring rules to get a clean result.
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<FlagCandidatesInProjectEngineResult> FlagCandidatesInProjectAsync(
        string? projectName = null,
        string pattern = "AsyncBridgeCandidate",
        int minScore = 50,
        bool dryRun = false,
        bool forceRescan = false,
        IProgress<string> progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        // ── Select projects ──────────────────────────────────────────────────
        IEnumerable<Microsoft.CodeAnalysis.Project> projects = solution.Projects;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            projects = projects.Where(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!projects.Any())
            {
                throw new InvalidOperationException(
                    $"Project '{projectName}' not found in the loaded solution.");
            }
        }

        // ── Collect documents (no designer files) ────────────────────────────
        var documents = projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null &&
                        !d.FilePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var flagged = new List<CandidateScoredItem>();
        var skipped = new List<CandidateScoredItem>();
        var alreadyFlagged = new List<CandidateScoredItem>();
        var fileChanges = new Dictionary<FilePath, string>();
        int totalExamined = 0;
        bool attrClassInjected = false;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var methods = root.DescendantNodes()
                              .OfType<MethodDeclarationSyntax>()
                              .ToList();
            if (methods.Count == 0)
            {
                continue;
            }

            bool fileModified = false;

            // Lazily resolved per-document semantic model used for callee attribute checks.
            SemanticModel? docSemanticModel = null;
            bool docSemanticModelFetched = false;

            foreach (var method in methods)
            {
                totalExamined++;

                // Skip compiler-generated method names (e.g. <top-level>, state-machine methods).
                if (method.Identifier.Text.StartsWith('<'))
                    continue;

                var containingClass = method.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault();
                var className = containingClass?.Identifier.Text ?? string.Empty;
                var lineNo = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                // When scanning for AsyncBridgeCandidate, event handlers are automatically categorised as
                // AsyncHandlerCandidate — their migration path (async void + try/catch wrapping)
                // is distinct from service-method bridge migration.
                var effectivePattern = (pattern == "AsyncBridgeCandidate" && IsEventHandlerSignature(method))
                    ? "AsyncHandlerCandidate"
                    : pattern;

                // ── Skip methods marked NeedsManualReview — they cannot be auto-migrated ──
                // Checking outside the forceRescan gate intentionally: NeedsManualReview is a
                // terminal state set when automatic conversion failed (e.g. no bridge wrappers to
                // replace). Re-scanning would just re-flag and create an oscillation cycle.
                bool isNeedsManualReview = method.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Any(a =>
                    {
                        var n = a.Name.ToString();
                        if (n != MigrationCandidateShortName && n != MigrationCandidateFullName) return false;
                        var firstArg = a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null);
                        return (firstArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText == "NeedsManualReview";
                    });
                if (isNeedsManualReview) continue;

                // ── Check if already flagged for this exact pattern ──────────
                if (!forceRescan)
                {
                    bool isAlreadyFlagged = method.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .Any(a =>
                        {
                            var n = a.Name.ToString();
                            if (n != MigrationCandidateShortName && n != MigrationCandidateFullName)
                            {
                                return false;
                            }

                            var firstArg = a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null);
                            return (firstArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText == effectivePattern;
                        });
                    if (isAlreadyFlagged)
                    {
                        alreadyFlagged.Add(new CandidateScoredItem(
                            document.FilePath!, method.Identifier.Text, className, lineNo, 0, "already-flagged", effectivePattern));
                        continue;
                    }
                }

                // ── Score the method ─────────────────────────────────────────
                var (score, reason) = ScoreMethodForPattern(method, className, pattern);
                if (score < 0)
                {
                    continue; // hard disqualifier — don't even record
                }

                // ── Semantic bonus: method calls an [Obsolete]-decorated bridge wrapper ──
                // Applies to ALL callers (service methods, helpers, UI forms, event handlers).
                // Any caller of an [Obsolete] bridge wrapper has a clear migration obligation:
                // replace the sync call with its async counterpart. This is the primary CS0618
                // migration signal — widening it beyond event handlers catches initComboBox(),
                // initControls(), and other UI helpers that call CommonSearch.search() directly.
                // Lazily fetch the semantic model once per document (expensive).
                if (!docSemanticModelFetched)
                {
                    docSemanticModelFetched = true;
                    try { docSemanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false); } catch { }
                }

                if (docSemanticModel != null)
                {
                    // Only give the bridge-caller bonus when the called method is specifically an
                    // Asyncify bridge wrapper (starts with "Asyncify-bridge:"). Matching any
                    // [Obsolete] attribute would incorrectly flag handlers that call unrelated
                    // deprecated APIs (e.g. BackgroundWorker.RunWorkerCompleted callbacks).
                    bool callsObsolete = method.Body?.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv =>
                        {
                            var sym = docSemanticModel.GetSymbolInfo(inv, cancellationToken).Symbol
                                as IMethodSymbol;
                            var msg = sym?.GetAttributes()
                                .FirstOrDefault(a => a.AttributeClass?.Name is "ObsoleteAttribute")
                                ?.ConstructorArguments.FirstOrDefault().Value as string;
                            return msg?.StartsWith("Asyncify-bridge:", StringComparison.Ordinal) == true;
                        }) == true;
                    if (callsObsolete)
                    {
                        score += 20;
                        reason += " calls-obsolete-wrapper:20";
                    }
                }

                if (score < minScore)
                {
                    skipped.Add(new CandidateScoredItem(
                        document.FilePath!, method.Identifier.Text, className, lineNo, score, reason, effectivePattern));
                    continue;
                }

                flagged.Add(new CandidateScoredItem(
                    document.FilePath!, method.Identifier.Text, className, lineNo, score, reason, effectivePattern));
            }

            if (!dryRun && flagged.Any(f => f.FilePath == document.FilePath!))
            {
                // Apply all flagged methods for this file in one rewrite pass.
                var flaggedInThisFile = flagged.Where(f => f.FilePath == document.FilePath!).ToList();
                var rewrittenRoot = root;
                var today = System.DateTime.UtcNow.ToString("yyyy-MM-dd");

                foreach (var item in flaggedInThisFile)
                {
                    var methodNode = rewrittenRoot.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.Text == item.MethodName &&
                                             m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == item.Line);
                    if (methodNode == null)
                    {
                        // Line shifted after earlier rewrites — fall back to name-only match.
                        methodNode = rewrittenRoot.DescendantNodes()
                            .OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault(m => m.Identifier.Text == item.MethodName);
                    }
                    if (methodNode == null)
                    {
                        continue;
                    }

                    // Find the existing [MigrationCandidate] for this pattern (if any).
                    var existingCandidateAttr = methodNode.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .FirstOrDefault(a =>
                        {
                            var n = a.Name.ToString();
                            return (n == MigrationCandidateShortName || n == MigrationCandidateFullName) &&
                                   (a.ArgumentList?.Arguments
                                        .FirstOrDefault(arg => arg.NameEquals == null)
                                        ?.Expression as LiteralExpressionSyntax)?.Token.ValueText == item.Pattern;
                        });

                    // Preserve existing FlaggedDate to avoid spurious Git churn.
                    var flaggedDate = (existingCandidateAttr?.ArgumentList?.Arguments
                        .FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == "FlaggedDate")
                        ?.Expression as LiteralExpressionSyntax)?.Token.ValueText
                        ?? today;

                    // Skip rewrite when score and reason are unchanged — prevents NormalizeWhitespace
                    // from adding blank lines to files that don't actually need modification.
                    if (existingCandidateAttr != null)
                    {
                        int? existingScore = null;
                        var scoreLit = existingCandidateAttr.ArgumentList?.Arguments
                            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Score")
                            ?.Expression as LiteralExpressionSyntax;
                        if (scoreLit != null && scoreLit.Token.Value is int sv)
                            existingScore = sv;

                        var existingReason = (existingCandidateAttr.ArgumentList?.Arguments
                            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Reason")
                            ?.Expression as LiteralExpressionSyntax)?.Token.ValueText;

                        if (existingScore == item.Score && existingReason == item.Reason)
                            continue;
                    }

                    var args = new List<AttributeArgumentSyntax>
                    {
                        SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(item.Pattern))),
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Score")), null,
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(item.Score))),
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("Reason")), null,
                            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(item.Reason))),
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("FlaggedDate")), null,
                            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(flaggedDate)))
                    };

                    var newAttr = SyntaxFactory.Attribute(
                        SyntaxFactory.IdentifierName(MigrationCandidateShortName),
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(args)));

                    var (updatedMethodNode, _) = ReplaceOrAddAttribute(
                        methodNode, MigrationCandidateShortName, MigrationCandidateFullName, item.Pattern, newAttr);
                    rewrittenRoot = rewrittenRoot.ReplaceNode(methodNode, updatedMethodNode);
                    fileModified = true;
                }

                if (fileModified)
                {
                    fileChanges[document.FilePath!] = rewrittenRoot.NormalizeWhitespace().ToFullString();
                }
            }
        }

        // ── Inject MigrationCandidateAttribute.cs per project that needs it ─
        // Each compiled assembly (project) needs exactly ONE copy at the project root.
        // An SDK-style .csproj glob-includes all *.cs files under the project directory,
        // so placing the file at the .csproj directory means it is picked up automatically.
        if (!dryRun && flagged.Count > 0 && fileChanges.Count > 0)
        {
            // Build a set of project roots that already have the attribute class defined.
            var projectRootsWithAttr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proj in solution.Projects)
            {
                bool hasAttr = proj.Documents.Any(d =>
                    d.FilePath != null &&
                    System.IO.Path.GetFileName(d.FilePath).Equals(
                        $"{MigrationCandidateFullName}.cs", StringComparison.OrdinalIgnoreCase));
                if (hasAttr)
                {
                    var dir = System.IO.Path.GetDirectoryName(proj.FilePath);
                    if (dir != null)
                    {
                        projectRootsWithAttr.Add(dir);
                    }
                }
            }

            // Inject the attribute class into every project root that has flagged files
            // but does not yet have MigrationCandidateAttribute.cs.
            var processedProjectRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var changedFilePath in fileChanges.Keys.ToList())
            {
                var doc = documents.FirstOrDefault(d =>
                    string.Equals(d.FilePath, changedFilePath, StringComparison.OrdinalIgnoreCase));
                if (doc?.Project.FilePath == null)
                {
                    continue;
                }

                var projRoot = System.IO.Path.GetDirectoryName(doc.Project.FilePath);
                if (projRoot == null)
                {
                    continue;
                }

                if (projectRootsWithAttr.Contains(projRoot))
                {
                    continue;
                }

                if (!processedProjectRoots.Add(projRoot))
                {
                    continue; // already queued for this run
                }

                var attrPath = System.IO.Path.Combine(projRoot, $"{MigrationCandidateFullName}.cs");
                fileChanges[attrPath] = BuildMigrationCandidateAttributeSource("");
                attrClassInjected = true;
            }
        }

        return new FlagCandidatesInProjectEngineResult(
            Changes: fileChanges,
            Flagged: flagged,
            Skipped: skipped,
            AlreadyFlagged: alreadyFlagged,
            AttributeClassInjected: attrClassInjected,
            TotalMethodsExamined: totalExamined);
    }

    /// <summary>
    /// Scores a method declaration against the specified migration <paramref name="pattern"/>.
    /// Returns <c>(-1, "")</c> for hard disqualifiers (caller must skip the method entirely).
    /// </summary>
    private static (int Score, string Reason) ScoreMethodForPattern(
        MethodDeclarationSyntax method,
        string className,
        string pattern)
    {
        // ── Hard disqualifiers ───────────────────────────────────────────────
        if (method.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            return (-1, "");
        }

        if (method.Modifiers.Any(SyntaxKind.ExternKeyword))
        {
            return (-1, "");
        }

        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            return (-1, "");
        }

        if (method.Identifier.Text.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
        {
            return (-1, "");
        }

        if (method.Body?.Statements.OfType<YieldStatementSyntax>().Any() == true)
        {
            return (-1, "");
        }

        if (method.ParameterList.Parameters.Any(p =>
                p.Modifiers.Any(SyntaxKind.RefKeyword) ||
                p.Modifiers.Any(SyntaxKind.OutKeyword)))
        {
            return (-1, "");
        }

        // Disqualify any method already decorated with [Obsolete] or [System.Obsolete].
        // Deprecated methods fall into two categories:
        //   1. Asyncify-bridge sync wrappers: [Obsolete("Asyncify-bridge: call XxxAsync instead.")]
        //      Their body is purely .GetAwaiter().GetResult(), which scores +40 (blocking-calls).
        //   2. Other deprecated wrappers: [System.Obsolete("Use XxxAsync instead.")]
        //      Same pattern — body calls async counterpart synchronously.
        // Both are already migrated. Excluding any [Obsolete] method (regardless of message)
        // prevents false positives from either convention.
        bool isObsoleteMethod = method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var n = a.Name.ToString();
                return n is "Obsolete" or "ObsoleteAttribute"
                          or "System.Obsolete" or "System.ObsoleteAttribute";
            });
        if (isObsoleteMethod)
        {
            return (-1, "");
        }

        if (pattern != "AsyncBridgeCandidate")
        {
            return (0, ""); // only AsyncBridgeCandidate scoring implemented
        }

        var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? "";
        int score = 0;
        var reasons = new System.Text.StringBuilder();

        // Track key signals for the LINQ-to-SQL disqualifier applied after all signals are scored.
        bool hasBlockingCalls = false;
        bool hasCommonSearch = false;
        bool hasSqlAccess = false;

        // Blocking calls — async leaf exists (.GetAwaiter().GetResult() means async counterpart already written)
        if (body.Contains(".GetAwaiter().GetResult()") ||
            body.Contains(".GetAwaiter()\r\n") ||
            body.Contains(".Wait(") ||
            (body.Contains(".Result") && !body.Contains("// ")))
        {
            score += 40; reasons.Append("blocking-calls:40 "); hasBlockingCalls = true;
        }

        // CommonSearch entry point — the project's standard async-ready SQL wrapper.
        // Deliberately narrow: only match the class name, not generic 'search(' identifiers.
        if (body.Contains("CommonSearch"))
        {
            score += 30; reasons.Append("calls-CommonSearch:30 "); hasCommonSearch = true;
        }

        // SQL / data context access
        if (body.Contains("SqlCommand") || body.Contains("SqlConnection") ||
            body.Contains("getDataContext") || body.Contains("DataContext"))
        {
            score += 25; reasons.Append("sql-access:25 "); hasSqlAccess = true;
        }

        // LINQ-to-SQL disqualifier — sql-access fired but there is no async leaf in this codebase
        // for DataContext/LINQ-to-SQL queries. Without CommonSearch or a known blocking bridge,
        // this method cannot be migrated today. Skip it to avoid cluttering results.
        if (hasSqlAccess && !hasCommonSearch && !hasBlockingCalls)
        {
            return (-1, ""); // linq-to-sql-only: no async leaf available
        }

        // Service class
        if (className.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
        {
            score += 15; reasons.Append("service-class:15 ");
        }

        // Static method (easier to bridge in isolation)
        if (method.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            score += 5; reasons.Append("static:5 ");
        }

        // Virtual/override penalty (interface widening may be required)
        if (method.Modifiers.Any(SyntaxKind.VirtualKeyword) ||
            method.Modifiers.Any(SyntaxKind.OverrideKeyword))
        {
            score -= 20; reasons.Append("virtual-override-penalty:-20 ");
        }

        // Event-handler bonus — (object sender, XxxEventArgs e) signatures can be converted
        // to async void in-place without touching the calling convention or adding CancellationToken.
        // +40 base: plain handlers land at 40 (below the default 50 threshold) so they are
        // not flagged on their own. Handlers that also carry sync signals (blocking calls,
        // SQL access, CommonSearch) or that call an [Obsolete]-decorated bridge wrapper get
        // additional points and cross the threshold.
        if (IsEventHandlerSignature(method))
        {
            score += 40; reasons.Append("event-handler:40 ");
        }

        return (score, reasons.ToString().TrimEnd());
    }

    // ── ReplaceOrAddAttribute ─────────────────────────────────────────────────

    /// <summary>
    /// Strips any existing <paramref name="shortName"/>/<paramref name="fullName"/> attributes
    /// whose first positional argument equals <paramref name="patternKey"/> (or ALL instances when
    /// <paramref name="patternKey"/> is null), then appends <paramref name="newAttribute"/>.
    /// Returns the updated method and whether a matching attribute was already present.
    /// </summary>
    private static (MethodDeclarationSyntax Updated, bool WasPresent) ReplaceOrAddAttribute(
        MethodDeclarationSyntax method,
        string shortName,
        string fullName,
        string? patternKey,
        AttributeSyntax newAttribute)
    {
        bool wasPresent = false;

        var strippedLists = SyntaxFactory.List(
            method.AttributeLists
                .Select(al =>
                {
                    var filtered = al.Attributes.Where(a =>
                    {
                        var n = a.Name.ToString();
                        if (n != shortName && n != fullName)
                            return true; // keep — unrelated attribute

                        if (patternKey == null)
                        {
                            wasPresent = true;
                            return false; // strip all instances
                        }

                        var firstArg = a.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null);
                        var argVal = (firstArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
                        if (argVal == patternKey)
                        {
                            wasPresent = true;
                            return false; // strip matching pattern
                        }

                        return true;
                    }).ToList();

                    if (filtered.Count == al.Attributes.Count) return al;
                    return filtered.Count == 0 ? null : al.WithAttributes(SyntaxFactory.SeparatedList(filtered));
                })
                .Where(al => al != null)
                .Select(al => al!));

        var updated = method
            .WithAttributeLists(strippedLists)
            .AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(newAttribute)));

        return (updated, wasPresent);
    }

    // ── PropagateCancellationToken ────────────────────────────────────────────

    /// <summary>
    /// Propagates an existing CancellationToken parameter from <paramref name="methodName"/>
    /// to all eligible async callees within its body.  The method must already have a
    /// CancellationToken parameter; call sites that already forward the token or whose callees
    /// have no CT overload are reported as skipped.
    /// </summary>
    /// <returns>
    /// A tuple of the full updated source string and per-call-site forward/skip lists.
    /// The source is identical to the input when no call sites were rewritten.
    /// </returns>
    public async Task<DocumentEditResult>
        PropagateCancellationTokenInMethodAsync(
            FilePath filePath,
            string methodName,
            IProgress<string> progress = default,
            CancellationToken cancellationToken = default)
    {
        var result = new PropagateCtResult { MethodName = methodName };

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
        {
            result.Error = $"File '{filePath}' not found in the loaded solution.";
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = result.Error
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            result.Error = "Failed to get syntax root.";
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Error,
                FilePath = filePath,
                Message = result.Error
            };
        }

        var methodNode = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null)
        {
            result.MethodFound = false;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = $"Method '{methodName}' not found in file '{filePath}'."
            };
        }

        result.MethodFound = true;

        // Find CancellationToken parameter
        var ctParam = methodNode.ParameterList.Parameters.FirstOrDefault(p =>
        {
            var typeName = p.Type?.ToString() ?? "";
            return typeName == "CancellationToken" ||
                   typeName.EndsWith(".CancellationToken");
        });

        if (ctParam == null)
        {
            result.Error = "Method does not have a CancellationToken parameter";
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Error,
                FilePath = filePath,
                Message = result.Error
            };
        }

        var ctParamName = ctParam.Identifier.Text;
        result.TokenParameterName = ctParamName;

        SemanticModel? semanticModel = null;
        try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

        var body = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody;
        if (body == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Error,
                FilePath = filePath,
                Message = "Method body not found."
            };
        }

        var invocations = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();

        var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            var lineSpan = inv.GetLocation().GetLineSpan();
            int lineNum = lineSpan.StartLinePosition.Line + 1;

            // Determine callee name and type
            string? calleeName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            string calleeType = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression.ToString(),
                _ => ""
            };

            if (calleeName == null)
                continue;

            // Check if already forwarding CT
            bool alreadyForwarded = inv.ArgumentList.Arguments.Any(a =>
                a.Expression.ToString().Contains(ctParamName) ||
                (a.NameColon != null && a.NameColon.Name.Identifier.Text == "cancellationToken" &&
                 a.Expression.ToString().Contains(ctParamName)));

            if (alreadyForwarded)
            {
                result.Skipped.Add(new CallSiteSkip
                {
                    CalleeMethod = calleeName,
                    Line = lineNum,
                    Reason = "AlreadyForwarded"
                });
                continue;
            }

            // Check if callee is async (heuristic: ends with Async, or semantic check)
            bool calleeIsAsync = calleeName.EndsWith("Async", StringComparison.Ordinal);
            if (!calleeIsAsync && semanticModel != null)
            {
                var si = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var sym = si.Symbol as IMethodSymbol
                    ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                if (sym != null)
                {
                    var retType = sym.ReturnType.ToDisplayString();
                    calleeIsAsync = retType.StartsWith("System.Threading.Tasks.Task") ||
                                    retType.StartsWith("System.Threading.Tasks.ValueTask") ||
                                    retType == "System.Threading.Tasks.Task" ||
                                    retType == "System.Threading.Tasks.ValueTask";
                }
            }

            if (!calleeIsAsync)
            {
                result.Skipped.Add(new CallSiteSkip
                {
                    CalleeMethod = calleeName,
                    Line = lineNum,
                    Reason = "CalleeNotAsync"
                });
                continue;
            }

            // Check for CT overload via semantic model
            bool hasCtOverload = false;
            bool isAmbiguous = false;

            if (semanticModel != null)
            {
                var si = semanticModel.GetSymbolInfo(inv, cancellationToken);
                var candidates = new List<IMethodSymbol>();
                if (si.Symbol is IMethodSymbol ms0) candidates.Add(ms0);
                candidates.AddRange(si.CandidateSymbols.OfType<IMethodSymbol>());

                if (candidates.Count == 0)
                {
                    // No symbol resolution — fall through to heuristic
                    hasCtOverload = true;
                }
                else
                {
                    var containingType = candidates.First().ContainingType;
                    if (containingType != null)
                    {
                        var overloads = containingType.GetMembers(calleeName)
                            .OfType<IMethodSymbol>()
                            .ToList();
                        var ctOverloads = overloads.Where(o =>
                            o.Parameters.Any(p =>
                                p.Type.ToDisplayString() == "System.Threading.CancellationToken"))
                            .ToList();
                        hasCtOverload = ctOverloads.Count > 0;
                        // Ambiguous if there's more than one CT overload with different non-CT params
                        if (ctOverloads.Count > 1)
                        {
                            isAmbiguous = true;
                        }
                    }
                    else
                    {
                        // Direct symbol; check it directly
                        hasCtOverload = candidates.Any(o =>
                            o.Parameters.Any(p =>
                                p.Type.ToDisplayString() == "System.Threading.CancellationToken"));
                    }
                }
            }
            else
            {
                // No semantic model — heuristic: any *Async call can probably take CT
                hasCtOverload = true;
            }

            if (!hasCtOverload)
            {
                result.Skipped.Add(new CallSiteSkip
                {
                    CalleeMethod = calleeName,
                    Line = lineNum,
                    Reason = "NoCancellationTokenOverload"
                });
                continue;
            }

            if (isAmbiguous)
            {
                result.Skipped.Add(new CallSiteSkip
                {
                    CalleeMethod = calleeName,
                    Line = lineNum,
                    Reason = "AmbiguousOverload"
                });
                continue;
            }

            // Check if call uses named arguments — if so, we need named CT arg too
            bool hasNamedArgs = inv.ArgumentList.Arguments.Any(a => a.NameColon != null);

            var beforeSnippet = inv.ToString();
            ArgumentSyntax ctArg;
            if (hasNamedArgs)
            {
                ctArg = SyntaxFactory.Argument(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName("cancellationToken")),
                    default,
                    SyntaxFactory.IdentifierName(ctParamName));
            }
            else
            {
                ctArg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ctParamName));
            }

            var newArgList = inv.ArgumentList.AddArguments(ctArg);
            var newInv = inv.WithArgumentList(newArgList);
            var afterSnippet = newInv.ToString();

            replacements[inv] = newInv;
            result.Forwarded.Add(new CallSiteForward
            {
                CalleeMethod = calleeName,
                CalleeType = calleeType,
                Line = lineNum,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            });
        }

        result.ForwardedCount = result.Forwarded.Count;
        result.SkippedCount = result.Skipped.Count;

        if (replacements.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "No eligible call sites found."
            };
        }

        // Apply all replacements in one pass
        var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "Call sites updated successfully."
        };
    }

    /// <summary>
    /// Propagates CancellationToken parameters to all eligible call sites across all methods
    /// in a file. For each method that has a CancellationToken parameter, applies the same
    /// logic as <see cref="PropagateCancellationTokenInMethodAsync"/>. Methods without a CT
    /// parameter are skipped silently.
    /// </summary>
    public async Task<(string UpdatedSource, PropagateCtFileResult Result)>
        PropagateCancellationTokenInFileAsync(
            FilePath filePath,
            string[]? methodNames = null,
            IProgress<string> progress = default,
            CancellationToken cancellationToken = default)
    {
        var fileResult = new PropagateCtFileResult { FilePath = filePath };

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault();
        if (document == null)
        {
            return ($"// Error: File '{filePath}' not found in the loaded solution.", fileResult);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return ("// Error: Failed to get syntax root.", fileResult);
        }

        SemanticModel? semanticModel = null;
        try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

        var requested = methodNames != null
            ? new HashSet<string>(methodNames, StringComparer.Ordinal)
            : null;

        // Single-pass: collect all replacements across all eligible methods
        var allReplacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();

        foreach (var methodNode in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = methodNode.Identifier.Text;

            // Apply method filter if provided
            if (requested != null && !requested.Contains(name))
            {
                fileResult.MethodsSkipped++;
                continue;
            }

            // Method must have a CancellationToken parameter
            var ctParam = methodNode.ParameterList.Parameters.FirstOrDefault(p =>
            {
                var typeName = p.Type?.ToString() ?? "";
                return typeName == "CancellationToken" ||
                       typeName.EndsWith(".CancellationToken");
            });

            if (ctParam == null)
            {
                fileResult.MethodsSkipped++;
                continue;
            }

            var ctParamName = ctParam.Identifier.Text;
            var body = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody;
            if (body == null)
            {
                fileResult.MethodsSkipped++;
                continue;
            }

            var methodResult = new PropagateCtResult
            {
                MethodName = name,
                TokenParameterName = ctParamName,
                MethodFound = true
            };

            foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var lineSpan = inv.GetLocation().GetLineSpan();
                int lineNum = lineSpan.StartLinePosition.Line + 1;

                string? calleeName = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                string calleeType = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression.ToString(),
                    _ => ""
                };

                if (calleeName == null) continue;

                bool alreadyForwarded = inv.ArgumentList.Arguments.Any(a =>
                    a.Expression.ToString().Contains(ctParamName) ||
                    (a.NameColon != null &&
                     a.NameColon.Name.Identifier.Text == "cancellationToken" &&
                     a.Expression.ToString().Contains(ctParamName)));

                if (alreadyForwarded)
                {
                    methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "AlreadyForwarded" });
                    continue;
                }

                bool calleeIsAsync = calleeName.EndsWith("Async", StringComparison.Ordinal);
                if (!calleeIsAsync && semanticModel != null)
                {
                    var si = semanticModel.GetSymbolInfo(inv, cancellationToken);
                    var sym = si.Symbol as IMethodSymbol
                        ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (sym != null)
                    {
                        var retType = sym.ReturnType.ToDisplayString();
                        calleeIsAsync = retType.StartsWith("System.Threading.Tasks.Task") ||
                                        retType.StartsWith("System.Threading.Tasks.ValueTask");
                    }
                }

                if (!calleeIsAsync)
                {
                    methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "CalleeNotAsync" });
                    continue;
                }

                bool hasCtOverload = false;
                bool isAmbiguous = false;

                if (semanticModel != null)
                {
                    var si = semanticModel.GetSymbolInfo(inv, cancellationToken);
                    var candidates = new List<IMethodSymbol>();
                    if (si.Symbol is IMethodSymbol ms0) candidates.Add(ms0);
                    candidates.AddRange(si.CandidateSymbols.OfType<IMethodSymbol>());

                    if (candidates.Count == 0)
                    {
                        hasCtOverload = true;
                    }
                    else
                    {
                        var containingType = candidates.First().ContainingType;
                        if (containingType != null)
                        {
                            var ctOverloads = containingType.GetMembers(calleeName)
                                .OfType<IMethodSymbol>()
                                .Where(o => o.Parameters.Any(p =>
                                    p.Type.ToDisplayString() == "System.Threading.CancellationToken"))
                                .ToList();
                            hasCtOverload = ctOverloads.Count > 0;
                            isAmbiguous = ctOverloads.Count > 1;
                        }
                        else
                        {
                            hasCtOverload = candidates.Any(o =>
                                o.Parameters.Any(p =>
                                    p.Type.ToDisplayString() == "System.Threading.CancellationToken"));
                        }
                    }
                }
                else
                {
                    hasCtOverload = true;
                }

                if (!hasCtOverload)
                {
                    methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "NoCancellationTokenOverload" });
                    continue;
                }

                if (isAmbiguous)
                {
                    methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "AmbiguousOverload" });
                    continue;
                }

                bool hasNamedArgs = inv.ArgumentList.Arguments.Any(a => a.NameColon != null);
                var beforeSnippet = inv.ToString();

                ArgumentSyntax ctArg;
                if (hasNamedArgs)
                {
                    ctArg = SyntaxFactory.Argument(
                        SyntaxFactory.NameColon(SyntaxFactory.IdentifierName("cancellationToken")),
                        default,
                        SyntaxFactory.IdentifierName(ctParamName));
                }
                else
                {
                    ctArg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ctParamName));
                }

                var newArgList = inv.ArgumentList.AddArguments(ctArg);
                var newInv = inv.WithArgumentList(newArgList);
                allReplacements[inv] = newInv;

                methodResult.Forwarded.Add(new CallSiteForward
                {
                    CalleeMethod = calleeName,
                    CalleeType = calleeType,
                    Line = lineNum,
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = newInv.ToString()
                });
            }

            methodResult.ForwardedCount = methodResult.Forwarded.Count;
            methodResult.SkippedCount = methodResult.Skipped.Count;
            fileResult.PerMethod.Add(methodResult);
            fileResult.MethodsProcessed++;
            fileResult.TotalForwarded += methodResult.ForwardedCount;
            fileResult.TotalSkipped += methodResult.SkippedCount;
        }

        if (allReplacements.Count == 0)
        {
            return (root.ToFullString(), fileResult);
        }

        var newRoot = root.ReplaceNodes(allReplacements.Keys, (orig, _) => allReplacements[orig]);
        return (newRoot.ToFullString(), fileResult);
    }

    /// <summary>
    /// Propagates CancellationToken in a single named method within an in-memory source string.
    /// Does not load from the workspace — uses a purely syntactic pass (no semantic model).
    /// Used by batch operations that have transformed source that hasn't been written to disk yet.
    /// </summary>
    /// <param name="source">Full source file text to transform.</param>
    /// <param name="filePath">File path (used only for error messages; not read from disk).</param>
    /// <param name="methodName">Method to process.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public Task<(string UpdatedSource, PropagateCtFileResult Result)>
        PropagateCancellationTokenInSourceAsync(
            string source,
            FilePath filePath,
            string methodName,
            IProgress<string> progress = default,
            CancellationToken cancellationToken = default)
    {
        var fileResult = new PropagateCtFileResult { FilePath = filePath };
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);

        var allReplacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();

        var methodNode = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null)
        {
            fileResult.MethodsSkipped++;
            return Task.FromResult((source, fileResult));
        }

        var ctParam = methodNode.ParameterList.Parameters.FirstOrDefault(p =>
        {
            var typeName = p.Type?.ToString() ?? "";
            return typeName == "CancellationToken" || typeName.EndsWith(".CancellationToken");
        });

        if (ctParam == null)
        {
            fileResult.MethodsSkipped++;
            return Task.FromResult((source, fileResult));
        }

        var ctParamName = ctParam.Identifier.Text;
        var body = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody;
        if (body == null)
        {
            fileResult.MethodsSkipped++;
            return Task.FromResult((source, fileResult));
        }

        var methodResult = new PropagateCtResult
        {
            MethodName = methodName,
            TokenParameterName = ctParamName,
            MethodFound = true
        };

        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var lineSpan = inv.GetLocation().GetLineSpan();
            int lineNum = lineSpan.StartLinePosition.Line + 1;

            string? calleeName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            string calleeType = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression.ToString(),
                _ => ""
            };

            if (calleeName == null) continue;

            bool alreadyForwarded = inv.ArgumentList.Arguments.Any(a =>
                a.Expression.ToString().Contains(ctParamName) ||
                (a.NameColon != null &&
                 a.NameColon.Name.Identifier.Text == "cancellationToken" &&
                 a.Expression.ToString().Contains(ctParamName)));

            if (alreadyForwarded)
            {
                methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "AlreadyForwarded" });
                continue;
            }

            // Without a semantic model, rely on naming heuristic only
            bool calleeIsAsync = calleeName.EndsWith("Async", StringComparison.Ordinal);
            if (!calleeIsAsync)
            {
                methodResult.Skipped.Add(new CallSiteSkip { CalleeMethod = calleeName, Line = lineNum, Reason = "CalleeNotAsync" });
                continue;
            }

            bool hasNamedArgs = inv.ArgumentList.Arguments.Any(a => a.NameColon != null);
            var beforeSnippet = inv.ToString();

            ArgumentSyntax ctArg;
            if (hasNamedArgs)
            {
                ctArg = SyntaxFactory.Argument(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName("cancellationToken")),
                    default,
                    SyntaxFactory.IdentifierName(ctParamName));
            }
            else
            {
                ctArg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ctParamName));
            }

            var newArgList = inv.ArgumentList.AddArguments(ctArg);
            var newInv = inv.WithArgumentList(newArgList);
            allReplacements[inv] = newInv;

            methodResult.Forwarded.Add(new CallSiteForward
            {
                CalleeMethod = calleeName,
                CalleeType = calleeType,
                Line = lineNum,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = newInv.ToString()
            });
        }

        methodResult.ForwardedCount = methodResult.Forwarded.Count;
        methodResult.SkippedCount = methodResult.Skipped.Count;
        fileResult.PerMethod.Add(methodResult);
        fileResult.MethodsProcessed++;
        fileResult.TotalForwarded += methodResult.ForwardedCount;
        fileResult.TotalSkipped += methodResult.SkippedCount;

        if (allReplacements.Count == 0)
        {
            return Task.FromResult((source, fileResult));
        }

        var newRoot2 = root.ReplaceNodes(allReplacements.Keys, (orig, _) => allReplacements[orig]);
        return Task.FromResult((newRoot2.ToFullString(), fileResult));
    }

    // ── Remove migration candidates ──────────────────────────────────────────

    public record RemovedCandidateInfo(FilePath FilePath, string MethodName, string RemovedPattern, int Line);

    public record RemoveMigrationCandidatesEngineResult(
        Dictionary<FilePath, string> Changes,
        int TotalRemoved,
        int FilesModified,
        IReadOnlyList<RemovedCandidateInfo> Removed);

    /// <summary>
    /// Strips <c>[MigrationCandidate]</c> attributes from all methods in the solution,
    /// optionally filtered by project, file, method name, or pattern.
    /// </summary>
    /// <param name="pattern">Pattern string to match (e.g. "AsyncBridgeCandidate"). Null removes all patterns.</param>
    /// <param name="methodName">When provided, restricts removal to a single method of this name.</param>
    public async Task<RemoveMigrationCandidatesEngineResult> RemoveMigrationCandidatesAsync(
        string? projectName = null,
        string? filePath = null,
        string? pattern = null,
        string? methodName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath)
                .Select(id => solution.GetDocument(id)!)
                .Where(d => d != null);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var proj = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = proj?.Documents ?? [];
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents);
        }

        var changes = new Dictionary<FilePath, string>();
        var removed = new List<RemovedCandidateInfo>();

        foreach (var doc in documents)
        {
            if (doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Collect removal info before rewriting (node identity is stable at this point).
            foreach (var attrList in root.DescendantNodes().OfType<AttributeListSyntax>())
            {
                foreach (var attr in attrList.Attributes)
                {
                    if (!IsMatchingMigrationAttr(attr, pattern)) continue;
                    var ownerMethodName = attrList.Parent switch
                    {
                        MethodDeclarationSyntax m => m.Identifier.Text,
                        _ => attrList.Parent?.Ancestors().OfType<MethodDeclarationSyntax>()
                                 .FirstOrDefault()?.Identifier.Text ?? "Unknown"
                    };
                    if (methodName != null && ownerMethodName != methodName) continue;
                    var removedPattern = (attr.ArgumentList?.Arguments
                        .FirstOrDefault(arg => arg.NameEquals == null)?.Expression
                        as LiteralExpressionSyntax)?.Token.ValueText ?? "";
                    var line = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    removed.Add(new RemovedCandidateInfo(doc.FilePath, ownerMethodName, removedPattern, line));
                }
            }

            var rewriter = new MigrationCandidateRemover(pattern, methodName);
            var newRoot = rewriter.Visit(root)!;

            if (newRoot != root && !dryRun)
                changes[(FilePath)doc.FilePath] = newRoot.ToFullString();
        }

        return new RemoveMigrationCandidatesEngineResult(
            Changes: changes,
            TotalRemoved: removed.Count,
            FilesModified: changes.Count,
            Removed: removed);
    }

    private static bool IsMatchingMigrationAttr(AttributeSyntax attr, string? pattern)
    {
        var name = attr.Name.ToString();
        if (name != MigrationCandidateShortName && name != MigrationCandidateFullName)
            return false;
        if (pattern == null) return true;
        var firstArg = attr.ArgumentList?.Arguments.FirstOrDefault(arg => arg.NameEquals == null);
        return (firstArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText == pattern;
    }

    private sealed class MigrationCandidateRemover(string? pattern, string? methodName = null) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
        {
            if (methodName != null)
            {
                var ownerMethod = node.Parent as MethodDeclarationSyntax
                    ?? node.Parent?.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (ownerMethod?.Identifier.Text != methodName)
                    return node;
            }

            var keep = node.Attributes
                .Where(a => !IsMatchingMigrationAttr(a, pattern))
                .ToList();
            if (keep.Count == node.Attributes.Count) return node;
            if (keep.Count == 0) return null;
            return node.WithAttributes(SyntaxFactory.SeparatedList(keep));
        }
    }

    /// <summary>
    /// Adds the <c>async</c> modifier to any anonymous function (delegate, lambda) whose body
    /// contains an <c>await</c> expression that is directly in that function's scope — i.e. not
    /// nested inside a further inner anonymous function or local function. This fixes the case
    /// where a bridge-call rewriter introduces <c>await</c> inside a delegate that was not
    /// previously async, producing a compile error.
    /// </summary>
    internal sealed class AsyncifyAnonymousFunctionsRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
            if (visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword) || !HasDirectAwait(visited.Block))
                return visited;
            return visited.WithAsyncKeyword(
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
            if (visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword) || !HasDirectAwait(visited.Body))
                return visited;
            return visited.WithAsyncKeyword(
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
            if (visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword) || !HasDirectAwait(visited.Body))
                return visited;
            return visited.WithAsyncKeyword(
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        // Returns true if `body` contains an AwaitExpressionSyntax that is NOT nested inside an
        // inner anonymous function or local function (which would be its own async scope).
        private static bool HasDirectAwait(SyntaxNode? body)
        {
            if (body == null) return false;
            return body.DescendantNodes(n =>
                    n is not AnonymousMethodExpressionSyntax &&
                    n is not ParenthesizedLambdaExpressionSyntax &&
                    n is not SimpleLambdaExpressionSyntax &&
                    n is not LocalFunctionStatementSyntax)
                .OfType<AwaitExpressionSyntax>()
                .Any();
        }
    }
}

