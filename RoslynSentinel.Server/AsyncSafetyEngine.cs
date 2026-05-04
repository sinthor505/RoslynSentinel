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

                    if (awaitStatements.Count < 2) continue;

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
                                declaredVarNames.Add(v.Identifier.Text);
                        }

                        // Extract the second await expression
                        ExpressionSyntax? secondAwaitExpression = null;
                        if (second is ExpressionStatementSyntax secondExpr &&
                            secondExpr.Expression is AwaitExpressionSyntax secondAwait1)
                            secondAwaitExpression = secondAwait1.Expression;
                        else if (second is LocalDeclarationStatementSyntax secondLocal &&
                                 secondLocal.Declaration.Variables[0].Initializer?.Value is AwaitExpressionSyntax secondAwait2)
                            secondAwaitExpression = secondAwait2.Expression;

                        if (secondAwaitExpression == null) continue;

                        // Check if the second await depends on any variable declared in the first
                        bool dependent = secondAwaitExpression
                            .DescendantNodes()
                            .OfType<IdentifierNameSyntax>()
                            .Any(id => declaredVarNames.Contains(id.Identifier.Text));

                        if (!dependent)
                            independentPairs++;
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

    public async Task<List<AsyncSafetyReport>> DetectValueTaskMisuseAsync(string filePath, CancellationToken cancellationToken = default)
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

            SemanticModel? semanticModel = null;
            try { semanticModel = await document.GetSemanticModelAsync(cancellationToken); } catch { }

            var docPath = document.FilePath ?? document.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodName = method.Identifier.Text;
                if (method.Body == null && method.ExpressionBody == null) continue;

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
                    if (memberAccess.Name.Identifier.Text != "Result") continue;

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
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword))))
            {
                if (method.Body == null && method.ExpressionBody == null) continue;

                var awaitExprs = method.DescendantNodes().OfType<AwaitExpressionSyntax>().ToList();

                if (!awaitExprs.Any())
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
                        return true;
                    if (a.Expression is MemberAccessExpressionSyntax directMa &&
                        directMa.Name.Identifier.Text == "CompletedTask" &&
                        directMa.Expression.ToString() == "Task")
                        return true;
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
    private static bool IsEventHandlerSignature(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2) return false;

        var firstType = parameters[0].Type?.ToString().TrimEnd('?') ?? "";
        if (firstType != "object") return false;

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
    public async Task<List<AsyncSafetyReport>> FindUnawaitedFireAndForgetAsync(string filePath, CancellationToken cancellationToken = default)
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

            foreach (var exprStmt in root.DescendantNodes().OfType<ExpressionStatementSyntax>())
            {
                // Pattern A: Raw invocation without await — RunAsync();
                if (exprStmt.Expression is InvocationExpressionSyntax inv)
                {
                    // Skip if parent is an await expression
                    if (exprStmt.Expression is AwaitExpressionSyntax)
                        continue;

                    string? methodName = null;
                    if (inv.Expression is MemberAccessExpressionSyntax ma)
                        methodName = ma.Name.Identifier.Text;
                    else if (inv.Expression is IdentifierNameSyntax id)
                        methodName = id.Identifier.Text;

                    if (methodName != null && methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
                    {
                        var containingMethod = exprStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var lineSpan = exprStmt.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            containingMethod?.Identifier.Text ?? "<unknown>",
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Task-returning method '{methodName}' called without await — exceptions will be swallowed silently."));
                    }
                    continue;
                }

                // Pattern B: Discard-assignment — _ = RunAsync();
                if (exprStmt.Expression is AssignmentExpressionSyntax assign &&
                    assign.Left is IdentifierNameSyntax discardId &&
                    discardId.Identifier.Text == "_" &&
                    assign.Right is InvocationExpressionSyntax discardInv)
                {
                    string? methodName = null;
                    if (discardInv.Expression is MemberAccessExpressionSyntax dma)
                        methodName = dma.Name.Identifier.Text;
                    else if (discardInv.Expression is IdentifierNameSyntax did)
                        methodName = did.Identifier.Text;

                    if (methodName != null && methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
                    {
                        var containingMethod = exprStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var lineSpan = exprStmt.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            containingMethod?.Identifier.Text ?? "<unknown>",
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Task-returning method '{methodName}' fire-and-forgot via discard ('_ = {methodName}()') — exceptions will be swallowed silently. Use proper fire-and-forget with error logging instead."));
                    }
                }
            }
        }
        return reports;
    }
}
