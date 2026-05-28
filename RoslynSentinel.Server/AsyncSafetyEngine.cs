using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record AsyncSafetyReport(string FilePath, string MethodName, string Reason);

public class AsyncSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AsyncSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<AsyncSafetyReport>> DetectAsyncVoidMethodsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath) 
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)) && m.ReturnType.ToString() == "void");

            foreach (var method in methods)
            {
                // Event handler exclusion: (object sender, *EventArgs* e) is the conventional
                // async void pattern for UI events — flag with advisory rather than crash warning.
                if (IsEventHandlerSignature(method))
                {
                    reports.Add(new AsyncSafetyReport(
                        document.FilePath ?? document.Name,
                        method.Identifier.Text,
                        "Async void event handler: this is the only acceptable use of async void. " +
                        "Exceptions still crash the process — wrap the body in try/catch if exceptions are possible."));
                }
                else
                {
                    reports.Add(new AsyncSafetyReport(
                        document.FilePath ?? document.Name,
                        method.Identifier.Text,
                        "Async void methods cannot be awaited and crash the process on exceptions. Change return type to Task."));
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskYieldUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var yieldCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression.ToString() == "Task"
                    && ma.Name.Identifier.Text == "Yield");

            foreach (var call in yieldCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Yield()' forces an async context switch — verify it is intentional and not a workaround."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var delayCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression.ToString() == "Task"
                    && ma.Name.Identifier.Text == "Delay");

            foreach (var call in delayCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Delay(...)' found. Prefer CancellationToken overloads; avoid polling-with-delay patterns."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayZeroUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var delayZeroCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv =>
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma
                        || ma.Expression.ToString() != "Task"
                        || ma.Name.Identifier.Text != "Delay")
                    {
                        return false;
                    }

                    var args = inv.ArgumentList.Arguments;
                    if (args.Count == 0)
                    {
                        return false;
                    }

                    var first = args[0].Expression;
                    if (first is LiteralExpressionSyntax lit && lit.Token.Text == "0")
                    {
                        return true;
                    }

                    if (first is MemberAccessExpressionSyntax mts
                        && mts.Expression.ToString() == "TimeSpan"
                        && mts.Name.Identifier.Text == "Zero")
                    {
                        return true;
                    }

                    return false;
                });

            foreach (var call in delayZeroCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Delay(0)' found. Use 'await Task.Yield()' to yield the thread to the thread pool if that is the intent."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskWhenAllUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            // Detect methods with 2+ sequential independent awaits that could be parallelized with Task.WhenAll
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var block in method.DescendantNodes().OfType<BlockSyntax>())
                {
                    // Collect all await statements from the block
                    var awaitStatements = block.Statements
                        .Where(s =>
                            (s is ExpressionStatementSyntax e && e.Expression is AwaitExpressionSyntax) ||
                            (s is LocalDeclarationStatementSyntax ld &&
                             ld.Declaration.Variables.Count == 1 &&
                             ld.Declaration.Variables[0].Initializer?.Value is AwaitExpressionSyntax))
                        .ToList();

                    if (awaitStatements.Count < 2)
                    {
                        continue;
                    }

                    // Count independent consecutive pairs
                    int independentPairs = 0;
                    for (int idx = 0; idx < awaitStatements.Count - 1; idx++)
                    {
                        var first = awaitStatements[idx];
                        var second = awaitStatements[idx + 1];

                        // Extract declared variable names from the first statement
                        var declaredVarNames = new HashSet<string>();
                        if (first is LocalDeclarationStatementSyntax firstLocal)
                        {
                            foreach (var v in firstLocal.Declaration.Variables)
                            {
                                declaredVarNames.Add(v.Identifier.Text);
                            }
                        }

                        // Extract the second await expression
                        ExpressionSyntax? secondAwaitExpression = null;
                        if (second is ExpressionStatementSyntax secondExpr &&
                            secondExpr.Expression is AwaitExpressionSyntax secondAwait1)
                        {
                            secondAwaitExpression = secondAwait1.Expression;
                        }
                        else if (second is LocalDeclarationStatementSyntax secondLocal &&
                                 secondLocal.Declaration.Variables[0].Initializer?.Value is AwaitExpressionSyntax secondAwait2)
                        {
                            secondAwaitExpression = secondAwait2.Expression;
                        }

                        if (secondAwaitExpression == null)
                        {
                            continue;
                        }

                        // Check if the second await depends on any variable declared in the first
                        bool dependent = secondAwaitExpression
                            .DescendantNodes()
                            .OfType<IdentifierNameSyntax>()
                            .Any(id => declaredVarNames.Contains(id.Identifier.Text));

                        if (!dependent)
                        {
                            independentPairs++;
                        }
                    }

                    if (independentPairs >= 1)
                    {
                        var lineSpan = method.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Method has {awaitStatements.Count} sequential awaits with independent pairs. Consider Task.WhenAll() for parallelism."
                        ));
                        break;
                    }
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindConfigureAwaitMissingAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        var appSuffixes = new[] { "Controller", "Hub", "PageModel", "ViewModel" };

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                var expr = awaitExpr.Expression;
                if (expr is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.Text == "ConfigureAwait")
                {
                    continue;
                }

                var containingClass = awaitExpr.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (containingClass != null)
                {
                    var className = containingClass.Identifier.Text;
                    if (appSuffixes.Any(s => className.EndsWith(s)))
                    {
                        continue;
                    }
                }

                var method = awaitExpr.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = awaitExpr.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: Await missing .ConfigureAwait(false) — add for library code or remove for ASP.NET app code."));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindBlockingCallsInAsyncAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var asyncMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)));

            foreach (var method in asyncMethods)
            {
                foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is MemberAccessExpressionSyntax maSleep &&
                        maSleep.Expression.ToString().EndsWith("Thread") &&
                        maSleep.Name.Identifier.Text == "Sleep")
                    {
                        var lineSpan = inv.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Thread.Sleep() in async method — use 'await Task.Delay()' instead."));
                    }

                    if (inv.Expression is MemberAccessExpressionSyntax maGetResult &&
                        maGetResult.Name.Identifier.Text == "GetResult" &&
                        maGetResult.Expression is InvocationExpressionSyntax innerInv &&
                        innerInv.Expression is MemberAccessExpressionSyntax maGetAwaiter &&
                        maGetAwaiter.Name.Identifier.Text == "GetAwaiter")
                    {
                        var lineSpan = inv.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: .GetAwaiter().GetResult() in async method — use 'await' instead."));
                    }

                    if (inv.Expression is MemberAccessExpressionSyntax maWait &&
                        maWait.Name.Identifier.Text == "Wait")
                    {
                        var lineSpan = inv.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: .Wait() in async method — use 'await' instead."));
                    }
                }

                foreach (var memberAccess in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (memberAccess.Name.Identifier.Text == "Result" &&
                        !(memberAccess.Parent is InvocationExpressionSyntax) &&
                        !(memberAccess.Parent is AssignmentExpressionSyntax lhsAssign && lhsAssign.Left == memberAccess))
                    {
                        var lineSpan = memberAccess.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: .Result accessed in async method — use 'await' instead."));
                    }
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindAsyncInConstructorAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                if (ctor.Body == null)
                {
                    continue;
                }

                var className = ctor.Identifier.Text;

                if (ctor.Body.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                {
                    var lineSpan = ctor.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, className,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: Constructor invokes async code — use factory method pattern or AsyncHelper."));
                    continue;
                }

                foreach (var invExpr in ctor.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var calledMethod = invExpr.Expression is MemberAccessExpressionSyntax maa
                        ? maa.Name.Identifier.Text
                        : invExpr.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;

                    if (calledMethod != null && calledMethod.EndsWith("Async"))
                    {
                        var lineSpan = invExpr.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, className,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Constructor invokes async code — use factory method pattern or AsyncHelper."));
                        break;
                    }
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskRunInAsyncAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                if (awaitExpr.Expression is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Expression.ToString().EndsWith("Task") &&
                    ma.Name.Identifier.Text == "Run")
                {
                    var method = awaitExpr.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var lineSpan = awaitExpr.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name,
                        method?.Identifier.Text ?? "<unknown>",
                        $"Line {lineSpan.StartLinePosition.Line + 1}: await Task.Run(...) in server code wastes thread pool threads. Prefer direct async/await or remove if the lambda is already async."));
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindConcurrentCollectionOpportunitiesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var lockStmt in root.DescendantNodes().OfType<LockStatementSyntax>())
            {
                var containingType = lockStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null)
                {
                    continue;
                }

                var collectionFields = containingType.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f =>
                    {
                        var typeName = f.Declaration.Type.ToString();
                        return typeName.Contains("List<") || typeName.Contains("Dictionary<");
                    })
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet();

                var identifiers = lockStmt.Statement.DescendantNodes().OfType<IdentifierNameSyntax>();
                foreach (var id in identifiers)
                {
                    if (collectionFields.Contains(id.Identifier.Text))
                    {
                        var method = lockStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var lineSpan = lockStmt.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name,
                            method?.Identifier.Text ?? "<unknown>",
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Consider replacing List<T>/Dictionary<K,V> + lock with ConcurrentDictionary<K,V> or ImmutableDictionary."));
                        break;
                    }
                }
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindUnsafeLazyInitAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            // Pattern 1: double-checked locking without volatile
            foreach (var outerIf in root.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (!IsNullCheck(outerIf.Condition, out var checkedName))
                {
                    continue;
                }

                var lockStmt = outerIf.Statement is BlockSyntax b1
                    ? b1.Statements.OfType<LockStatementSyntax>().FirstOrDefault()
                    : outerIf.Statement as LockStatementSyntax;
                if (lockStmt == null)
                {
                    continue;
                }

                var innerIf = lockStmt.Statement is BlockSyntax b2
                    ? b2.Statements.OfType<IfStatementSyntax>().FirstOrDefault()
                    : lockStmt.Statement as IfStatementSyntax;
                if (innerIf == null)
                {
                    continue;
                }

                if (!IsNullCheck(innerIf.Condition, out var innerName))
                {
                    continue;
                }

                if (checkedName != innerName)
                {
                    continue;
                }

                var containingType = outerIf.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null)
                {
                    continue;
                }

                var isVolatile = containingType.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Declaration.Variables.Any(v => v.Identifier.Text == checkedName))
                    .Any(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword)));

                if (!isVolatile)
                {
                    var lineSpan = outerIf.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name,
                        containingType.Identifier.Text,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: Double-checked locking without volatile — field may be partially initialized. Use Lazy<T> or volatile."));
                }
            }

            // Pattern 2: unguarded null initialization (if (field == null) field = new T(); without lock)
            foreach (var ifStmt in root.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (!IsNullCheck(ifStmt.Condition, out var name))
                {
                    continue;
                }

                if (ifStmt.Statement.DescendantNodes().OfType<LockStatementSyntax>().Any())
                {
                    continue;
                }
                // Not inside a lock
                if (ifStmt.Ancestors().OfType<LockStatementSyntax>().Any())
                {
                    continue;
                }

                var innerStmts = ifStmt.Statement is BlockSyntax blk
                    ? blk.Statements
                    : new SyntaxList<StatementSyntax>().Add(ifStmt.Statement);

                var hasAssignment = innerStmts.OfType<ExpressionStatementSyntax>()
                    .Any(s => s.Expression is AssignmentExpressionSyntax assign &&
                              assign.Left is IdentifierNameSyntax leftId &&
                              leftId.Identifier.Text == name);

                if (!hasAssignment)
                {
                    continue;
                }

                var containingType = ifStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null)
                {
                    continue;
                }

                var isField = containingType.Members.OfType<FieldDeclarationSyntax>()
                    .Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == name));

                if (isField)
                {
                    var lineSpan = ifStmt.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name,
                        containingType.Identifier.Text,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: Double-checked locking without volatile — field may be partially initialized. Use Lazy<T> or volatile."));
                }
            }
        }
        return reports;
    }

    private static bool IsNullCheck(ExpressionSyntax condition, out string fieldName)
    {
        fieldName = string.Empty;

        if (condition is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.EqualsExpression))
        {
            if (bin.Left is IdentifierNameSyntax left &&
                bin.Right is LiteralExpressionSyntax rightLit &&
                rightLit.IsKind(SyntaxKind.NullLiteralExpression))
            {
                fieldName = left.Identifier.Text;
                return true;
            }
            if (bin.Right is IdentifierNameSyntax right &&
                bin.Left is LiteralExpressionSyntax leftLit &&
                leftLit.IsKind(SyntaxKind.NullLiteralExpression))
            {
                fieldName = right.Identifier.Text;
                return true;
            }
        }

        if (condition is IsPatternExpressionSyntax isPattern &&
            isPattern.Expression is IdentifierNameSyntax id &&
            isPattern.Pattern is ConstantPatternSyntax cps &&
            cps.Expression is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.NullLiteralExpression))
        {
            fieldName = id.Identifier.Text;
            return true;
        }

        return false;
    }

    public async Task<List<AsyncSafetyReport>> DetectValueTaskMisuseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            SemanticModel? semanticModel = null;
            try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

            var docPath = document.FilePath ?? document.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodName = method.Identifier.Text;
                if (method.Body == null && method.ExpressionBody == null)
                {
                    continue;
                }

                // Pattern A: double await on same variable
                var awaitedIdentifiers = method.DescendantNodes()
                    .OfType<AwaitExpressionSyntax>()
                    .Select(a => a.Expression)
                    .OfType<IdentifierNameSyntax>()
                    .Select(id => id.Identifier.Text)
                    .ToList();

                var seen = new HashSet<string>();
                foreach (var name in awaitedIdentifiers)
                {
                    if (!seen.Add(name))
                    {
                        var line = method.DescendantNodes()
                            .OfType<AwaitExpressionSyntax>()
                            .Where(a => a.Expression is IdentifierNameSyntax id2 && id2.Identifier.Text == name)
                            .Skip(1).FirstOrDefault()?.GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? 0;
                        reports.Add(new AsyncSafetyReport(docPath, methodName,
                            $"Line {line}: ValueTask variable '{name}' is awaited more than once. ValueTask may only be awaited once."));
                    }
                }

                // Collect local declarations of ValueTask type
                var valueTaskLocals = new Dictionary<string, int>(); // name -> statement index in containing block

                // Pattern B + C + D: walk all statements in the method body
                var statements = method.Body?.Statements.ToList()
                    ?? new List<StatementSyntax>();

                for (int i = 0; i < statements.Count; i++)
                {
                    var stmt = statements[i];

                    // Check for ValueTask local declarations
                    if (stmt is LocalDeclarationStatementSyntax localDecl)
                    {
                        foreach (var variable in localDecl.Declaration.Variables)
                        {
                            var varName = variable.Identifier.Text;
                            var isValueTask = false;

                            // Semantic check
                            if (semanticModel != null && variable.Initializer?.Value != null)
                            {
                                var typeInfo = semanticModel.GetTypeInfo(variable.Initializer.Value, cancellationToken);
                                var typeName = typeInfo.Type?.ToDisplayString() ?? "";
                                isValueTask = typeName == "System.Threading.Tasks.ValueTask" ||
                                              typeName.StartsWith("System.Threading.Tasks.ValueTask<");
                            }

                            // Syntactic fallback: declared type is ValueTask or ValueTask<T>
                            if (!isValueTask)
                            {
                                var declaredType = localDecl.Declaration.Type.ToString().Trim();
                                isValueTask = declaredType == "ValueTask" || declaredType.StartsWith("ValueTask<");
                            }

                            if (isValueTask)
                            {
                                // Check if initializer is already awaited directly (fine)
                                var initIsDirectAwait = variable.Initializer?.Value is AwaitExpressionSyntax;
                                if (!initIsDirectAwait)
                                {
                                    valueTaskLocals[varName] = i;
                                }
                            }
                        }
                    }
                }

                // For each ValueTask local, check for deferred await (Pattern B)
                foreach (var (varName, declIdx) in valueTaskLocals)
                {
                    for (int j = declIdx + 1; j < statements.Count; j++)
                    {
                        var stmt = statements[j];
                        var awaitsHere = stmt.DescendantNodes()
                            .OfType<AwaitExpressionSyntax>()
                            .Any(a => a.Expression is IdentifierNameSyntax id3 && id3.Identifier.Text == varName);

                        if (awaitsHere && j > declIdx + 1)
                        {
                            var lineNo = stmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            reports.Add(new AsyncSafetyReport(docPath, methodName,
                                $"Line {lineNo}: ValueTask '{varName}' stored and awaited deferred (with intervening statements). ValueTask may be consumed — use Task/await directly or .AsTask()."));
                            break;
                        }
                    }
                }

                // Pattern C: Task.WhenAll(valueTaskVar)
                foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax ma &&
                        ma.Expression.ToString() == "Task" &&
                        ma.Name.Identifier.Text == "WhenAll")
                    {
                        foreach (var arg in invocation.ArgumentList.Arguments)
                        {
                            var argName = arg.Expression is IdentifierNameSyntax argId ? argId.Identifier.Text : null;
                            var isVtArg = false;

                            if (semanticModel != null)
                            {
                                var ti = semanticModel.GetTypeInfo(arg.Expression, cancellationToken);
                                var tn = ti.Type?.ToDisplayString() ?? "";
                                isVtArg = tn == "System.Threading.Tasks.ValueTask" || tn.StartsWith("System.Threading.Tasks.ValueTask<");
                            }
                            else if (argName != null && valueTaskLocals.ContainsKey(argName))
                            {
                                isVtArg = true;
                            }

                            if (isVtArg)
                            {
                                var lineNo = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                reports.Add(new AsyncSafetyReport(docPath, methodName,
                                    $"Line {lineNo}: ValueTask passed to Task.WhenAll(). ValueTask is not a Task — call .AsTask() first."));
                            }
                        }
                    }
                }

                // Pattern D: .Result on ValueTask
                foreach (var memberAccess in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (memberAccess.Name.Identifier.Text != "Result")
                    {
                        continue;
                    }

                    var isVt = false;
                    if (semanticModel != null)
                    {
                        var ti = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken);
                        var tn = ti.Type?.ToDisplayString() ?? "";
                        isVt = tn == "System.Threading.Tasks.ValueTask" || tn.StartsWith("System.Threading.Tasks.ValueTask<");
                    }
                    else if (memberAccess.Expression is IdentifierNameSyntax rid && valueTaskLocals.ContainsKey(rid.Identifier.Text))
                    {
                        isVt = true;
                    }

                    if (isVt)
                    {
                        var lineNo = memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        reports.Add(new AsyncSafetyReport(docPath, methodName,
                            $"Line {lineNo}: .Result accessed on ValueTask. This is undefined behavior if the ValueTask is not yet completed — await it instead."));
                    }
                }
            }
        }

        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindAsyncOverSyncAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        var noOpTargets = new HashSet<string> { "FromResult", "CompletedTask" };

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword))))
            {
                if (method.Body == null && method.ExpressionBody == null)
                {
                    continue;
                }

                var awaitExprs = method.DescendantNodes().OfType<AwaitExpressionSyntax>().ToList();

                if (awaitExprs.Count == 0)
                {
                    var lineSpan = method.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(
                        document.FilePath ?? document.Name,
                        method.Identifier.Text,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: async method contains no await — remove async keyword and return Task.FromResult()/Task.CompletedTask directly."));
                    continue;
                }

                // Check if ALL awaits are no-ops (Task.FromResult, Task.CompletedTask, ValueTask.FromResult)
                bool allNoOp = awaitExprs.All(a =>
                {
                    if (a.Expression is InvocationExpressionSyntax inv &&
                        inv.Expression is MemberAccessExpressionSyntax ma &&
                        noOpTargets.Contains(ma.Name.Identifier.Text) &&
                        (ma.Expression.ToString() == "Task" || ma.Expression.ToString() == "ValueTask"))
                    {
                        return true;
                    }

                    if (a.Expression is MemberAccessExpressionSyntax directMa &&
                        directMa.Name.Identifier.Text == "CompletedTask" &&
                        directMa.Expression.ToString() == "Task")
                    {
                        return true;
                    }

                    return false;
                });

                if (allNoOp)
                {
                    var lineSpan = method.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(
                        document.FilePath ?? document.Name,
                        method.Identifier.Text,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: async method only awaits Task.FromResult/Task.CompletedTask — remove async keyword and return directly."));
                }
            }
        }
        return reports;
    }

    /// <summary>
    /// Returns true when a method matches the conventional event-handler signature:
    /// exactly 2 parameters where the first is <c>object</c> (or <c>object?</c>) and the
    /// second type name ends with <c>EventArgs</c>.
    /// </summary>
    /// <summary>
    /// EPC31: Finds async methods that have a CancellationToken parameter but call awaitable
    /// methods without forwarding the token. Only flags calls ending in "Async" that don't
    /// already receive the token as an argument.
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindCancellationTokenNotForwardedAsync(
        string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<AsyncSafetyReport>();

        IEnumerable<Document?> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents).Cast<Document?>()
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(solution.GetDocument);

        foreach (var doc in docs)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            // Get semantic model for this doc (optional — fall back to syntax heuristics if unavailable)
            SemanticModel? model = null;
            try { model = await doc.GetSemanticModelAsync(cancellationToken); } catch { }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                // Method must be async (or return Task/ValueTask)
                bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                bool returnsTask = !isAsync && method.ReturnType is GenericNameSyntax gn &&
                    (gn.Identifier.Text == "Task" || gn.Identifier.Text == "ValueTask");
                if (!isAsync && !returnsTask)
                {
                    continue;
                }

                // Method must have a CancellationToken parameter
                var ctParam = method.ParameterList.Parameters.FirstOrDefault(p =>
                {
                    var typeName = p.Type?.ToString() ?? "";
                    return typeName == "CancellationToken" || typeName.EndsWith(".CancellationToken");
                });
                if (ctParam == null)
                {
                    continue;
                }

                var ctParamName = ctParam.Identifier.Text;
                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    // Target must look like an async call
                    string? calleeName = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        IdentifierNameSyntax id => id.Identifier.Text,
                        _ => null
                    };
                    if (calleeName == null || !calleeName.EndsWith("Async", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Must be awaited to be a real async call site
                    bool isAwaited = invocation.Parent is AwaitExpressionSyntax ||
                        (invocation.Parent is MemberAccessExpressionSyntax chainMa &&
                         chainMa.Parent is InvocationExpressionSyntax chainCall &&
                         chainCall.Parent is AwaitExpressionSyntax);
                    if (!isAwaited)
                    {
                        continue;
                    }

                    // Check whether the CancellationToken param is already forwarded
                    var args = invocation.ArgumentList.Arguments;
                    bool alreadyForwarded = args.Any(a =>
                        a.Expression.ToString().Contains(ctParamName));
                    if (alreadyForwarded)
                    {
                        continue;
                    }

                    // Verify (via semantic model) that a CancellationToken overload exists for the callee.
                    // If no semantic model, use heuristic: assume any *Async method can accept one.
                    bool hasCancellableOverload = true;
                    if (model != null)
                    {
                        var si = model.GetSymbolInfo(invocation, cancellationToken);
                        var candidates = si.Symbol != null
                            ? new[] { si.Symbol }
                            : si.CandidateSymbols.ToArray();
                        hasCancellableOverload = candidates.OfType<IMethodSymbol>().Any() &&
                            invocation.Expression is MemberAccessExpressionSyntax callMa
                            ? (model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol ms &&
                               ms.ContainingType.GetMembers(ms.Name).OfType<IMethodSymbol>()
                                 .Any(overload => overload.Parameters.Any(p =>
                                     p.Type.ToDisplayString() == "System.Threading.CancellationToken")))
                            : true; // fallback: flag it
                    }

                    if (!hasCancellableOverload)
                    {
                        continue;
                    }

                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    reports.Add(new AsyncSafetyReport(fp, method.Identifier.Text,
                        $"Line {lineSpan.StartLinePosition.Line + 1}: '{calleeName}' is called without forwarding '{ctParamName}'. " +
                        $"Pass '{ctParamName}' as the last argument to propagate cancellation."));
                }
            }
        }
        return reports;
    }

    private static bool IsEventHandlerSignature(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
        {
            return false;
        }

        var firstType = parameters[0].Type?.ToString().TrimEnd('?') ?? "";
        if (firstType != "object")
        {
            return false;
        }

        var secondType = parameters[1].Type?.ToString() ?? "";
        // Strip nullable suffix and fully-qualified prefix, check tail
        var bare = secondType.TrimEnd('?');
        var dotIdx = bare.LastIndexOf('.');
        var simpleName = dotIdx >= 0 ? bare[(dotIdx + 1)..] : bare;
        return simpleName.EndsWith("EventArgs", StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds unawaited fire-and-forget Task calls including the <c>_ = MethodAsync()</c>
    /// discard-assignment pattern.
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindUnawaitedFireAndForgetAsync(
        string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath!).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            documents = solution.Projects
                .Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Documents).Cast<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            // Semantic model enables precise return-type checking instead of name-suffix heuristics.
            // Graceful fallback: if null (unresolvable project), the Async-suffix heuristic is used.
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            foreach (var exprStmt in root.DescendantNodes().OfType<ExpressionStatementSyntax>())
            {
                // Pattern A: Raw invocation without await — RunAsync();
                if (exprStmt.Expression is InvocationExpressionSyntax inv)
                {
                    if (exprStmt.Expression is AwaitExpressionSyntax)
                    {
                        continue;
                    }

                    string? methodName = null;
                    if (inv.Expression is MemberAccessExpressionSyntax ma)
                    {
                        methodName = ma.Name.Identifier.Text;
                    }
                    else if (inv.Expression is IdentifierNameSyntax id)
                    {
                        methodName = id.Identifier.Text;
                    }

                    bool isTaskReturning = IsTaskReturningSemantic(semanticModel, inv, cancellationToken)
                        ?? methodName?.EndsWith("Async", StringComparison.OrdinalIgnoreCase) == true;

                    if (isTaskReturning)
                    {
                        var containingMethod = exprStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var lineSpan = exprStmt.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            containingMethod?.Identifier.Text ?? "<unknown>",
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Task-returning method '{methodName ?? "<unknown>"}' called without await — exceptions will be swallowed silently."));
                    }
                    continue;
                }

                // Pattern B: Discard-assignment — _ = RunAsync();  OR  _ = _obj.RunAsync();
                if (exprStmt.Expression is AssignmentExpressionSyntax assign &&
                    assign.Left is IdentifierNameSyntax discardId &&
                    discardId.Identifier.Text == "_")
                {
                    string? methodName = null;
                    bool? semanticResult = null;

                    // B1: right side is a direct invocation (method call or member call)
                    if (assign.Right is InvocationExpressionSyntax discardInv)
                    {
                        if (discardInv.Expression is MemberAccessExpressionSyntax dma)
                        {
                            methodName = dma.Name.Identifier.Text;
                        }
                        else if (discardInv.Expression is IdentifierNameSyntax did)
                        {
                            methodName = did.Identifier.Text;
                        }

                        // Skip: task-chaining methods are the correct error-handling pattern.
                        // _ = DoWorkAsync().ContinueWith(errHandler, OnlyOnFaulted) is intentional.
                        if (methodName is "ContinueWith" or "Unwrap" or "ConfigureAwait" or "AsTask")
                        {
                            continue;
                        }

                        semanticResult = IsTaskReturningSemantic(semanticModel, discardInv, cancellationToken);
                    }
                    // B2: right side is a null-conditional call — _ = _obj?.RunAsync()
                    // or chained: _ = _obj?._svc?.RunAsync()
                    else if (assign.Right is ConditionalAccessExpressionSyntax cae)
                    {
                        methodName = ExtractTerminalAsyncMethodName(cae.WhenNotNull);
                        // Null-conditional returns T? — check the overall expression type (unwrap nullable)
                        semanticResult = IsTaskReturningSemantic(semanticModel, cae, cancellationToken);
                    }
                    // B3: right side is a ternary expression — _ = condition ? DoAAsync() : DoBAsync()
                    else if (assign.Right is ConditionalExpressionSyntax condExpr)
                    {
                        var thenName = ExtractDirectMethodName(condExpr.WhenTrue);
                        var elseName = ExtractDirectMethodName(condExpr.WhenFalse);
                        if (thenName?.EndsWith("Async", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            methodName = thenName;
                        }
                        else if (elseName?.EndsWith("Async", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            methodName = elseName;
                        }
                        // Check type of either branch
                        semanticResult = IsTaskReturningSemantic(semanticModel, condExpr.WhenTrue, cancellationToken)
                            ?? IsTaskReturningSemantic(semanticModel, condExpr.WhenFalse, cancellationToken);
                    }

                    bool isTaskReturning = semanticResult
                        ?? methodName?.EndsWith("Async", StringComparison.OrdinalIgnoreCase) == true;

                    if (isTaskReturning)
                    {
                        var containingMethod = exprStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var lineSpan = exprStmt.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            containingMethod?.Identifier.Text ?? "<unknown>",
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Task-returning method '{methodName ?? "<unknown>"}' fire-and-forgot via discard ('_ = ...') — exceptions will be swallowed silently. Use proper fire-and-forget with error logging instead."));
                    }
                }
            }
        }
        return reports;
    }

    // Returns true/false if the semantic model can resolve the return type; null if unresolvable (caller falls back).
    private static bool? IsTaskReturningSemantic(SemanticModel? model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (model == null)
        {
            return null;
        }

        var type = model.GetTypeInfo(expr, ct).Type;
        if (type == null)
        {
            return null; // unresolvable — let caller fall back to heuristic
        }

        return SemanticTypeHelper.IsTaskOrValueTask(type);
    }

    // Extracts a method name from a direct invocation or null-conditional call — used for B1/B3.
    private static string? ExtractDirectMethodName(ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax inv)
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                return ma.Name.Identifier.Text;
            }

            if (inv.Expression is IdentifierNameSyntax id)
            {
                return id.Identifier.Text;
            }
        }
        if (expr is ConditionalAccessExpressionSyntax cae)
        {
            return ExtractTerminalAsyncMethodName(cae.WhenNotNull);
        }

        return null;
    }

    // Recursively unwraps null-conditional access chains to find the terminal method name.
    // Handles: _obj?.RunAsync()  and chained: _obj?._svc?.RunAsync()
    private static string? ExtractTerminalAsyncMethodName(ExpressionSyntax whenNotNull)
    {
        // Terminal case: ?.RunAsync() — the WhenNotNull is an invocation via member binding
        if (whenNotNull is InvocationExpressionSyntax termInv &&
            termInv.Expression is MemberBindingExpressionSyntax termMb)
        {
            return termMb.Name.Identifier.Text;
        }

        // Chained case: ?._svc?.RunAsync() — WhenNotNull is another conditional access
        if (whenNotNull is ConditionalAccessExpressionSyntax innerCae)
        {
            return ExtractTerminalAsyncMethodName(innerCae.WhenNotNull);
        }

        return null;
    }

    // ── SequentialIndependentAwaits ───────────────────────────────────────────

    /// <summary>
    /// Detects sequences of two or more consecutive awaited calls whose result variables
    /// are independent (neither uses the other's variable) — missed parallelism opportunity.
    ///
    /// Example:  var x = await FetchAsync();   // could be parallel
    ///           var y = await GetAsync();      // neither uses x
    /// Suggest:  var (x, y) = await Task.WhenAll(FetchAsync(), GetAsync());
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindSequentialIndependentAwaitsAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<AsyncSafetyReport>();

        IEnumerable<Document?> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents).Cast<Document?>()
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(solution.GetDocument);

        foreach (var doc in docs)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                {
                    continue;
                }

                if (method.Body == null)
                {
                    continue;
                }

                var statements = method.Body.Statements;

                // Group consecutive independent awaits into one finding (avoids N-1 duplicate reports
                // for a block of N sequential awaits that could all be parallelised together).
                int stmtIdx = 0;
                while (stmtIdx < statements.Count)
                {
                    if (!TryGetAwaitedVarDecl(statements[stmtIdx], out var firstVar, out var firstExpr))
                    {
                        stmtIdx++;
                        continue;
                    }

                    // Skip if this await is already WhenAll/WhenAny
                    if (firstExpr!.ToString().Contains("WhenAll") || firstExpr.ToString().Contains("WhenAny"))
                    {
                        stmtIdx++;
                        continue;
                    }

                    // Grow the block: collect all consecutive awaited-var-decls where
                    // each new expr is independent of every var accumulated so far.
                    // Use identifier-node matching (not substring Contains) to avoid false
                    // positives when variable names are substrings of keywords (e.g. 'a' in 'Task').
                    var blockVars = new List<string> { firstVar! };
                    var blockExprs = new List<ExpressionSyntax> { firstExpr! };
                    int next = stmtIdx + 1;

                    while (next < statements.Count)
                    {
                        if (!TryGetAwaitedVarDecl(statements[next], out var nextVar, out var nextExpr))
                        {
                            break;
                        }

                        if (nextExpr!.ToString().Contains("WhenAll") || nextExpr.ToString().Contains("WhenAny"))
                        {
                            break;
                        }

                        // Check dependency via actual IdentifierNameSyntax nodes, not substring matching.
                        bool nextDependsOnBlock = blockVars.Any(v =>
                            nextExpr!.DescendantNodes().OfType<IdentifierNameSyntax>()
                                .Any(id => id.Identifier.Text == v));
                        bool blockDependsOnNext = blockExprs.Any(e =>
                            e.DescendantNodes().OfType<IdentifierNameSyntax>()
                                .Any(id => id.Identifier.Text == nextVar));
                        if (nextDependsOnBlock || blockDependsOnNext)
                        {
                            break;
                        }

                        blockVars.Add(nextVar!);
                        blockExprs.Add(nextExpr!);
                        next++;
                    }

                    if (blockVars.Count >= 2)
                    {
                        var line = statements[stmtIdx].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var varList = string.Join(", ", blockVars.Select(v => $"'{v}'"));
                        reports.Add(new AsyncSafetyReport(fp, method.Identifier.Text,
                            $"Line {line}: {varList} are awaited sequentially but are independent. " +
                            $"Consider 'await Task.WhenAll(...)' to run all {blockVars.Count} concurrently."));
                        stmtIdx = next; // skip the entire block — already reported as one finding
                    }
                    else
                    {
                        stmtIdx++;
                    }
                }
            }
        }
        return reports;
    }

    private static bool TryGetAwaitedVarDecl(
        StatementSyntax stmt,
        out string? varName,
        out ExpressionSyntax? awaitedExpr)
    {
        varName = null;
        awaitedExpr = null;
        if (stmt is not LocalDeclarationStatementSyntax localDecl)
        {
            return false;
        }

        if (localDecl.Declaration.Variables.Count != 1)
        {
            return false;
        }

        var v = localDecl.Declaration.Variables[0];
        if (v.Initializer?.Value is not AwaitExpressionSyntax awaitExpr)
        {
            return false;
        }

        varName = v.Identifier.Text;
        awaitedExpr = awaitExpr.Expression;
        return true;
    }

    // ── AsyncVoidWithoutTryCatch ──────────────────────────────────────────────

    /// <summary>
    /// Detects async void methods (typically event handlers) whose entire body is not
    /// wrapped in a top-level try/catch. Unhandled exceptions inside async void crash
    /// the process on the thread-pool — there is no caller to propagate to.
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindAsyncVoidWithoutTryCatchAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<AsyncSafetyReport>();

        IEnumerable<Document?> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents).Cast<Document?>()
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(solution.GetDocument);

        foreach (var doc in docs)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                {
                    continue;
                }

                if (method.ReturnType.ToString() != "void")
                {
                    continue;
                }

                if (method.Body == null)
                {
                    continue;
                }

                // The "safe" form wraps the entire body in try/catch:
                // the body has exactly one statement which is a try statement
                // that has at least one catch clause.
                bool hasTopLevelTryCatch = method.Body.Statements.Count == 1 &&
                    method.Body.Statements[0] is TryStatementSyntax trySt &&
                    trySt.Catches.Count > 0;

                if (hasTopLevelTryCatch)
                {
                    continue;
                }

                // Also pass if the method body itself contains ANY try/catch wrapping all awaits
                // (conservative: just check for presence of a catch at any level)
                // This avoids false positives on short methods with inner try/catch.
                bool hasSomeCatch = method.Body.DescendantNodes().OfType<CatchClauseSyntax>().Any();
                if (hasSomeCatch)
                {
                    continue;
                }

                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                reports.Add(new AsyncSafetyReport(fp, method.Identifier.Text,
                    $"Line {line}: async void '{method.Identifier.Text}' has no try/catch. " +
                    "Unhandled exceptions crash the process. Wrap the body in try {{ }} catch (Exception ex) {{ }}."));
            }
        }
        return reports;
    }

    // ── UnawakedDisposeAsync ──────────────────────────────────────────────────
    // Calling IAsyncDisposable.DisposeAsync() without await in a synchronous Dispose()
    // or other non-async cleanup path means cleanup finishes after the method returns,
    // leaving file handles, network connections, and database connections dangling.

    /// <summary>
    /// Detects calls to DisposeAsync() in non-async methods (or in async methods without await),
    /// where the ValueTask returned by DisposeAsync is discarded rather than awaited.
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindUnawakedDisposeAsyncAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<AsyncSafetyReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

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
                if (method.Body == null && method.ExpressionBody == null)
                {
                    continue;
                }

                bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

                foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    // Must be a call to DisposeAsync()
                    if (inv.Expression is not MemberAccessExpressionSyntax ma)
                    {
                        continue;
                    }

                    if (ma.Name.Identifier.Text != "DisposeAsync")
                    {
                        continue;
                    }

                    // Flag if the call is not the direct operand of an AwaitExpressionSyntax
                    bool isAwaited = inv.Parent is AwaitExpressionSyntax;
                    // Also accept: await obj.DisposeAsync().ConfigureAwait(false)
                    if (!isAwaited && inv.Parent is MemberAccessExpressionSyntax chainMa)
                    {
                        if (chainMa.Name.Identifier.Text == "ConfigureAwait" &&
                            chainMa.Parent is InvocationExpressionSyntax configureInv &&
                            configureInv.Parent is AwaitExpressionSyntax)
                        {
                            isAwaited = true;
                        }
                    }

                    if (isAwaited)
                    {
                        continue;
                    }

                    var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var context = isAsync
                        ? "async method without await on DisposeAsync()"
                        : "synchronous method";
                    reports.Add(new AsyncSafetyReport(fp, method.Identifier.Text,
                        $"Line {line}: DisposeAsync() called in {context} without await. " +
                        "The ValueTask returned is discarded — async cleanup runs after the method returns, " +
                        "leaving resources dangling. Use 'await obj.DisposeAsync()' or implement IAsyncDisposable."));
                }
            }
        }
        return reports;
    }

    // ── UnobservedTaskInField ─────────────────────────────────────────────────
    // Assigning a Task/ValueTask to a field or property (rather than awaiting it)
    // means any exception thrown by that task is never observed — it silently fails
    // and can eventually crash the process via UnobservedTaskException.

    /// <summary>
    /// Detects assignments of Task/ValueTask-returning method calls to fields or properties
    /// without await, where the assigned field is never subsequently awaited or .Wait()ed.
    /// This is a fire-and-forget variant that silently swallows exceptions.
    /// </summary>
    public async Task<List<AsyncSafetyReport>> FindUnobservedTaskInFieldAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<AsyncSafetyReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var model = await doc.GetSemanticModelAsync(ct);
            if (model == null)
            {
                continue;
            }

            var fp = doc.FilePath ?? doc.Name;

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                // Collect all field and property names in the class
                var memberNames = classDecl.Members
                    .SelectMany<MemberDeclarationSyntax, string>(m => m switch
                    {
                        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.Text),
                        PropertyDeclarationSyntax p => [p.Identifier.Text],
                        _ => []
                    })
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var method in classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (method.Body == null)
                    {
                        continue;
                    }

                    foreach (var assignment in method.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                        {
                            continue;
                        }

                        // LHS must be a field or property member (not a local)
                        string? targetName = assignment.Left switch
                        {
                            IdentifierNameSyntax id => memberNames.Contains(id.Identifier.Text) ? id.Identifier.Text : null,
                            MemberAccessExpressionSyntax ma when ma.Expression.ToString() is "this" or "_"
                                => memberNames.Contains(ma.Name.Identifier.Text) ? ma.Name.Identifier.Text : null,
                            _ => null
                        };
                        if (targetName == null)
                        {
                            continue;
                        }

                        // RHS must be an invocation — not already awaited
                        ExpressionSyntax rhs = assignment.Right;
                        if (rhs is AwaitExpressionSyntax)
                        {
                            continue; // already properly awaited
                        }

                        if (rhs is not InvocationExpressionSyntax rhsInv)
                        {
                            continue;
                        }

                        // Verify RHS returns Task or ValueTask via semantic model
                        var rhsType = model.GetTypeInfo(rhs, ct).Type as INamedTypeSymbol;
                        if (rhsType == null)
                        {
                            continue;
                        }

                        var rtName = rhsType.OriginalDefinition.ToDisplayString();
                        bool returnsTask = rtName.StartsWith("System.Threading.Tasks.Task") ||
                                           rtName.StartsWith("System.Threading.Tasks.ValueTask");
                        if (!returnsTask)
                        {
                            continue;
                        }

                        // Check that the field is never awaited anywhere in the class
                        bool everAwaited = classDecl.DescendantNodes().OfType<AwaitExpressionSyntax>()
                            .Any(aw => aw.Expression is IdentifierNameSyntax awId &&
                                       awId.Identifier.Text == targetName);
                        bool everWaited = classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma2 &&
                                        ma2.Expression is IdentifierNameSyntax recv &&
                                        recv.Identifier.Text == targetName &&
                                        ma2.Name.Identifier.Text == "Wait");
                        if (everAwaited || everWaited)
                        {
                            continue;
                        }

                        var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        reports.Add(new AsyncSafetyReport(fp, method.Identifier.Text,
                            $"Line {line}: Task/ValueTask from '{rhsInv}' stored in field '{targetName}' without await. " +
                            "Exceptions thrown by the task are never observed — the task silently fails. " +
                            "Await the result directly or use a fire-and-forget helper that logs exceptions."));
                    }
                }
            }
        }
        return reports;
    }
}
