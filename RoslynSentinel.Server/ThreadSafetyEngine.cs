using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ThreadSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ThreadSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Adds a private lock object and wraps a method's body in a lock statement.
    /// </summary>
    public async Task<string> MakeMethodThreadSafeAsync(string filePath, string methodName, string lockFieldName = "_lock", CancellationToken cancellationToken = default)
    {
        try
        {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return $"// Error: File '{filePath}' not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return $"// Error: Failed to get syntax root for '{filePath}'.";
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || method.Body == null) return $"// Error: Method '{methodName}' not found or has no body.";

        var typeNode = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeNode == null) return $"// Error: Containing type not found for method '{methodName}'.";

        // Check if a field with lockFieldName already exists
        var existingLockField = typeNode.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == lockFieldName));

        bool addLockField;
        if (existingLockField != null)
        {
            var fieldType = existingLockField.Declaration.Type.ToString().Trim();
            if (fieldType == "object")
            {
                // Reuse existing object lock field
                addLockField = false;
            }
            else
            {
                return $"// Error: Field '{lockFieldName}' already exists but is not of type 'object'. Please supply a different lockFieldName.";
            }
        }
        else
        {
            addLockField = true;
        }

        var lockField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("object"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(lockFieldName)
                .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("object")).WithArgumentList(SyntaxFactory.ArgumentList()))))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        var newBody = SyntaxFactory.Block(
            SyntaxFactory.LockStatement(
                SyntaxFactory.IdentifierName(lockFieldName),
                method.Body));

        var newMethod = method.WithBody(newBody);
        var newTypeNode = typeNode.ReplaceNode(method, newMethod);

        if (addLockField)
        {
            newTypeNode = newTypeNode.InsertNodesBefore(newTypeNode.Members.First(), new[] { lockField });
        }

        var newRoot = root.ReplaceNode(typeNode, newTypeNode);
        return newRoot.NormalizeWhitespace().ToFullString();
        }
        catch (Exception ex)
        {
            return $"// Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Converts lock statements inside a method and ALL other methods to async-safe SemaphoreSlim pattern.
    /// Adds a SemaphoreSlim field and replaces all lock statements with await _semaphore.WaitAsync() + try/finally.
    /// </summary>
    public async Task<string> ConvertLockToSemaphoreSlimAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Failed to get syntax root.");
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || method.Body == null) throw new Exception("Method or body not found.");

        var typeNode = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeNode == null) throw new Exception("Type not found.");

        // Find the lock object being used in this method
        var lockStatements = method.Body.DescendantNodes().OfType<LockStatementSyntax>().ToList();
        if (!lockStatements.Any()) return root.ToFullString();

        // Get the lock object identifier (e.g., "_lock")
        var lockExpression = lockStatements.First().Expression.ToString().Trim();

        // Find ALL lock statements in the entire type that use the same lock object
        var allLockStatementsInType = typeNode.DescendantNodes().OfType<LockStatementSyntax>()
            .Where(ls => ls.Expression.ToString().Trim() == lockExpression)
            .ToList();

        if (!allLockStatementsInType.Any()) return root.ToFullString();

        const string semaphoreName = "_semaphore";
        var hasSemaphore = typeNode.Members.OfType<FieldDeclarationSyntax>()
            .Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == semaphoreName));

        // Build the conversion function for lock statements
        Func<LockStatementSyntax, SyntaxNode> convertLock = (lockStmt) =>
        {
            var bodyStatements = lockStmt.Statement is BlockSyntax blk
                ? blk.Statements
                : SyntaxFactory.SingletonList<StatementSyntax>(lockStmt.Statement);

            var waitAsync = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AwaitExpression(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(semaphoreName),
                            SyntaxFactory.IdentifierName("WaitAsync")))));

            var release = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(semaphoreName),
                        SyntaxFactory.IdentifierName("Release"))));

            var tryFinally = SyntaxFactory.TryStatement(
                SyntaxFactory.Block(bodyStatements),
                new SyntaxList<CatchClauseSyntax>(),
                SyntaxFactory.FinallyClause(SyntaxFactory.Block(release)));

            return SyntaxFactory.Block(waitAsync, tryFinally);
        };

        // Replace all lock statements in the type
        var newTypeNode = typeNode.ReplaceNodes(allLockStatementsInType, (old, _) => convertLock((LockStatementSyntax)old));

        // Find method SIGNATURES that contain locks (before replacement, using original typeNode).
        // Using (name, parameterList) tuples avoids the overload bug where method-name-only matching
        // would make ALL overloads async even when only one overload actually contained a lock.
        var methodSignaturesWithLocks = typeNode.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body?.DescendantNodes().OfType<LockStatementSyntax>().Any(ls => ls.Expression.ToString().Trim() == lockExpression) ?? false)
            .Select(m => (Name: m.Identifier.Text, Params: m.ParameterList.ToString()))
            .ToHashSet();

        // Make ONLY the methods that contained locks async (match by name + parameters, not name alone)
        var methodsInNewType = newTypeNode.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        foreach (var meth in methodsInNewType)
        {
            if (methodSignaturesWithLocks.Contains((meth.Identifier.Text, meth.ParameterList.ToString())) && !meth.Modifiers.Any(SyntaxKind.AsyncKeyword))
            {
                var newMeth = meth.AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
                var retStr = newMeth.ReturnType.ToString().Trim();
                if (retStr == "void")
                    newMeth = newMeth.WithReturnType(SyntaxFactory.ParseTypeName("Task "));
                else if (!retStr.StartsWith("Task"))
                    newMeth = newMeth.WithReturnType(SyntaxFactory.ParseTypeName($"Task<{retStr}> "));
                
                newTypeNode = newTypeNode.ReplaceNode(meth, newMeth);
            }
        }

        if (!hasSemaphore)
        {
            // If every method that contains this lock is static, the semaphore must also be static
            // so that the static methods can access it without an instance.
            bool isStaticContext = typeNode.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Body?.DescendantNodes().OfType<LockStatementSyntax>()
                    .Any(ls => ls.Expression.ToString().Trim() == lockExpression) ?? false)
                .All(m => m.Modifiers.Any(SyntaxKind.StaticKeyword));

            var semaphoreField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("SemaphoreSlim"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(semaphoreName)
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("SemaphoreSlim"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1))),
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)))
                        }))))))))
            .AddModifiers(isStaticContext
                ? new[] { SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword) }
                : new[] { SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword) });

            newTypeNode = newTypeNode.InsertNodesBefore(newTypeNode.Members.First(), new[] { semaphoreField });
        }

        var newRoot = root.ReplaceNode(typeNode, newTypeNode);
        return newRoot.NormalizeWhitespace().ToFullString();
        }
        catch (Exception ex)
        {
            return $"// Error converting lock to SemaphoreSlim: {ex.Message}";
        }
    }
}
