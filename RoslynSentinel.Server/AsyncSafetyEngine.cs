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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)) && m.ReturnType.ToString() == "void");

            foreach (var method in methods)
            {
                reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text, "Async void methods cannot be awaited and crash the process on exceptions."));
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var delayZeroCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv =>
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma
                        || ma.Expression.ToString() != "Task"
                        || ma.Name.Identifier.Text != "Delay")
                        return false;
                    var args = inv.ArgumentList.Arguments;
                    if (args.Count == 0) return false;
                    var first = args[0].Expression;
                    if (first is LiteralExpressionSyntax lit && lit.Token.Text == "0") return true;
                    if (first is MemberAccessExpressionSyntax mts
                        && mts.Expression.ToString() == "TimeSpan"
                        && mts.Name.Identifier.Text == "Zero") return true;
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Detect methods with 2+ sequential awaits that could be parallelized with Task.WhenAll
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var block in method.DescendantNodes().OfType<BlockSyntax>())
                {
                    int awaitCount = block.Statements.Count(s =>
                        s is ExpressionStatementSyntax e && e.Expression is AwaitExpressionSyntax
                        || s is LocalDeclarationStatementSyntax ld
                            && ld.Declaration.Variables.Any(v => v.Initializer?.Value is AwaitExpressionSyntax));

                    if (awaitCount >= 2)
                    {
                        var lineSpan = method.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Method has {awaitCount} sequential awaits. If tasks are independent, consider Task.WhenAll() for parallelism."
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                var expr = awaitExpr.Expression;
                if (expr is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.Text == "ConfigureAwait")
                    continue;

                var containingClass = awaitExpr.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (containingClass != null)
                {
                    var className = containingClass.Identifier.Text;
                    if (appSuffixes.Any(s => className.EndsWith(s)))
                        continue;
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

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
                        !(memberAccess.Parent is InvocationExpressionSyntax))
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                if (ctor.Body == null) continue;
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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            foreach (var lockStmt in root.DescendantNodes().OfType<LockStatementSyntax>())
            {
                var containingType = lockStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null) continue;

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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Pattern 1: double-checked locking without volatile
            foreach (var outerIf in root.DescendantNodes().OfType<IfStatementSyntax>())
            {
                if (!IsNullCheck(outerIf.Condition, out var checkedName)) continue;

                var lockStmt = outerIf.Statement is BlockSyntax b1
                    ? b1.Statements.OfType<LockStatementSyntax>().FirstOrDefault()
                    : outerIf.Statement as LockStatementSyntax;
                if (lockStmt == null) continue;

                var innerIf = lockStmt.Statement is BlockSyntax b2
                    ? b2.Statements.OfType<IfStatementSyntax>().FirstOrDefault()
                    : lockStmt.Statement as IfStatementSyntax;
                if (innerIf == null) continue;
                if (!IsNullCheck(innerIf.Condition, out var innerName)) continue;
                if (checkedName != innerName) continue;

                var containingType = outerIf.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null) continue;

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
                if (!IsNullCheck(ifStmt.Condition, out var name)) continue;
                if (ifStmt.Statement.DescendantNodes().OfType<LockStatementSyntax>().Any()) continue;
                // Not inside a lock
                if (ifStmt.Ancestors().OfType<LockStatementSyntax>().Any()) continue;

                var innerStmts = ifStmt.Statement is BlockSyntax blk
                    ? blk.Statements
                    : new SyntaxList<StatementSyntax>().Add(ifStmt.Statement);

                var hasAssignment = innerStmts.OfType<ExpressionStatementSyntax>()
                    .Any(s => s.Expression is AssignmentExpressionSyntax assign &&
                              assign.Left is IdentifierNameSyntax leftId &&
                              leftId.Identifier.Text == name);

                if (!hasAssignment) continue;

                var containingType = ifStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null) continue;

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
}
