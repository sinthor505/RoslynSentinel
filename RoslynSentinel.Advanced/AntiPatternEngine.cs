using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Advanced;

public record MagicValueLocation(FilePath FilePath, int Line, string Snippet);

public record MagicValueFinding(
    string Value,
    int OccurrenceCount,
    string SuggestedConstantName,
    List<MagicValueLocation> Locations
);

public record OutParamMethodFinding(
    string MethodName,
    string ContainingType,
    FilePath FilePath,
    int Line,
    string CurrentReturnType,
    List<string> OutParamNames,
    List<string> OutParamTypes,
    string SuggestedTupleReturn
);

public record MissingCancellationTokenFinding(
    string MethodName,
    string ContainingType,
    FilePath FilePath,
    int Line,
    List<string> CalleesAcceptingToken
);

public record ExceptionHandlingFinding(
    string Pattern,
    string Description,
    string Severity,
    FilePath FilePath,
    int Line,
    string Snippet
);

/// <summary>A call site that invokes a method decorated with <see cref="ObsoleteAttribute"/>.</summary>
/// <param name="ObsoleteMethodName">Simple name of the [Obsolete]-decorated method.</param>
/// <param name="SymbolId">Roslyn documentation-comment ID uniquely identifying the bridge method (e.g. <c>M:Avaal.Service.CommonSearch.search(System.String)</c>). Pass as <c>symbolId</c> to <see cref="AntiPatternEngine.FindObsoleteCallersAsync"/> to target this exact symbol.</param>
/// <param name="ObsoleteMessage">The message from the [Obsolete] attribute, or empty string.</param>
/// <param name="DeclaringType">Fully-qualified type name that declares the obsolete method.</param>
/// <param name="CallerMethod">Name of the method containing the call site.</param>
/// <param name="CallerType">Fully-qualified type name containing the caller.</param>
/// <param name="FilePath">Absolute path of the file containing the call site.</param>
/// <param name="Line">1-based line number of the call site.</param>
/// <param name="CodeSnippet">Short source snippet around the call site.</param>
public record ObsoleteCallerFinding(
    string ObsoleteMethodName,
    string SymbolId,
    string ObsoleteMessage,
    string DeclaringType,
    string CallerMethod,
    string CallerType,
    FilePath FilePath,
    int Line,
    string CodeSnippet
);

public class AntiPatternEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    private static readonly HashSet<string> AllPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlockingTaskWait", "AsyncVoidMethod", "StringConcatInLoop",
        "CatchExceptionSwallow", "DisposedObjectUsage", "MissingCancellationToken", "MagicNumber",
        "FireAndForgetTask", "MissingDispose", "DisposedAfterUsing", "SyncCallInAsyncContext",
        "TaskRunBlocking", "NamedHandlerLeak", "NamedHandlerThisCapture",
        "ThrowInFinally", "StaticEventSubscription"
    };

    public AntiPatternEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<AntiPatternFinding>> DetectAntiPatternsAsync(
        string? filePath = null,
        string? projectName = null,
        string[]? patternFilter = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects
                .Where(p => !p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                         && !p.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Documents).Cast<Document?>();
        }

        var activePatterns = patternFilter != null && patternFilter.Length > 0
            ? new HashSet<string>(patternFilter, StringComparer.OrdinalIgnoreCase)
            : AllPatterns;

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = document.FilePath ?? document.Name;

            if (activePatterns.Contains("BlockingTaskWait"))
            {
                var model = await document.GetSemanticModelAsync(ct);
                findings.AddRange(DetectBlockingTaskWait(root, path, model));
            }

            if (activePatterns.Contains("AsyncVoidMethod"))
            {
                findings.AddRange(DetectAsyncVoidMethod(root, path));
            }

            if (activePatterns.Contains("StringConcatInLoop"))
            {
                findings.AddRange(DetectStringConcatInLoop(root, path));
            }

            if (activePatterns.Contains("CatchExceptionSwallow"))
            {
                findings.AddRange(DetectCatchExceptionSwallow(root, path));
            }

            if (activePatterns.Contains("DisposedObjectUsage"))
            {
                findings.AddRange(DetectDisposedObjectUsage(root, path));
            }

            if (activePatterns.Contains("MissingCancellationToken"))
            {
                findings.AddRange(DetectMissingCancellationToken(root, path));
            }

            if (activePatterns.Contains("MagicNumber"))
            {
                findings.AddRange(DetectMagicNumbers(root, path));
            }

            if (activePatterns.Contains("FireAndForgetTask"))
            {
                findings.AddRange(DetectFireAndForgetTask(root, path));
            }

            if (activePatterns.Contains("MissingDispose"))
            {
                findings.AddRange(DetectMissingDispose(root, path));
            }

            if (activePatterns.Contains("DisposedAfterUsing"))
            {
                findings.AddRange(DetectDisposedAfterUsing(root, path));
            }

            if (activePatterns.Contains("SyncCallInAsyncContext"))
            {
                findings.AddRange(DetectSyncCallInAsyncContext(root, path));
            }

            if (activePatterns.Contains("TaskRunBlocking"))
            {
                findings.AddRange(DetectTaskRunBlocking(root, path));
            }

            if (activePatterns.Contains("NamedHandlerLeak") || activePatterns.Contains("NamedHandlerThisCapture"))
            {
                findings.AddRange(DetectNamedHandlerLeaks(root, path, activePatterns));
            }

            if (activePatterns.Contains("ThrowInFinally"))
            {
                findings.AddRange(DetectThrowInFinally(root, path));
            }

            if (activePatterns.Contains("StaticEventSubscription"))
            {
                findings.AddRange(DetectStaticEventSubscription(root, path));
            }
        }

        return findings;
    }

    // ── BlockingTaskWait ──────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectBlockingTaskWait(SyntaxNode root, FilePath filePath, SemanticModel? model = null)
    {
        // .Result and .Wait() — use semantic model to verify Task/ValueTask type when available
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = ma.Name.Identifier.Text;
            if (name != "Result" && name != "Wait")
            {
                continue;
            }

            // Skip if parent is an invocation whose callee has 'Result' as a method — e.g. IActionResult
            if (name == "Result" && ma.Parent is InvocationExpressionSyntax)
            {
                continue;
            }

            // Skip if .Result is on the left side of an assignment (property setter, not Task.Result read)
            // e.g. context.Result = new UnauthorizedResult()
            if (name == "Result" && ma.Parent is AssignmentExpressionSyntax assign && assign.Left == ma)
            {
                continue;
            }

            // Use semantic model to verify the expression is a Task/ValueTask type.
            // Applies to both "Result" and "Wait" to prevent false positives on enum values
            // like BoundedChannelFullMode.Wait or IActionResult assignments.
            if (model != null)
            {
                var exprType = model.GetTypeInfo(ma.Expression).Type;
                if (exprType != null)
                {
                    var fullName = exprType.OriginalDefinition.ToDisplayString();
                    var isTask = fullName.StartsWith("System.Threading.Tasks.Task") ||
                                 fullName.StartsWith("System.Threading.Tasks.ValueTask");
                    if (!isTask)
                    {
                        continue;
                    }
                }
            }

            var line = ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(ma.ToString());
            var desc = name == "Result"
                ? "Accessing .Result on a Task blocks the current thread and can cause deadlocks in async contexts."
                : "Calling .Wait() on a Task blocks the current thread and can cause deadlocks in async contexts.";
            yield return new AntiPatternFinding("BlockingTaskWait", desc, "High", filePath, line, snippet);
        }

        // .GetAwaiter().GetResult() chain
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax outer)
            {
                continue;
            }

            if (outer.Name.Identifier.Text != "GetResult")
            {
                continue;
            }

            if (outer.Expression is not InvocationExpressionSyntax getAwaiterCall)
            {
                continue;
            }

            if (getAwaiterCall.Expression is not MemberAccessExpressionSyntax inner)
            {
                continue;
            }

            if (inner.Name.Identifier.Text != "GetAwaiter")
            {
                continue;
            }

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(invocation.ToString());
            yield return new AntiPatternFinding("BlockingTaskWait",
                ".GetAwaiter().GetResult() synchronously blocks the thread and can cause deadlocks.",
                "High", filePath, line, snippet);
        }
    }

    // ── AsyncVoidMethod ───────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectAsyncVoidMethod(SyntaxNode root, FilePath filePath)
    {
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

            if (IsEventHandlerSignature(method))
            {
                continue;
            }

            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(method.Identifier.Text + method.ParameterList.ToString());
            yield return new AntiPatternFinding("AsyncVoidMethod",
                $"'async void' method '{method.Identifier.Text}' cannot be awaited; unhandled exceptions will crash the process.",
                "High", filePath, line, snippet);
        }
    }

    private static bool IsEventHandlerSignature(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
        {
            return false;
        }

        var firstType = parameters[0].Type?.ToString() ?? "";
        var secondType = parameters[1].Type?.ToString() ?? "";
        // Standard pattern: (object sender, XxxEventArgs e)
        var bareFirstType = firstType.TrimEnd('?');
        return (bareFirstType == "object" || bareFirstType == "Object") &&
               secondType.EndsWith("EventArgs");
    }

    // ── StringConcatInLoop ────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectStringConcatInLoop(SyntaxNode root, FilePath filePath)
    {
        static bool IsInsideLoop(SyntaxNode node) =>
            node.Ancestors().Any(a =>
                a is ForEachStatementSyntax ||
                a is ForStatementSyntax ||
                a is WhileStatementSyntax ||
                a is DoStatementSyntax);

        // Pattern A: str += value  (compound assignment)
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                continue;
            }

            if (!IsInsideLoop(assignment))
            {
                continue;
            }

            var lhsText = assignment.Left.ToString();
            var rhs = assignment.Right;
            bool rhsIsString = (rhs is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                               || rhs is InterpolatedStringExpressionSyntax;
            bool lhsLooksLikeString = LooksLikeStringVar(lhsText);

            if (!rhsIsString && !lhsLooksLikeString)
            {
                continue;
            }

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(assignment.ToString());
            yield return new AntiPatternFinding("StringConcatInLoop",
                $"String '+=' in a loop creates many intermediate allocations. Use StringBuilder instead.",
                "Medium", filePath, line, snippet);
        }

        // Pattern B: str = str + value  (simple assignment with self-referencing addition)
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                continue;
            }

            if (assignment.Right is not BinaryExpressionSyntax binary)
            {
                continue;
            }

            if (!binary.IsKind(SyntaxKind.AddExpression))
            {
                continue;
            }

            if (!IsInsideLoop(assignment))
            {
                continue;
            }

            // Must be self-addition: lhs = lhs + rhs (not arbitrary a + b)
            var lhsText = assignment.Left.ToString();
            if (binary.Left.ToString() != lhsText)
            {
                continue;
            }

            bool rhsIsString = (binary.Right is LiteralExpressionSyntax lit2 && lit2.IsKind(SyntaxKind.StringLiteralExpression))
                               || binary.Right is InterpolatedStringExpressionSyntax;
            bool lhsLooksLikeString = LooksLikeStringVar(lhsText);

            if (!rhsIsString && !lhsLooksLikeString)
            {
                continue;
            }

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(assignment.ToString());
            yield return new AntiPatternFinding("StringConcatInLoop",
                $"String '=' with '+' in a loop ('{lhsText} = {lhsText} + ...') creates many intermediate allocations. Use StringBuilder instead.",
                "Medium", filePath, line, snippet);
        }
    }

    private static bool LooksLikeStringVar(string name)
    {
        var lower = name.ToLowerInvariant().TrimStart('_');
        return lower.EndsWith("str") || lower.EndsWith("string") || lower.EndsWith("text") ||
               lower.EndsWith("html") || lower.EndsWith("csv") || lower.EndsWith("xml") ||
               lower.EndsWith("json") || lower.EndsWith("sql") || lower.EndsWith("sb") ||
               lower.EndsWith("msg") || lower.EndsWith("message") || lower.EndsWith("output");
    }

    // ── CatchExceptionSwallow ─────────────────────────────────────────────────

    private static bool CatchBlockHasJustifyingComment(CatchClauseSyntax c) =>
        c.Block.DescendantTrivia().Any(t =>
            t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineCommentTrivia));

    private static IEnumerable<AntiPatternFinding> DetectCatchExceptionSwallow(SyntaxNode root, FilePath filePath)
    {
        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            // Include bare `catch {}` (no declaration) and `catch (Exception ...)` blocks
            if (catchClause.Declaration != null)
            {
                var typeName = catchClause.Declaration.Type.ToString();
                if (typeName != "Exception" && !typeName.EndsWith(".Exception"))
                {
                    continue;
                }
            }

            if (catchClause.Block.Statements.Count > 0)
            {
                continue;
            }

            // A comment inside the block (/* best-effort */, // intentional, etc.)
            // indicates the developer has explicitly acknowledged the swallow — skip.
            if (CatchBlockHasJustifyingComment(catchClause))
            {
                continue;
            }

            var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(catchClause.ToString());
            yield return new AntiPatternFinding("CatchExceptionSwallow",
                "Empty catch block silently swallows exceptions, hiding errors and making debugging extremely difficult.",
                "High", filePath, line, snippet);
        }
    }

    // ── DisposedObjectUsage ───────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectDisposedObjectUsage(SyntaxNode root, FilePath filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body == null)
            {
                continue;
            }

            var disposedVars = new HashSet<string>();

            foreach (var statement in method.Body.Statements)
            {
                // First: flag any member access on already-disposed variables (excluding the dispose call itself)
                if (disposedVars.Count > 0)
                {
                    foreach (var ma in statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                    {
                        if (ma.Name.Identifier.Text == "Dispose")
                        {
                            continue;
                        }

                        var varExpr = ma.Expression.ToString();
                        if (!disposedVars.Contains(varExpr))
                        {
                            continue;
                        }

                        var line = ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        yield return new AntiPatternFinding("DisposedObjectUsage",
                            $"Variable '{varExpr}' is accessed after Dispose() was called on it.",
                            "Medium", filePath, line, Truncate(ma.ToString()));
                    }
                }

                // Then: record any new Dispose() calls found in this statement
                foreach (var inv in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is MemberAccessExpressionSyntax disposeAccess &&
                        disposeAccess.Name.Identifier.Text == "Dispose")
                    {
                        disposedVars.Add(disposeAccess.Expression.ToString());
                    }
                }
            }
        }
    }

    // ── MissingCancellationToken ──────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectMissingCancellationToken(SyntaxNode root, FilePath filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                continue;
            }

            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                continue;
            }

            var returnType = method.ReturnType.ToString();
            if (!returnType.StartsWith("Task") && !returnType.StartsWith("ValueTask"))
            {
                continue;
            }

            var parameters = method.ParameterList.Parameters;
            // Zero-parameter public async methods should still accept CancellationToken
            // so callers can cancel long-running operations — do NOT skip them.

            var hasCt = parameters.Any(p =>
                p.Type?.ToString() is string t &&
                (t == "CancellationToken" || t.EndsWith(".CancellationToken")));

            if (hasCt)
            {
                continue;
            }

            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(method.Identifier.Text + method.ParameterList.ToString());
            yield return new AntiPatternFinding("MissingCancellationToken",
                $"Public async method '{method.Identifier.Text}' has {parameters.Count} parameter(s) but no CancellationToken; callers cannot cancel long-running operations.",
                "Medium", filePath, line, snippet);
        }
    }

    // ── MagicNumber ───────────────────────────────────────────────────────────

    private static readonly HashSet<double> ExemptNumbers = new() { -1, 0, 1 };

    private static IEnumerable<AntiPatternFinding> DetectMagicNumbers(SyntaxNode root, FilePath filePath)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                continue;
            }

            // Exclude field declarations, enum members, attribute arguments, switch labels
            if (literal.Ancestors().Any(a =>
                    a is FieldDeclarationSyntax ||
                    a is EnumMemberDeclarationSyntax ||
                    a is AttributeArgumentSyntax ||
                    a is CaseSwitchLabelSyntax))
            {
                continue;
            }

            // Must be inside a method/constructor/local function body
            if (!literal.Ancestors().Any(a =>
                    a is MethodDeclarationSyntax ||
                    a is ConstructorDeclarationSyntax ||
                    a is LocalFunctionStatementSyntax))
            {
                continue;
            }

            // Skip if the containing local variable declaration name suggests it is intentionally named
            var localDecl = literal.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
            if (localDecl != null)
            {
                var varName = localDecl.Declaration.Variables.FirstOrDefault()?.Identifier.Text?.ToLowerInvariant() ?? "";
                if (varName.Contains("timeout") || varName.Contains("max") || varName.Contains("min") ||
                    varName.Contains("limit") || varName.Contains("capacity") || varName.Contains("size") ||
                    varName.Contains("delay") || varName.Contains("interval") || varName.Contains("threshold"))
                {
                    continue;
                }
            }

            double numValue;
            try { numValue = Convert.ToDouble(literal.Token.Value); }
            catch { continue; }

            if (ExemptNumbers.Contains(numValue))
            {
                continue;
            }

            var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(literal.Parent?.ToString() ?? literal.ToString());
            yield return new AntiPatternFinding("MagicNumber",
                $"Magic number '{literal.Token.Text}' used directly in code. Extract to a named constant for clarity.",
                "Low", filePath, line, snippet);
        }
    }

    // ── FireAndForgetTask ─────────────────────────────────────────────────────

    private static readonly HashSet<string> TaskFireMethods = new(StringComparer.Ordinal)
    {
        "Run", "StartNew", "Factory"
    };

    private static IEnumerable<AntiPatternFinding> DetectFireAndForgetTask(SyntaxNode root, FilePath filePath)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Match Task.Run(...) and Task.Factory.StartNew(...)
            string? methodName = null;
            if (invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                var receiver = ma.Expression.ToString();
                methodName = ma.Name.Identifier.Text;

                bool isTaskRun = (receiver == "Task" && methodName == "Run") ||
                                 (receiver == "Task.Factory" && methodName == "StartNew");
                if (!isTaskRun)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            // Skip if directly awaited
            if (invocation.Parent is AwaitExpressionSyntax)
            {
                continue;
            }

            // Skip if assigned to any variable, field, or discard
            if (invocation.Parent is AssignmentExpressionSyntax)
            {
                continue;
            }

            if (invocation.Parent is EqualsValueClauseSyntax)
            {
                continue;
            }

            // Skip if returned
            if (invocation.Parent is ReturnStatementSyntax)
            {
                continue;
            }

            if (invocation.Parent is ArrowExpressionClauseSyntax)
            {
                continue;
            }

            // Skip if passed as argument (e.g. Task.WhenAll(Task.Run(...)))
            if (invocation.Parent is ArgumentSyntax)
            {
                continue;
            }

            // Skip if chained (.ContinueWith, .ConfigureAwait, etc.)
            if (invocation.Parent is MemberAccessExpressionSyntax)
            {
                continue;
            }

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(invocation.ToString());
            yield return new AntiPatternFinding(
                "FireAndForgetTask",
                $"'{snippet}' is not awaited and not stored — exceptions thrown inside will be silently swallowed. Assign to a Task variable or await it.",
                "High", filePath, line, snippet);
        }
    }

    // ── MissingDispose ────────────────────────────────────────────────────────

    // Well-known IDisposable types allocated with 'new' or factory methods that
    // should always be wrapped in 'using' or try/finally.
    private static readonly HashSet<string> KnownDisposableTypes = new(StringComparer.Ordinal)
    {
        "StreamReader", "StreamWriter", "FileStream", "BinaryReader", "BinaryWriter",
        "MemoryStream", "BufferedStream", "GZipStream", "DeflateStream",
        "SqlConnection", "SqlCommand", "SqlDataReader", "SqlTransaction",
        "SqliteConnection", "NpgsqlConnection", "OleDbConnection", "OdbcConnection",
        "HttpClient", "HttpClientHandler", "HttpResponseMessage",
        "WebClient", "TcpClient", "UdpClient", "Socket",
        "Mutex", "Semaphore", "SemaphoreSlim", "EventWaitHandle", "ManualResetEvent", "AutoResetEvent",
        "CancellationTokenSource", "Timer",
        "Process", "RegistryKey", "X509Certificate", "X509Certificate2"
    };

    private static readonly HashSet<string> KnownDisposableFactories = new(StringComparer.Ordinal)
    {
        "OpenText", "OpenRead", "OpenWrite", "CreateText", "Open", "Create", "AppendText"
    };

    private static IEnumerable<AntiPatternFinding> DetectMissingDispose(SyntaxNode root, FilePath filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body == null)
            {
                continue;
            }

            foreach (var localDecl in method.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                // Skip declarations that use the 'using' keyword (using var x = ...)
                if (localDecl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                {
                    continue;
                }

                foreach (var variable in localDecl.Declaration.Variables)
                {
                    var init = variable.Initializer?.Value;
                    if (init == null)
                    {
                        continue;
                    }

                    bool isKnownDisposable = init switch
                    {
                        // new StreamReader(...), new SqlConnection(...), etc.
                        ObjectCreationExpressionSyntax oc =>
                            KnownDisposableTypes.Contains(oc.Type.ToString().Split('.')[^1]),
                        // File.OpenText(...), File.OpenRead(...), etc.
                        InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax fma =>
                            KnownDisposableFactories.Contains(fma.Name.Identifier.Text),
                        _ => false
                    };

                    if (!isKnownDisposable)
                    {
                        continue;
                    }

                    // Check if this variable is contained in a using-statement block
                    bool inUsing = localDecl.Ancestors().Any(a =>
                        a is UsingStatementSyntax ||
                        (a is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)));
                    if (inUsing)
                    {
                        continue;
                    }

                    // Check if enclosed in a try/finally that calls Dispose
                    bool inTryFinally = localDecl.Ancestors().OfType<TryStatementSyntax>()
                        .Any(ts => ts.Finally?.Block.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Any(inv => inv.Expression is MemberAccessExpressionSyntax dma &&
                                        dma.Name.Identifier.Text == "Dispose" &&
                                        dma.Expression.ToString() == variable.Identifier.Text) == true);
                    if (inTryFinally)
                    {
                        continue;
                    }

                    var line = localDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var typeName = init switch
                    {
                        ObjectCreationExpressionSyntax oc => oc.Type.ToString().Split('.')[^1],
                        InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax fma
                            => fma.Name.Identifier.Text,
                        _ => "disposable resource"
                    };
                    yield return new AntiPatternFinding(
                        "MissingDispose",
                        $"'{variable.Identifier.Text}' ({typeName}) implements IDisposable but is not wrapped in 'using' or try/finally. Resource leak if an exception occurs.",
                        "Medium", filePath, line, Truncate(localDecl.ToString()));
                }
            }
        }
    }

    // ── DisposedAfterUsing ────────────────────────────────────────────────────
    // Detects: variable assigned inside a using() statement body, then accessed after the block.
    // The using-statement form (using (s = expr) { }) disposes on exit but does not scope the
    // variable — s remains accessible after and is now disposed. Use 'using var' to prevent this.

    private static IEnumerable<AntiPatternFinding> DetectDisposedAfterUsing(SyntaxNode root, FilePath filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body == null)
            {
                continue;
            }

            foreach (var usingStmt in method.Body.DescendantNodes().OfType<UsingStatementSyntax>())
            {
                // Only flag the expression form: using (s = expr) { } where s is not declared here
                if (usingStmt.Expression is not AssignmentExpressionSyntax assign)
                {
                    continue;
                }

                if (assign.Left is not IdentifierNameSyntax varId)
                {
                    continue;
                }

                var varName = varId.Identifier.Text;

                // Find statements that come AFTER this using in the same parent block
                var containingBlock = usingStmt.Parent as BlockSyntax;
                if (containingBlock == null)
                {
                    continue;
                }

                var statements = containingBlock.Statements;
                var usingIndex = statements.IndexOf(usingStmt);
                if (usingIndex < 0)
                {
                    continue;
                }

                var accessedAfter = statements
                    .Skip(usingIndex + 1)
                    .SelectMany(s => s.DescendantNodes().OfType<IdentifierNameSyntax>())
                    .Any(id => id.Identifier.Text == varName);

                if (!accessedAfter)
                {
                    continue;
                }

                var line = usingStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new AntiPatternFinding(
                    "DisposedAfterUsing",
                    $"Variable '{varName}' is used after its 'using' block — it is already disposed. " +
                    "Declare the variable inside the using block ('using var') to prevent this.",
                    "High", filePath, line, Truncate(usingStmt.ToString()));
            }
        }
    }

    // ── SyncCallInAsyncContext ────────────────────────────────────────────────
    // Detects synchronous blocking API calls inside async methods where an async alternative exists.
    // Thread.Sleep → await Task.Delay; File.ReadAllText → await File.ReadAllTextAsync; etc.

    private static readonly Dictionary<string, string> SyncToAsyncSuggestions =
        new(StringComparer.Ordinal)
        {
            // Thread
            { "Thread.Sleep",           "await Task.Delay(...)" },
            // File I/O
            { "File.ReadAllText",       "await File.ReadAllTextAsync(...)" },
            { "File.WriteAllText",      "await File.WriteAllTextAsync(...)" },
            { "File.ReadAllBytes",      "await File.ReadAllBytesAsync(...)" },
            { "File.WriteAllBytes",     "await File.WriteAllBytesAsync(...)" },
            { "File.ReadAllLines",      "await File.ReadAllLinesAsync(...)" },
            { "File.WriteAllLines",     "await File.WriteAllLinesAsync(...)" },
            // StreamReader/StreamWriter
            { "StreamReader.ReadToEnd", "await StreamReader.ReadToEndAsync()" },
            { "StreamWriter.Flush",     "await StreamWriter.FlushAsync()" },
            // WebClient (obsolete — prefer HttpClient)
            { "WebClient.DownloadString",   "await HttpClient.GetStringAsync(...)" },
            { "WebClient.UploadString",     "await HttpClient.PostAsync(...)" },
            { "WebClient.DownloadData",     "await HttpClient.GetByteArrayAsync(...)" },
        };

    private static IEnumerable<AntiPatternFinding> DetectSyncCallInAsyncContext(SyntaxNode root, FilePath filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                continue;
            }

            if (method.Body == null && method.ExpressionBody == null)
            {
                continue;
            }

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax ma)
                {
                    continue;
                }

                var receiverText = ma.Expression.ToString();
                var methodText = ma.Name.Identifier.Text;
                var fullKey = $"{receiverText}.{methodText}";

                if (!SyncToAsyncSuggestions.TryGetValue(fullKey, out var suggestion))
                {
                    continue;
                }

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new AntiPatternFinding(
                    "SyncCallInAsyncContext",
                    $"'{fullKey}' blocks the thread inside an async method. Use '{suggestion}' instead.",
                    "Medium", filePath, line, Truncate(invocation.ToString()));
            }
        }
    }

    // ── TaskRunBlocking ───────────────────────────────────────────────────────
    // Detects .Wait()/.Result/.GetAwaiter().GetResult() specifically on Task.Run(...)
    // This blocks one thread-pool thread waiting for ANOTHER thread-pool thread — under
    // load the pool saturates and new work cannot start (thread starvation cascade).

    private static IEnumerable<AntiPatternFinding> DetectTaskRunBlocking(SyntaxNode root, FilePath filePath)
    {
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var memberName = ma.Name.Identifier.Text;
            if (memberName != "Result" && memberName != "Wait")
            {
                continue;
            }

            // Walk up: skip .Wait() that is itself called (it would be MemberAccess.Parent = Invocation)
            // We want the receiver of .Result or .Wait() to be Task.Run(...)
            var receiver = ma.Expression;

            // Direct: Task.Run(...).Result  or  Task.Run(...).Wait()
            bool isTaskRun = false;
            if (receiver is InvocationExpressionSyntax inv &&
                inv.Expression is MemberAccessExpressionSyntax runMa &&
                runMa.Expression.ToString() is "Task" or "Task.Factory" &&
                runMa.Name.Identifier.Text is "Run" or "StartNew")
            {
                isTaskRun = true;
            }

            // Variable: var t = Task.Run(...); t.Result  — harder to track, skip for now
            if (!isTaskRun)
            {
                continue;
            }

            var line = ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(ma.ToString());
            yield return new AntiPatternFinding(
                "TaskRunBlocking",
                $"'.{memberName}' on Task.Run() blocks a thread-pool thread while waiting for another thread-pool thread. " +
                "Under load this causes thread starvation. Use 'await Task.Run(...)' instead.",
                "High", filePath, line, snippet);
        }
    }

    // ── NamedHandlerLeak / NamedHandlerThisCapture ────────────────────────────
    // NamedHandlerLeak: event += this.Handler (or external.Method) without paired -= in Dispose
    // NamedHandlerThisCapture: += this.Method on an external publisher — keeps 'this' alive

    private static IEnumerable<AntiPatternFinding> DetectNamedHandlerLeaks(
        SyntaxNode root, FilePath filePath, HashSet<string> activePatterns)
    {
        foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            bool implementsDisposable = classNode.BaseList?.Types
                .Any(t => t.ToString().Contains("IDisposable")) ?? false;

            // Collect += subscriptions where the right-hand side is a named method (not a lambda)
            var subscriptions = classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression) &&
                            // RHS is a method group (MemberAccess or Identifier), not lambda/anonymous
                            a.Right is MemberAccessExpressionSyntax or IdentifierNameSyntax)
                .ToList();

            if (subscriptions.Count == 0)
            {
                continue;
            }

            // Collect unsubscriptions
            var unsubKeys = new HashSet<string>(
                classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                    .Select(a => $"{a.Left}|{a.Right}"));

            foreach (var sub in subscriptions)
            {
                // Is the event on an external object (not 'this')? Skip 'this.Event += ...' (subscribing to own events is fine)
                bool externalPublisher = sub.Left is MemberAccessExpressionSyntax lma &&
                    lma.Expression is not ThisExpressionSyntax;
                if (!externalPublisher)
                {
                    continue;
                }

                bool hasUnsubscribe = unsubKeys.Contains($"{sub.Left}|{sub.Right}");

                if (activePatterns.Contains("NamedHandlerLeak") && (!implementsDisposable || !hasUnsubscribe))
                {
                    var line = sub.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var reason = !implementsDisposable
                        ? "class does not implement IDisposable"
                        : "Dispose does not unsubscribe";
                    yield return new AntiPatternFinding(
                        "NamedHandlerLeak",
                        $"'{sub.Left}' subscribed with named handler '{sub.Right}' but {reason}. " +
                        "The publisher will keep this instance alive until the event is unsubscribed.",
                        "Medium", filePath, line, Truncate(sub.ToString()));
                }

                // NamedHandlerThisCapture: RHS is 'this.Method' — keeps 'this' alive via publisher
                if (activePatterns.Contains("NamedHandlerThisCapture") &&
                    sub.Right is MemberAccessExpressionSyntax rma &&
                    rma.Expression is ThisExpressionSyntax)
                {
                    var line = sub.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return new AntiPatternFinding(
                        "NamedHandlerThisCapture",
                        $"'{sub.Left} += this.{rma.Name}' on an external publisher holds a reference to 'this'. " +
                        "This instance will stay alive as long as the publisher lives. " +
                        "Unsubscribe in Dispose() with '{sub.Left} -= this.{rma.Name};'",
                        "Medium", filePath, line, Truncate(sub.ToString()));
                }
            }
        }
    }

    // ── MutablePublicApi ──────────────────────────────────────────────────────

    private static readonly HashSet<string> DtoSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Request", "Response", "Dto", "ViewModel", "Model", "Options", "Settings",
        "Config", "Configuration", "Entity", "Projection", "Args", "Arguments",
        "Event", "Command", "Query", "Message", "Payload"
    };

    public async Task<List<AntiPatternFinding>> FindMutablePublicPropertiesAsync(
        string? filePath = null,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = document.FilePath ?? document.Name;

            foreach (var classDecl in root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax))
            {
                if (!classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                {
                    continue;
                }

                var className = classDecl.Identifier.Text;
                if (DtoSuffixes.Any(s => className.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (!prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    {
                        continue;
                    }

                    if (prop.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    {
                        continue;
                    }

                    var setAccessor = prop.AccessorList?.Accessors
                        .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                    if (setAccessor == null)
                    {
                        continue;
                    }
                    // A private or protected setter is NOT a public API surface
                    if (setAccessor.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)))
                    {
                        continue;
                    }

                    var line = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add(new AntiPatternFinding(
                        "MutablePublicApi",
                        $"Public class '{className}' exposes mutable property '{prop.Identifier.Text}' with a public setter. Consider init-only or a dedicated mutator method.",
                        "Low",
                        path,
                        line,
                        Truncate(prop.Identifier.Text + " { get; set; }")
                    ));
                }
            }
        }

        return findings;
    }

    // ── NamingViolation ───────────────────────────────────────────────────────

    public async Task<List<AntiPatternFinding>> FindNamingViolationsAsync(
        string? filePath = null,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = document.FilePath ?? document.Name;

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                findings.AddRange(CheckFieldNamingConventions(classDecl, path));
                findings.AddRange(CheckMethodNamingConventions(classDecl, path));
                findings.AddRange(CheckParameterNamingConventions(classDecl, path));
            }
        }

        return findings;
    }

    private static IEnumerable<AntiPatternFinding> CheckFieldNamingConventions(
        ClassDeclarationSyntax classDecl, FilePath filePath)
    {
        foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            {
                continue;
            }

            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                continue;
            }

            bool isPrivateOrImplicit =
                field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) ||
                (!field.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.PublicKeyword) ||
                    m.IsKind(SyntaxKind.ProtectedKeyword) ||
                    m.IsKind(SyntaxKind.InternalKeyword)));

            if (!isPrivateOrImplicit)
            {
                continue;
            }

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.Text;
                if (name.Contains('<') || name.Contains('>'))
                {
                    continue; // compiler-generated
                }

                if (name == "_")
                {
                    continue; // discard
                }

                // Expected: _camelCase (starts with _ followed by a lowercase letter)
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^_[a-z]"))
                {
                    var line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return new AntiPatternFinding(
                        "NamingViolation",
                        $"Private field '{name}' does not follow the '_camelCase' convention (e.g. '_myField').",
                        "Low",
                        filePath,
                        line,
                        name
                    );
                }
            }
        }
    }

    private static IEnumerable<AntiPatternFinding> CheckMethodNamingConventions(
        ClassDeclarationSyntax classDecl, FilePath filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            bool isNonPrivate = method.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.PublicKeyword) ||
                m.IsKind(SyntaxKind.ProtectedKeyword) ||
                m.IsKind(SyntaxKind.InternalKeyword));

            if (isNonPrivate && char.IsLower(name[0]))
            {
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new AntiPatternFinding(
                    "NamingViolation",
                    $"Method '{name}' is non-private but starts with a lowercase letter; C# convention requires PascalCase.",
                    "Low",
                    filePath,
                    line,
                    Truncate(name + method.ParameterList.ToString())
                );
            }
        }
    }

    private static IEnumerable<AntiPatternFinding> CheckParameterNamingConventions(
        ClassDeclarationSyntax classDecl, FilePath filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            foreach (var param in method.ParameterList.Parameters)
            {
                var name = param.Identifier.Text;
                if (string.IsNullOrEmpty(name) || name == "_")
                {
                    continue;
                }

                if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
                {
                    continue;
                }

                if (char.IsUpper(name[0]))
                {
                    var line = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return new AntiPatternFinding(
                        "NamingViolation",
                        $"Parameter '{name}' starts with an uppercase letter; C# convention requires camelCase for parameters.",
                        "Low",
                        filePath,
                        line,
                        name
                    );
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Truncate(string text, int maxLength = 100)
    {
        text = text.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    // ── FindStringMagicValues ─────────────────────────────────────────────────

    public async Task<List<MagicValueFinding>> FindStringMagicValuesAsync(
        string? filePath = null,
        string? projectName = null,
        int minOccurrences = 3,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var occurrences = new Dictionary<string, List<MagicValueLocation>>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var docPath = document.FilePath ?? document.Name;

            // Skip test files
            if (docPath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                docPath.Contains("Spec", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                var value = literal.Token.ValueText;
                if (string.IsNullOrWhiteSpace(value) || value.Length < 3)
                {
                    continue;
                }

                // Skip SQL parameter tokens like @UserId, @Email (ADO.NET parameterized queries)
                if (value.Length > 1 && value[0] == '@' && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    continue;
                }

                // Skip inside nameof()
                if (literal.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(inv => inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof"))
                {
                    continue;
                }

                // Skip inside attribute constructor args
                if (literal.Ancestors().Any(a => a is AttributeArgumentSyntax))
                {
                    continue;
                }

                // Skip inside using directives
                if (literal.Ancestors().Any(a => a is UsingDirectiveSyntax))
                {
                    continue;
                }

                var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var snippet = Truncate(literal.Parent?.ToString() ?? literal.ToString());

                if (!occurrences.TryGetValue(value, out var list))
                {
                    list = new List<MagicValueLocation>();
                    occurrences[value] = list;
                }
                list.Add(new MagicValueLocation(docPath, line, snippet));
            }
        }

        return occurrences
            .Where(kv => kv.Value.Count >= minOccurrences)
            .Select(kv => new MagicValueFinding(
                Value: kv.Key,
                OccurrenceCount: kv.Value.Count,
                SuggestedConstantName: ToConstantName(kv.Key),
                Locations: kv.Value))
            .OrderByDescending(f => f.OccurrenceCount)
            .ToList();
    }

    private static string ToConstantName(string value)
    {
        var segments = System.Text.RegularExpressions.Regex.Split(value, @"[^a-zA-Z0-9]+")
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1).ToLowerInvariant() : ""));
        var result = string.Concat(segments);
        return string.IsNullOrEmpty(result) ? "MagicString" : result;
    }

    // ── FindMissingCancellationTokens ─────────────────────────────────────────

    public async Task<List<MissingCancellationTokenFinding>> FindMissingCancellationTokensAsync(
        string? filePath = null,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects
                .Where(p => !p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                         && !p.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Documents).Cast<Document?>();
        }

        var findings = new List<MissingCancellationTokenFinding>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var docPath = document.FilePath ?? document.Name;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(ct);
            if (model == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                var returnType = method.ReturnType.ToString();
                bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");
                if (!isAsync && !returnsTask)
                {
                    continue;
                }

                // Skip abstract methods (no body)
                if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
                {
                    continue;
                }

                // Skip if already has CancellationToken
                bool hasCt = method.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString() is string t &&
                    (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
                if (hasCt)
                {
                    continue;
                }

                // Skip event handlers — their delegate signature is fixed (object sender, XxxEventArgs e)
                // and cannot be extended with a CancellationToken parameter.
                if (IsEventHandlerSignature(method))
                {
                    continue;
                }

                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                var calleesAcceptingToken = new List<string>();
                var seenCallees = new HashSet<string>();

                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var si = model.GetSymbolInfo(invocation, ct);
                    var callee = si.Symbol as IMethodSymbol
                        ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (callee == null)
                    {
                        continue;
                    }

                    bool acceptsCt = callee.Parameters.Any(p => p.Type.Name == "CancellationToken");
                    if (!acceptsCt)
                    {
                        continue;
                    }

                    if (seenCallees.Add(callee.Name))
                    {
                        calleesAcceptingToken.Add(callee.Name);
                    }
                }

                if (calleesAcceptingToken.Count == 0)
                {
                    continue;
                }

                var containingType = model.GetDeclaredSymbol(method, ct)?.ContainingType?.Name ?? "";
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                findings.Add(new MissingCancellationTokenFinding(
                    MethodName: method.Identifier.Text,
                    ContainingType: containingType,
                    FilePath: docPath,
                    Line: line,
                    CalleesAcceptingToken: calleesAcceptingToken
                ));
            }
        }

        return findings;
    }

    // ── AnalyzeExceptionHandling ──────────────────────────────────────────────

    public async Task<List<ExceptionHandlingFinding>> AnalyzeExceptionHandlingAsync(
        FilePath filePath,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        var findings = new List<ExceptionHandlingFinding>();

        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = document.FilePath ?? document.Name;

            foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var snippet = Truncate(catchClause.ToString());
                var typeName = catchClause.Declaration?.Type.ToString();

                // 1. CatchAll
                bool isCatchAll = catchClause.Declaration == null
                    || typeName == "Exception"
                    || typeName == "System.Exception";
                if (isCatchAll)
                {
                    var suggestion = "";
                    if (catchClause.Parent is TryStatementSyntax parentTry)
                    {
                        var inferred = InferExpectedExceptions(parentTry.Block);
                        if (inferred.Count > 0)
                        {
                            suggestion = $" Based on the try block, consider catching: {string.Join(", ", inferred)}, then Exception as a final catch-all.";
                        }
                    }
                    findings.Add(new ExceptionHandlingFinding(
                        "CatchAll",
                        "Catching System.Exception (or bare catch) swallows all exception types, including those that should propagate." + suggestion,
                        "High", path, line, snippet));
                }

                // 2. EmptyRethrow: catch (Exception ex) { throw ex; }
                var statements = catchClause.Block.Statements;
                if (statements.Count == 1 &&
                    statements[0] is ThrowStatementSyntax throwStmt &&
                    throwStmt.Expression != null)
                {
                    var catchVar = catchClause.Declaration?.Identifier.Text;
                    if (catchVar != null && throwStmt.Expression.ToString() == catchVar)
                    {
                        findings.Add(new ExceptionHandlingFinding(
                            "EmptyRethrow",
                            $"'throw {catchVar};' loses the original stack trace. Use 'throw;' to preserve it.",
                            "High", path, line, snippet));
                    }
                }

                // 3. SwallowedException: no rethrow, no log, no return
                bool hasRethrow = statements.Any(s => s is ThrowStatementSyntax t && t.Expression == null);
                bool hasThrowExpr = statements.Any(s => s is ThrowStatementSyntax t && t.Expression != null);
                bool hasReturn = statements.Any(s => s is ReturnStatementSyntax);
                bool hasLog = catchClause.Block.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(inv =>
                    {
                        var text = inv.ToString();
                        return text.Contains("Log") || text.Contains("log") ||
                               text.Contains("Console.Write") || text.Contains("Debug.Write");
                    });

                if (!hasRethrow && !hasThrowExpr && !hasReturn && !hasLog)
                {
                    bool isEmpty = statements.Count == 0;
                    // An empty block with a comment (/* best-effort */, // intentional) is
                    // acknowledged by the developer — downgrade to Info rather than High.
                    bool hasComment = CatchBlockHasJustifyingComment(catchClause);
                    var severity = isEmpty ? (hasComment ? "Info" : "High") : "Medium";
                    var desc = isEmpty
                        ? (hasComment
                            ? "Empty catch block with justifying comment. Verify the swallow is truly intentional."
                            : "Empty catch block silently swallows the exception with no logging, rethrowing, or error return.")
                        : "Exception is caught but neither rethrown nor logged, making the failure invisible.";
                    findings.Add(new ExceptionHandlingFinding(
                        "SwallowedException", desc, severity, path, line, snippet));
                }

                // 4. ExceptionAsControlFlow: catch of validation-type exceptions inside a loop
                bool isInsideLoop = catchClause.Ancestors().Any(a =>
                    a is ForEachStatementSyntax ||
                    a is ForStatementSyntax ||
                    a is WhileStatementSyntax ||
                    a is DoStatementSyntax);

                if (isInsideLoop && typeName != null)
                {
                    bool isExpectedExType =
                        typeName is "FormatException" or "System.FormatException" or
                                    "ParseException" or
                                    "InvalidCastException" or "System.InvalidCastException" or
                                    "OverflowException" or "System.OverflowException" or
                                    "ArgumentException" or "System.ArgumentException";
                    if (isExpectedExType)
                    {
                        findings.Add(new ExceptionHandlingFinding(
                            "ExceptionAsControlFlow",
                            $"Catching '{typeName}' inside a loop uses exceptions for control flow. Use TryParse/TryXxx methods instead.",
                            "Medium", path, line, snippet));
                    }
                }
            }

            // 5. throw new Exception("message") — too broad; suggest specific exception type
            foreach (var throwStmt in root.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression is not ObjectCreationExpressionSyntax oc)
                {
                    continue;
                }

                if (oc.Type.ToString() is not ("Exception" or "System.Exception"))
                {
                    continue;
                }

                var msgArg = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                var suggested = InferSpecificExceptionType(msgArg?.Token.ValueText);
                var desc = suggested != null
                    ? $"'throw new Exception()' is too broad. Based on the message, consider 'throw new {suggested}(...)' or a custom exception type."
                    : "'throw new Exception()' is too broad. Use a specific BCL exception (ArgumentException, InvalidOperationException, etc.) or create a custom exception class.";

                var tLoc = throwStmt.GetLocation().GetLineSpan().StartLinePosition;
                findings.Add(new ExceptionHandlingFinding(
                    "GenericThrowExpression", desc, "Medium", path, tLoc.Line + 1, Truncate(throwStmt.ToString())));
            }

            // 6. Explicit .Dispose() call not protected by try/catch or 'using'
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax dma)
                {
                    continue;
                }

                if (dma.Name.Identifier.Text != "Dispose")
                {
                    continue;
                }

                if (inv.ArgumentList.Arguments.Count != 0)
                {
                    continue;
                }

                var isProtected = false;
                foreach (var anc in inv.Ancestors())
                {
                    if (anc is TryStatementSyntax || anc is UsingStatementSyntax) { isProtected = true; break; }
                    if (anc is LocalDeclarationStatementSyntax lds3 && lds3.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) { isProtected = true; break; }
                }

                if (!isProtected)
                {
                    var dLoc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    findings.Add(new ExceptionHandlingFinding(
                        "UnprotectedDispose",
                        "Explicit .Dispose() call is not protected by try/catch. If Dispose() throws, cleanup is incomplete. Prefer 'using', or wrap in try { } catch { /* log */ }.",
                        "Medium", path, dLoc.Line + 1, Truncate(inv.ToString())));
                }
            }

            // 7. Dispose() implementation with multiple unprotected sub-Dispose calls
            // If one sub-resource throws in Dispose(), the others are never released.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == "Dispose" && m.ParameterList.Parameters.Count == 0))
            {
                if (method.Body == null)
                {
                    continue;
                }

                var unprotected = method.Body.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                                  ma.Name.Identifier.Text == "Dispose" &&
                                  inv.ArgumentList.Arguments.Count == 0 &&
                                  !IsProtectedByTryWithin(inv, method))
                    .ToList();

                if (unprotected.Count >= 2)
                {
                    var mLoc = method.GetLocation().GetLineSpan().StartLinePosition;
                    findings.Add(new ExceptionHandlingFinding(
                        "UnsafeDisposeImplementation",
                        $"Dispose() calls {unprotected.Count} sub-resources without individual try/catch. If one throws, the remaining resources are not disposed. Wrap each .Dispose() call in its own try/catch.",
                        "Medium", path, mLoc.Line + 1, "Dispose()"));
                }
            }
        }

        return findings;
    }

    private static bool IsProtectedByTryWithin(SyntaxNode node, SyntaxNode container)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor == container)
            {
                break;
            }

            if (ancestor is TryStatementSyntax)
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> InferExpectedExceptions(BlockSyntax tryBlock)
    {
        var suggestions = new List<string>();

        foreach (var inv in tryBlock.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var expr = inv.ToString();
            if (expr.Contains("File.") || expr.Contains("Directory.") || expr.Contains("Stream") ||
                expr.Contains("ReadAllText") || expr.Contains("WriteAllText") || expr.Contains("ReadAllBytes"))
            {
                AddIfAbsent(suggestions, "IOException");
                AddIfAbsent(suggestions, "UnauthorizedAccessException");
            }
            if (expr.Contains("HttpClient") || expr.Contains("GetAsync") || expr.Contains("PostAsync") ||
                expr.Contains("PutAsync") || expr.Contains("SendAsync") || expr.Contains("GetStringAsync"))
            {
                AddIfAbsent(suggestions, "HttpRequestException");
                AddIfAbsent(suggestions, "TaskCanceledException");
            }
            if (expr.Contains(".Parse(") && !expr.Contains("Enum.Parse"))
            {
                AddIfAbsent(suggestions, "FormatException");
                AddIfAbsent(suggestions, "OverflowException");
            }
            if (expr.Contains("SqlCommand") || expr.Contains("ExecuteReader") ||
                expr.Contains("ExecuteNonQuery") || expr.Contains("ExecuteScalar") || expr.Contains(".Open()"))
            {
                AddIfAbsent(suggestions, "SqlException");
                AddIfAbsent(suggestions, "InvalidOperationException");
            }
            if (expr.Contains("JsonSerializer") || expr.Contains("JsonConvert") || expr.Contains("Deserialize"))
            {
                AddIfAbsent(suggestions, "JsonException");
            }

            if (expr.Contains("Convert.To"))
            {
                AddIfAbsent(suggestions, "FormatException");
                AddIfAbsent(suggestions, "InvalidCastException");
            }
        }

        if (tryBlock.DescendantNodes().OfType<CastExpressionSyntax>().Any())
        {
            AddIfAbsent(suggestions, "InvalidCastException");
        }

        if (tryBlock.DescendantNodes().OfType<ElementAccessExpressionSyntax>().Any())
        {
            AddIfAbsent(suggestions, "IndexOutOfRangeException");
        }

        if (tryBlock.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Any(b => b.IsKind(SyntaxKind.DivideExpression)))
        {
            AddIfAbsent(suggestions, "DivideByZeroException");
        }

        return suggestions;
    }

    private static string? InferSpecificExceptionType(string? message)
    {
        if (message == null)
        {
            return null;
        }

        var lower = message.ToLowerInvariant();

        if (lower.Contains("null") || lower.Contains("cannot be null") || lower.Contains("required"))
        {
            return "ArgumentNullException";
        }

        if (lower.Contains("not supported"))
        {
            return "NotSupportedException";
        }

        if (lower.Contains("not implemented"))
        {
            return "NotImplementedException";
        }

        if (lower.Contains("out of range") || lower.Contains("bounds"))
        {
            return "ArgumentOutOfRangeException";
        }

        if (lower.Contains("invalid operation") || lower.Contains("invalid state") || lower.Contains("already"))
        {
            return "InvalidOperationException";
        }

        if (lower.Contains("timeout") || lower.Contains("timed out"))
        {
            return "TimeoutException";
        }

        if (lower.Contains("format") || lower.Contains("invalid format") || lower.Contains("parse"))
        {
            return "FormatException";
        }

        if (lower.Contains("overflow"))
        {
            return "OverflowException";
        }

        if (lower.Contains("argument") || lower.Contains("parameter") || lower.Contains("invalid"))
        {
            return "ArgumentException";
        }

        if (lower.Contains("not found") || lower.Contains("does not exist") || lower.Contains("missing key"))
        {
            return "KeyNotFoundException";
        }

        if (lower.Contains("access denied") || lower.Contains("unauthorized") || lower.Contains("permission"))
        {
            return "UnauthorizedAccessException";
        }

        if (lower.Contains("disposed"))
        {
            return "ObjectDisposedException";
        }

        return null;
    }

    private static void AddIfAbsent(List<string> list, string item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    public async Task<List<AntiPatternFinding>> FindLongParameterListAsync(
        string? filePath = null, string? projectName = null, int minParameters = 4, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var results = new List<AntiPatternFinding>();
        var diSuffixes = new[] { "Service", "Repository", "Options", "Factory" };

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var parameters = method.ParameterList.Parameters;
                if (parameters.Count < minParameters)
                {
                    continue;
                }

                // For constructors: if ALL params end in DI suffixes, skip
                if (method is ConstructorDeclarationSyntax)
                {
                    bool allDi = parameters.All(p =>
                        p.Type != null && diSuffixes.Any(s => p.Type.ToString().EndsWith(s)));
                    if (allDi)
                    {
                        continue;
                    }
                }

                string memberName = method switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    ConstructorDeclarationSyntax c => c.Identifier.Text + " (constructor)",
                    _ => "<unknown>"
                };

                var lineSpan = method.GetLocation().GetLineSpan();
                var paramNames = string.Join(", ", parameters.Select(p => p.Identifier.Text));
                results.Add(new AntiPatternFinding(
                    "LongParameterList",
                    $"'{memberName}' has {parameters.Count} parameters ({paramNames}). Consider introducing a Parameter Object.",
                    "Medium",
                    doc.FilePath,
                    lineSpan.StartLinePosition.Line + 1,
                    method.ParameterList.ToString()));
            }
        }
        return results;
    }

    public async Task<List<AntiPatternFinding>> FindPrimitiveObsessionAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var primitiveTypes = new HashSet<string> { "string", "int", "long", "Guid", "bool", "String", "Int32", "Int64", "Boolean" };
        var results = new List<AntiPatternFinding>();

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var parameters = method.ParameterList.Parameters
                    .Where(p => p.Modifiers.All(m => !m.IsKind(SyntaxKind.ParamsKeyword)) && p.Type != null)
                    .ToList();

                var typeCounts = parameters
                    .GroupBy(p => p.Type!.ToString())
                    .Where(g => primitiveTypes.Contains(g.Key) && g.Count() >= 3)
                    .ToList();

                foreach (var group in typeCounts)
                {
                    string memberName = method switch
                    {
                        MethodDeclarationSyntax m => m.Identifier.Text,
                        ConstructorDeclarationSyntax c => c.Identifier.Text + " (constructor)",
                        _ => "<unknown>"
                    };

                    var paramNames = string.Join(", ", group.Select(p => p.Identifier.Text));
                    var lineSpan = method.GetLocation().GetLineSpan();
                    results.Add(new AntiPatternFinding(
                        "PrimitiveObsession",
                        $"'{memberName}' has {group.Count()} parameters of type '{group.Key}': {paramNames}. Consider a dedicated type.",
                        "Medium",
                        doc.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        method.ParameterList.ToString()));
                }
            }
        }
        return results;
    }

    public async Task<List<AntiPatternFinding>> FindInconsistentAsyncSuffixAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var results = new List<AntiPatternFinding>();

        static bool IsTaskReturning(MethodDeclarationSyntax m)
        {
            var ret = m.ReturnType.ToString();
            return ret == "Task" || ret.StartsWith("Task<") || ret == "ValueTask" || ret.StartsWith("ValueTask<");
        }

        static bool IsAsync(MethodDeclarationSyntax m) =>
            m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)) || IsTaskReturning(m);

        static bool IsEventHandler(MethodDeclarationSyntax m)
        {
            var name = m.Identifier.Text;
            return name.StartsWith("On") || (m.ParameterList.Parameters.Count == 2 &&
                m.ParameterList.Parameters[1].Type?.ToString().Contains("EventArgs") == true);
        }

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (IsEventHandler(method))
                {
                    continue;
                }

                var name = method.Identifier.Text;
                bool isAsync = IsAsync(method);
                bool hasAsyncSuffix = name.EndsWith("Async", StringComparison.Ordinal);

                var lineSpan = method.GetLocation().GetLineSpan();

                if (isAsync && !hasAsyncSuffix)
                {
                    results.Add(new AntiPatternFinding(
                        "InconsistentAsyncSuffix",
                        $"Method '{name}' is async/Task-returning but does not end with 'Async'. Rename to '{name}Async'.",
                        "Low",
                        doc.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        name));
                }
                else if (!isAsync && hasAsyncSuffix)
                {
                    results.Add(new AntiPatternFinding(
                        "InconsistentAsyncSuffix",
                        $"Method '{name}' ends with 'Async' but is not async and does not return Task/ValueTask. Remove 'Async' suffix.",
                        "Low",
                        doc.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        name));
                }
            }
        }
        return results;
    }

    // ── ThrowInFinally ────────────────────────────────────────────────────────
    // Throwing from a finally block suppresses the original exception; the caller
    // sees the finally-throw instead of the real error, destroying stack context.

    private static IEnumerable<AntiPatternFinding> DetectThrowInFinally(SyntaxNode root, FilePath filePath)
    {
        foreach (var tryStmt in root.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (tryStmt.Finally == null)
            {
                continue;
            }

            foreach (var throwStmt in tryStmt.Finally.Block.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                // Bare `throw;` re-throws current exception — that is fine
                if (throwStmt.Expression == null)
                {
                    continue;
                }

                var loc = throwStmt.GetLocation().GetLineSpan().StartLinePosition;
                yield return new AntiPatternFinding(
                    "ThrowInFinally",
                    "Throwing in a finally block silently swallows the original exception. " +
                    "The caller sees the finally-throw instead of the real error, losing the original stack trace. " +
                    "Move error handling into catch blocks.",
                    "Warning", filePath,
                    loc.Line + 1, throwStmt.ToString());
            }
        }
    }

    // ── StaticEventSubscription ───────────────────────────────────────────────
    // Subscribing an instance method to a static event without unsubscribing in
    // Dispose pins the instance in memory for the lifetime of the AppDomain.

    private static IEnumerable<AntiPatternFinding> DetectStaticEventSubscription(SyntaxNode root, FilePath filePath)
    {
        // Collect unsubscribe targets: everything on the RHS of a -= assignment
        var unsubscribeTargets = root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
            .Select(a => a.Right.ToString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                continue;
            }

            // LHS must be a qualified member access: ReceiverName.EventName
            if (assignment.Left is not MemberAccessExpressionSyntax lhsMa)
            {
                continue;
            }

            // Heuristic: receiver starts with uppercase letter — likely a class name (static access)
            var receiverText = lhsMa.Expression.ToString();
            if (string.IsNullOrEmpty(receiverText) || !char.IsUpper(receiverText[0]))
            {
                continue;
            }

            // RHS must be a simple method reference (not a lambda)
            var rhsText = assignment.Right.ToString();
            if (assignment.Right is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                continue;
            }

            // Flag only if there's no paired unsubscribe
            if (unsubscribeTargets.Contains(rhsText))
            {
                continue;
            }

            var loc = assignment.GetLocation().GetLineSpan().StartLinePosition;
            yield return new AntiPatternFinding(
                "StaticEventSubscription",
                $"Subscribing '{rhsText}' to static event '{lhsMa}' without a paired '-=' in Dispose. " +
                "Static events hold references to subscribers for the lifetime of the AppDomain, preventing GC. " +
                "Unsubscribe in Dispose() or use a WeakEventManager.",
                "Warning", filePath,
                loc.Line + 1, assignment.ToString());
        }
    }

    // ── Multiple out-parameter methods ────────────────────────────────────────

    public async Task<List<OutParamMethodFinding>> FindMultipleOutParameterMethodsAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var results = new List<OutParamMethodFinding>();

        foreach (var doc in documents)
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

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

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var outParams = method.ParameterList.Parameters
                    .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)))
                    .ToList();

                if (outParams.Count < 2)
                {
                    continue;
                }

                var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<unknown>";
                var outParamNames = outParams.Select(p => p.Identifier.Text).ToList();
                var outParamTypes = outParams.Select(p => p.Type?.ToString() ?? "?").ToList();

                var tupleElements = outParams.Select(p => $"{p.Type} {p.Identifier.Text}");
                var currentReturn = method.ReturnType.ToString();
                string suggestedReturn;
                if (currentReturn == "void")
                {
                    suggestedReturn = $"({string.Join(", ", tupleElements)})";
                }
                else
                {
                    suggestedReturn = $"({currentReturn} result, {string.Join(", ", tupleElements)})";
                }

                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                results.Add(new OutParamMethodFinding(
                    method.Identifier.Text,
                    containingType,
                    doc.FilePath,
                    line,
                    currentReturn,
                    outParamNames,
                    outParamTypes,
                    suggestedReturn));
            }
        }

        return results;
    }

    // ── Value-type mutation intent warnings ───────────────────────────────────

    public async Task<List<AntiPatternFinding>> FindValueTypeMutationIntentAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var results = new List<AntiPatternFinding>();

        foreach (var doc in documents)
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

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

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                // Map parameter name → parameter symbol for fast lookup
                var paramSymbols = new Dictionary<string, (IParameterSymbol Symbol, bool IsValueType)>();
                foreach (var p in method.ParameterList.Parameters)
                {
                    // Skip ref/out/in — those are intentionally pass-by-reference
                    if (p.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.RefKeyword) ||
                        m.IsKind(SyntaxKind.OutKeyword) ||
                        m.IsKind(SyntaxKind.InKeyword)))
                    {
                        continue;
                    }

                    if (model.GetDeclaredSymbol(p, ct) is not IParameterSymbol sym)
                    {
                        continue;
                    }

                    paramSymbols[p.Identifier.Text] = (sym, sym.Type.IsValueType);
                }

                if (paramSymbols.Count == 0)
                {
                    continue;
                }

                // Find which parameters are mentioned in return statements
                var returnedSymbols = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
                {
                    if (ret.Expression == null)
                    {
                        continue;
                    }

                    foreach (var id in ret.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    {
                        if (paramSymbols.ContainsKey(id.Identifier.Text))
                        {
                            returnedSymbols.Add(id.Identifier.Text);
                        }
                    }
                }

                // Find all simple assignments where LHS is a parameter (not a member access like param.Prop)
                foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    // LHS must be a bare identifier, not a member-access
                    if (assignment.Left is not IdentifierNameSyntax lhsId)
                    {
                        continue;
                    }

                    var paramName = lhsId.Identifier.Text;
                    if (!paramSymbols.TryGetValue(paramName, out var entry))
                    {
                        continue;
                    }

                    // Verify via semantic model that it resolves to the parameter, not a local with the same name
                    var resolvedSym = model.GetSymbolInfo(lhsId, ct).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(resolvedSym, entry.Symbol))
                    {
                        continue;
                    }

                    var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    bool isReturned = returnedSymbols.Contains(paramName);

                    if (entry.IsValueType && !isReturned)
                    {
                        // Value type param reassigned but not returned — caller will never see the change
                        results.Add(new AntiPatternFinding(
                            "ValueTypeParameterReassigned",
                            $"Parameter '{paramName}' ({entry.Symbol.Type.Name}) is a value type reassigned inside the method but not returned. " +
                            "The caller's copy is unaffected. If caller visibility was the intent, use 'ref' or return the value.",
                            "Warning",
                            doc.FilePath,
                            line,
                            Truncate(assignment.ToString())));
                    }
                    else if (!entry.IsValueType && !isReturned)
                    {
                        // Reference type param replaced with new instance — caller's reference is unaffected
                        // Only flag if RHS is an object creation (new ...) to reduce false positives
                        bool rhsIsNewInstance =
                            assignment.Right is ObjectCreationExpressionSyntax ||
                            assignment.Right is ImplicitObjectCreationExpressionSyntax;

                        if (rhsIsNewInstance)
                        {
                            results.Add(new AntiPatternFinding(
                                "ReferenceTypeParameterReplaced",
                                $"Parameter '{paramName}' ({entry.Symbol.Type.Name}) is replaced with a new instance but the new reference is not returned. " +
                                "The caller's reference still points to the original object. If the intent was to return the new instance, add it to the return value.",
                                "Warning",
                                doc.FilePath,
                                line,
                                Truncate(assignment.ToString())));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds all call sites that invoke a method decorated with <see cref="ObsoleteAttribute"/>.
    /// Useful for tracking CS0618 migration progress — every result is a caller that still needs
    /// to be migrated away from the deprecated (bridge) method.
    /// </summary>
    /// <param name="messagePattern">Optional substring to filter by the [Obsolete] message text (case-insensitive).</param>
    /// <param name="filePath">Optional: restrict results to call sites in this file.</param>
    /// <param name="projectName">Optional: restrict results to call sites in this project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<ObsoleteCallerFinding>> FindObsoleteCallersAsync(
        string? messagePattern = null,
        string? filePath = null,
        string? projectName = null,
        string? symbolId = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ObsoleteCallerFinding>();

        // Collect all [Obsolete]-decorated method symbols across the solution.
        foreach (var project in solution.Projects)
        {
            if (projectName != null && !project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                continue;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                // Walk all method declarations in this file, looking for [Obsolete].
                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
                    if (methodSymbol == null)
                    {
                        continue;
                    }

                    var obsoleteAttr = methodSymbol.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name is "ObsoleteAttribute" or "Obsolete");
                    if (obsoleteAttr == null)
                    {
                        continue;
                    }

                    // Extract the message text from the attribute constructor.
                    var obsoleteMessage = obsoleteAttr.ConstructorArguments.Length > 0
                        ? obsoleteAttr.ConstructorArguments[0].Value?.ToString() ?? string.Empty
                        : string.Empty;

                    // Filter by message pattern if provided.
                    if (messagePattern != null &&
                        !obsoleteMessage.Contains(messagePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Filter by exact symbol ID (documentation-comment ID) when provided.
                    // This ensures only the specific overload on the specific type is targeted,
                    // even when unrelated types have methods with the same name.
                    if (symbolId != null &&
                        methodSymbol.GetDocumentationCommentId() != symbolId)
                    {
                        continue;
                    }

                    // Use SymbolFinder to find all references to this symbol across the solution.
                    var references = await SymbolFinder.FindReferencesAsync(
                        methodSymbol, solution, cancellationToken).ConfigureAwait(false);

                    foreach (var refSymbol in references)
                    {
                        foreach (var location in refSymbol.Locations)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!location.Location.IsInSource)
                            {
                                continue;
                            }

                            var refDoc = solution.GetDocument(location.Document.Id);
                            if (refDoc == null)
                            {
                                continue;
                            }

                            var refFilePath = refDoc.FilePath ?? string.Empty;

                            // Filter by filePath if provided.
                            if (filePath != null &&
                                !refFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Skip the declaration itself.
                            if (refFilePath == (syntaxTree.FilePath ?? string.Empty) &&
                                location.Location.SourceSpan == methodDecl.Identifier.Span)
                            {
                                continue;
                            }

                            var refRoot = await location.Document.GetSyntaxRootAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (refRoot == null)
                            {
                                continue;
                            }

                            var refNode = refRoot.FindNode(location.Location.SourceSpan);
                            var refLine = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                            var snippet = Truncate(refNode.Parent?.ToString() ?? refNode.ToString());

                            // Find the nearest enclosing named declaration. Non-method containers
                            // (constructor, accessor, local function, lambda) get descriptive sentinel
                            // names so the uplift phase can surface them for manual review rather than
                            // crashing with "Method not found."
                            MethodDeclarationSyntax? callerMethod = null;
                            SyntaxNode? callerContainer = null;
                            string callerMethodName = "<top-level>";
                            foreach (var ancestor in refNode.Ancestors())
                            {
                                if (ancestor is MethodDeclarationSyntax m)
                                {
                                    callerMethod = m;
                                    callerContainer = m;
                                    callerMethodName = m.Identifier.Text;
                                    break;
                                }
                                if (ancestor is ConstructorDeclarationSyntax ctor)
                                {
                                    callerContainer = ctor;
                                    callerMethodName = ctor.Identifier.Text + " (constructor)";
                                    break;
                                }
                                if (ancestor is LocalFunctionStatementSyntax lf)
                                {
                                    callerContainer = lf;
                                    callerMethodName = lf.Identifier.Text + " (local function)";
                                    break;
                                }
                                if (ancestor is AccessorDeclarationSyntax acc)
                                {
                                    callerContainer = acc;
                                    var propName = acc.Parent is PropertyDeclarationSyntax prop
                                        ? prop.Identifier.Text
                                        : "<property>";
                                    callerMethodName = $"{propName} ({acc.Keyword.Text})";
                                    break;
                                }
                                if (ancestor is AnonymousFunctionExpressionSyntax)
                                {
                                    callerContainer = ancestor;
                                    callerMethodName = "<lambda>";
                                    break;
                                }
                            }

                            // Resolve the semantic model for the caller's document.
                            // The reference may be in a different project's compilation (cross-project
                            // reference), so we must not call compilation.GetSemanticModel() on a
                            // SyntaxTree that does not belong to this compilation.
                            var refSyntaxTree = await location.Document.GetSyntaxTreeAsync(cancellationToken)
                                .ConfigureAwait(false);

                            SemanticModel? refSemanticModel = null;
                            if (refSyntaxTree != null)
                            {
                                if (compilation.ContainsSyntaxTree(refSyntaxTree))
                                {
                                    refSemanticModel = compilation.GetSemanticModel(refSyntaxTree);
                                }
                                else
                                {
                                    // Cross-project reference: look up the owning project's compilation.
                                    var refProject = solution.GetDocument(location.Document.Id)?.Project;
                                    if (refProject != null)
                                    {
                                        var refCompilation = await refProject
                                            .GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                                        if (refCompilation != null && refCompilation.ContainsSyntaxTree(refSyntaxTree))
                                            refSemanticModel = refCompilation.GetSemanticModel(refSyntaxTree);
                                    }
                                }
                            }

                            string callerTypeName = "<unknown>";
                            if (callerContainer != null && refSemanticModel != null)
                            {
                                var callerSymbol = refSemanticModel.GetDeclaredSymbol(callerContainer, cancellationToken);
                                callerTypeName = callerSymbol?.ContainingType?.ToDisplayString() ?? "<unknown>";
                            }

                            results.Add(new ObsoleteCallerFinding(
                                ObsoleteMethodName: methodSymbol.Name,
                                SymbolId: methodSymbol.GetDocumentationCommentId() ?? "",
                                ObsoleteMessage: obsoleteMessage,
                                DeclaringType: methodSymbol.ContainingType?.ToDisplayString() ?? string.Empty,
                                CallerMethod: callerMethodName,
                                CallerType: callerTypeName,
                                FilePath: refFilePath,
                                Line: refLine,
                                CodeSnippet: snippet
                            ));
                        }
                    }
                }
            }
        }

        return results;
    }

    // ── GetAsyncMigrationProgress ─────────────────────────────────────────────

    /// <summary>
    /// Aggregates async-migration statistics for the solution or a single project.
    /// Counts total async methods, CT coverage, Asyncify-bridge wrappers, pending
    /// call sites (CS0618 sites), and async-void event handlers.
    /// </summary>
    /// <param name="projectName">
    /// When non-null, only the named project is scanned; otherwise the entire solution
    /// (excluding test and benchmark projects) is scanned.
    /// </param>
    /// <param name="cancellationToken">Propagated to all Roslyn compilation calls.</param>
    public async Task<AsyncMigrationProgressReport> GetAsyncMigrationProgressAsync(
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        var solution = _workspaceManager.CurrentSolution
            ?? throw new InvalidOperationException("No solution is loaded.");

        int totalAsync = 0;
        int withCt = 0;
        int asyncVoidHandlers = 0;
        int bridgeWrappers = 0;

        IEnumerable<Project> projects = solution.Projects;
        if (projectName != null)
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        }

        await Parallel.ForEachAsync(projects, async (project, cancellationToken) =>
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            await Parallel.ForEachAsync(project.Documents, async (document, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                if (syntaxTree == null || !compilation.ContainsSyntaxTree(syntaxTree))
                {
                    return; // generated file or excluded document — skip
                }

                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                    var returnType = method.ReturnType.ToString();
                    bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");

                    // Count async void methods (event handlers — informational only).
                    if (isAsync && method.ReturnType.IsKind(SyntaxKind.PredefinedType) &&
                        method.ReturnType.ToString() == "void")
                    {
                        asyncVoidHandlers++;
                        return; // async void methods are not counted in the async-Task bucket
                    }

                    if (!isAsync && !returnsTask)
                    {
                        return;
                    }

                    if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
                    {
                        return;
                    }

                    totalAsync++;

                    // Check for CancellationToken parameter.
                    bool hasCt = method.ParameterList.Parameters.Any(p =>
                        p.Type?.ToString() is string t &&
                        (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
                    if (hasCt)
                    {
                        withCt++;
                    }

                    // Count Asyncify-bridge wrapper methods via [Obsolete] attribute.
                    var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                    if (methodSymbol != null)
                    {
                        var obsoleteAttr = methodSymbol.GetAttributes()
                            .FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute");
                        if (obsoleteAttr != null)
                        {
                            var msg = obsoleteAttr.ConstructorArguments.Length > 0
                                ? obsoleteAttr.ConstructorArguments[0].Value?.ToString() ?? string.Empty
                                : string.Empty;
                            if (msg.Contains("Asyncify-bridge", StringComparison.OrdinalIgnoreCase))
                            {
                                bridgeWrappers++;
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        int withoutCt = totalAsync - withCt;
        double pct = totalAsync > 0 ? Math.Round((double)withCt / totalAsync * 100.0, 1) : 0.0;

        // Count pending bridge-wrapper call sites by reusing FindObsoleteCallersAsync
        // (scoped to Asyncify-bridge message, optionally scoped to project).
        var pendingCallers = await FindObsoleteCallersAsync(
            messagePattern: "Asyncify-bridge",
            filePath: null,
            projectName: projectName,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new AsyncMigrationProgressReport(
            TotalAsyncMethods: totalAsync,
            WithCancellationToken: withCt,
            WithoutCancellationToken: withoutCt,
            CancellationTokenPct: pct,
            BridgeWrappers: bridgeWrappers,
            PendingObsoleteCallers: pendingCallers.Count,
            AsyncVoidEventHandlers: asyncVoidHandlers
        );
    }
}
