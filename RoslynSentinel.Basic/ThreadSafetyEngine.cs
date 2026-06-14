using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Basic;

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
    public async Task<DocumentEditResult> MakeMethodThreadSafeAsync(FilePath filePath, string methodName, string lockFieldName = "_lock", CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: File '{filePath}' not found."
                };
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: Failed to get syntax root for '{filePath}'."
                };
            }

            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
            if (method == null || method.Body == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: Method '{methodName}' not found or has no body."
                };
            }

            var typeNode = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeNode == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Error: Containing type not found for method '{methodName}'."
                };
            }

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
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.TargetNotFound,
                        FilePath = filePath,
                        Message = $"// Error: Field '{lockFieldName}' already exists but is not of type 'object'. Please supply a different lockFieldName."
                    };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = "// Lock statement converted to SemaphoreSlim pattern.",
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
            };
        }
        catch (Exception ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Error,
                FilePath = filePath,
                Message = $"// Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Converts lock statements inside a method and ALL other methods to async-safe SemaphoreSlim pattern.
    /// Adds a SemaphoreSlim field and replaces all lock statements with await _semaphore.WaitAsync() + try/finally.
    /// </summary>
    public async Task<DocumentEditResult> ConvertLockToSemaphoreSlimAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                throw new InvalidOperationException($"Failed to get syntax root for file: {filePath}");
            }

            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
            if (method == null || method.Body == null)
            {
                throw new InvalidOperationException($"Method or body not found: {methodName} in file: {filePath}");
            }

            var typeNode = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeNode == null)
            {
                throw new InvalidOperationException($"Type not found for method: {methodName} in file: {filePath}");
            }

            // Find the lock object being used in this method
            var lockStatements = method.Body.DescendantNodes().OfType<LockStatementSyntax>().ToList();
            if (lockStatements.Count == 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = root.ToFullString()
                };
            }

            // Get the lock object identifier (e.g., "_lock")
            var lockExpression = lockStatements.First().Expression.ToString().Trim();

            // Find ALL lock statements in the entire type that use the same lock object
            var allLockStatementsInType = typeNode.DescendantNodes().OfType<LockStatementSyntax>()
                .Where(ls => ls.Expression.ToString().Trim() == lockExpression)
                .ToList();

            if (allLockStatementsInType.Count == 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = root.ToFullString()
                };
            }

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
                    {
                        newMeth = newMeth.WithReturnType(SyntaxFactory.ParseTypeName("Task "));
                    }
                    else if (!retStr.StartsWith("Task"))
                    {
                        newMeth = newMeth.WithReturnType(SyntaxFactory.ParseTypeName($"Task<{retStr}> "));
                    }

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = "// Lock statement converted to SemaphoreSlim pattern.",
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
            };
        }
        catch (Exception ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Error,
                FilePath = filePath,
                Message = $"// Error converting lock to SemaphoreSlim: {ex.Message}"
            };
        }
    }

    // ── UnsafeLazyInit ────────────────────────────────────────────────────────

    /// <summary>
    /// Detects the unsafe double-check lazy init pattern without volatile or lock:
    ///   if (_field == null) { _field = new X(); }
    /// Without volatile, a second thread may observe the partially-initialized object
    /// or both threads may initialise and one write gets lost.
    /// Correct alternatives: Lazy&lt;T&gt;, lock, Interlocked.CompareExchange.
    /// </summary>
    public async Task<List<string>> FindUnsafeLazyInitAsync(
        string? projectName = null, string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();

        IEnumerable<Document> docs;
        if (!string.IsNullOrEmpty(filePath))
        {
            docs = solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            docs = solution.Projects.Where(p => p.Name.Contains(projectName!, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents);
        }
        else
        {
            docs = solution.Projects.SelectMany(p => p.Documents);
        }

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            // Collect field names that are marked volatile
            var volatileFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword)))
                {
                    foreach (var v in field.Declaration.Variables)
                    {
                        volatileFields.Add(v.Identifier.Text);
                    }
                }
            }

            foreach (var ifStmt in root.DescendantNodes().OfType<IfStatementSyntax>())
            {
                // Pattern: if (_field == null) or if (null == _field)
                string? fieldName = null;
                if (ifStmt.Condition is BinaryExpressionSyntax bin &&
                    bin.IsKind(SyntaxKind.EqualsExpression))
                {
                    if (bin.Left is IdentifierNameSyntax lid &&
                        bin.Right is LiteralExpressionSyntax rn && rn.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        fieldName = lid.Identifier.Text;
                    }
                    else if (bin.Right is IdentifierNameSyntax rid &&
                             bin.Left is LiteralExpressionSyntax ln && ln.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        fieldName = rid.Identifier.Text;
                    }
                }
                // Also: if (_field is null)
                else if (ifStmt.Condition is IsPatternExpressionSyntax isPat &&
                         isPat.Expression is IdentifierNameSyntax isId &&
                         isPat.Pattern is ConstantPatternSyntax cp &&
                         cp.Expression is LiteralExpressionSyntax cpl &&
                         cpl.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    fieldName = isId.Identifier.Text;
                }

                if (fieldName == null)
                {
                    continue;
                }

                // The then-branch must assign the same field
                bool thenAssigns = ifStmt.Statement.DescendantNodesAndSelf()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(a => a.Left is IdentifierNameSyntax ai && ai.Identifier.Text == fieldName &&
                              a.Right is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax);
                if (!thenAssigns)
                {
                    continue;
                }

                // Skip if inside a lock statement
                bool insideLock = ifStmt.Ancestors().OfType<LockStatementSyntax>().Any();
                if (insideLock)
                {
                    continue;
                }

                // Skip if field is volatile (volatile reads/writes are atomic for reference types)
                if (volatileFields.Contains(fieldName))
                {
                    continue;
                }

                // Skip Lazy<T> wrappers
                var containingClass = ifStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingClass != null)
                {
                    bool isLazyField = containingClass.Members.OfType<FieldDeclarationSyntax>()
                        .Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName) &&
                                  f.Declaration.Type.ToString().StartsWith("Lazy"));
                    if (isLazyField)
                    {
                        continue;
                    }
                }

                var loc = ifStmt.GetLocation().GetLineSpan().StartLinePosition;
                results.Add(
                    $"{fp}:{loc.Line + 1} - Unsafe lazy init: '{fieldName}' is checked for null and then " +
                    $"assigned without a lock or volatile. Two threads may both see null and both initialise. " +
                    $"Use Lazy<T>, lock, or Interlocked.CompareExchange instead.");
            }
        }
        return results;
    }

    // ── CasLoopWithoutBackoff ─────────────────────────────────────────────────

    /// <summary>
    /// Detects Interlocked.CompareExchange retry loops (while/do-while) that spin without
    /// any Thread.Sleep, Task.Delay, Thread.SpinWait, or SpinWait.SpinOnce call.
    /// Without back-off, a failed CAS retry busy-loops and can peg a core at 100% CPU
    /// (live-lock). Use SpinWait.SpinOnce() between retries.
    /// </summary>
    public async Task<List<string>> FindCasLoopWithoutBackoffAsync(
        string? projectName = null, string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();

        IEnumerable<Document> docs;
        if (!string.IsNullOrEmpty(filePath))
        {
            docs = solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            docs = solution.Projects.Where(p => p.Name.Contains(projectName!, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents);
        }
        else
        {
            docs = solution.Projects.SelectMany(p => p.Documents);
        }

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            var loops = root.DescendantNodes()
                .Where(n => n is WhileStatementSyntax or DoStatementSyntax);

            foreach (var loop in loops)
            {
                // Must contain Interlocked.CompareExchange
                bool hasCas = loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                                ma.Expression.ToString() == "Interlocked" &&
                                ma.Name.Identifier.Text == "CompareExchange");
                if (!hasCas)
                {
                    continue;
                }

                // Check for any back-off call.
                // SpinWait is commonly used as an instance: `var spin = new SpinWait(); spin.SpinOnce();`
                // so we match on the method name "SpinOnce" regardless of receiver name.
                bool hasBackoff = loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(inv =>
                    {
                        if (inv.Expression is not MemberAccessExpressionSyntax ma)
                        {
                            return false;
                        }

                        var receiver = ma.Expression.ToString();
                        var name = ma.Name.Identifier.Text;
                        return (receiver is "Thread" && name is "Sleep" or "SpinWait" or "Yield") ||
                               (receiver is "SpinWait" && name == "SpinOnce") ||
                               name == "SpinOnce" || // instance: spin.SpinOnce()
                               (receiver is "Task" && name == "Delay");
                    });

                // Also accept SpinWait struct usage
                bool hasSpinWait = loop.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.Text == "SpinWait");

                if (!hasBackoff && !hasSpinWait)
                {
                    var loc = loop.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add(
                        $"{fp}:{loc.Line + 1} - CAS retry loop (Interlocked.CompareExchange) without back-off. " +
                        "A spinning loop with no sleep/yield can peg a CPU core at 100%% (live-lock). " +
                        "Add SpinWait.SpinOnce() between retries.");
                }
            }
        }
        return results;
    }

    // ── DoubleCheckedLockingWithoutVolatile ───────────────────────────────────
    // The double-checked locking (DCL) pattern is only safe when the checked field is
    // declared `volatile`. Without it, a CPU or JIT may reorder the store and expose a
    // partially-constructed object to the outer null-check on another thread.

    /// <summary>
    /// Detects the double-checked locking pattern where the lazily-initialized field is not
    /// declared volatile. Looks for: if (field == null) { lock(x) { if (field == null) { field = new X(); }}}
    /// </summary>
    public async Task<List<string>> FindDoubleCheckedLockingAsync(
        string? projectName = null, string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();

        IEnumerable<Document> docs;
        if (!string.IsNullOrEmpty(filePath))
        {
            docs = solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            docs = solution.Projects.Where(p => p.Name.Contains(projectName!, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents);
        }
        else
        {
            docs = solution.Projects.SelectMany(p => p.Documents);
        }

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                // Collect volatile fields in this class
                var volatileFields = classDecl.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var outerIf in classDecl.DescendantNodes().OfType<IfStatementSyntax>())
                {
                    // Outer if must check field == null or field is null
                    string? checkedField = ExtractNullCheckedIdentifier(outerIf.Condition);
                    if (checkedField == null)
                    {
                        continue;
                    }

                    // Outer if body must directly contain a lock statement
                    var lockStmt = outerIf.Statement is BlockSyntax outerBlock
                        ? outerBlock.Statements.OfType<LockStatementSyntax>().FirstOrDefault()
                        : outerIf.Statement as LockStatementSyntax;
                    if (lockStmt == null)
                    {
                        continue;
                    }

                    // Lock body must contain an inner if checking the same field
                    var innerIf = lockStmt.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault();
                    if (innerIf == null)
                    {
                        continue;
                    }

                    if (ExtractNullCheckedIdentifier(innerIf.Condition) != checkedField)
                    {
                        continue;
                    }

                    // Inner if body must assign to that field
                    bool assignsField = innerIf.Statement.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Any(a => a.Left is IdentifierNameSyntax id && id.Identifier.Text == checkedField);
                    if (!assignsField)
                    {
                        continue;
                    }

                    // Only flag if the field is not volatile
                    if (volatileFields.Contains(checkedField))
                    {
                        continue;
                    }

                    var loc = outerIf.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add(
                        $"{fp}:{loc.Line + 1} - Double-checked locking on '{checkedField}' without 'volatile'. " +
                        "Without volatile, the CPU or JIT may reorder the store and expose a partially-constructed " +
                        "object to another thread's outer null-check. Declare the field volatile or use Lazy<T>.");
                }
            }
        }
        return results;
    }

    // ── CheckThenActOnDictionary ──────────────────────────────────────────────
    // ContainsKey(k) → Add(k, v) is a classic check-then-act race: another thread
    // may insert the same key between the check and the add, causing a duplicate-key
    // exception or silent data loss. Use GetOrAdd / TryAdd instead.

    private static readonly HashSet<string> DictionaryCheckMethods = new(StringComparer.Ordinal)
    {
        "ContainsKey", "TryGetValue"
    };

    private static readonly HashSet<string> DictionaryAddMethods = new(StringComparer.Ordinal)
    {
        "Add", "TryAdd"
    };

    /// <summary>
    /// Detects the check-then-act race on Dictionary / ConcurrentDictionary: ContainsKey or
    /// TryGetValue followed by Add/TryAdd on the same variable, outside of any lock, without
    /// using the atomic GetOrAdd overload.
    /// </summary>
    public async Task<List<string>> FindCheckThenActOnDictionaryAsync(
        string? projectName = null, string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();

        IEnumerable<Document> docs;
        if (!string.IsNullOrEmpty(filePath))
        {
            docs = solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            docs = solution.Projects.Where(p => p.Name.Contains(projectName!, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents);
        }
        else
        {
            docs = solution.Projects.SelectMany(p => p.Documents);
        }

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body == null)
                {
                    continue;
                }

                // Collect all invocations in order
                var invocations = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

                for (int i = 0; i < invocations.Count - 1; i++)
                {
                    if (invocations[i].Expression is not MemberAccessExpressionSyntax checkMa)
                    {
                        continue;
                    }

                    if (!DictionaryCheckMethods.Contains(checkMa.Name.Identifier.Text))
                    {
                        continue;
                    }

                    var dictName = checkMa.Expression.ToString();

                    // Look for a subsequent Add/TryAdd on the same receiver
                    for (int j = i + 1; j < invocations.Count; j++)
                    {
                        if (invocations[j].Expression is not MemberAccessExpressionSyntax addMa)
                        {
                            continue;
                        }

                        if (!DictionaryAddMethods.Contains(addMa.Name.Identifier.Text))
                        {
                            continue;
                        }

                        if (addMa.Expression.ToString() != dictName)
                        {
                            continue;
                        }

                        // Both check and add must NOT be inside a LockStatementSyntax
                        bool checkInLock = invocations[i].Ancestors().OfType<LockStatementSyntax>().Any();
                        bool addInLock = invocations[j].Ancestors().OfType<LockStatementSyntax>().Any();
                        if (checkInLock && addInLock)
                        {
                            continue;
                        }

                        var loc = invocations[i].GetLocation().GetLineSpan().StartLinePosition;
                        results.Add(
                            $"{fp}:{loc.Line + 1} - Check-then-act race on '{dictName}': " +
                            $"{checkMa.Name.Identifier.Text}() followed by {addMa.Name.Identifier.Text}() without a lock. " +
                            "Another thread may insert the same key between the check and add. " +
                            "Use GetOrAdd() or TryAdd() for atomic single-step insertion.");
                        break; // one report per check
                    }
                }
            }
        }
        return results;
    }

    private static string? ExtractNullCheckedIdentifier(ExpressionSyntax condition)
    {
        // field == null
        if (condition is BinaryExpressionSyntax bin &&
            bin.IsKind(SyntaxKind.EqualsExpression) &&
            bin.Right is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression) &&
            bin.Left is IdentifierNameSyntax id1)
        {
            return id1.Identifier.Text;
        }

        // null == field
        if (condition is BinaryExpressionSyntax bin2 &&
            bin2.IsKind(SyntaxKind.EqualsExpression) &&
            bin2.Left is LiteralExpressionSyntax lit2 && lit2.IsKind(SyntaxKind.NullLiteralExpression) &&
            bin2.Right is IdentifierNameSyntax id2)
        {
            return id2.Identifier.Text;
        }

        // field is null
        if (condition is IsPatternExpressionSyntax isPattern &&
            isPattern.Expression is IdentifierNameSyntax id3 &&
            isPattern.Pattern is ConstantPatternSyntax cp &&
            cp.Expression is LiteralExpressionSyntax lit3 && lit3.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return id3.Identifier.Text;
        }

        return null;
    }
}
