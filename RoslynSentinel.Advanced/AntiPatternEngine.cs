using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Text;
using System.Security.Cryptography;

namespace RoslynSentinel.Advanced;
public record MagicValueLocation(FilePathWrapper FilePath, int Line, string Snippet);
public record MagicValueFinding(string Value, int OccurrenceCount, string SuggestedConstantName, List<MagicValueLocation> Locations);
public record OutParamMethodFinding(string MethodName, string ContainingType, FilePathWrapper FilePath, int Line, string CurrentReturnType, List<string> OutParamNames, List<string> OutParamTypes, string SuggestedTupleReturn);
public record MissingCancellationTokenFinding(string MethodName, string ContainingType, FilePathWrapper FilePath, int Line, List<string> CalleesAcceptingToken);
public record ExceptionHandlingFinding(string Pattern, string Description, string Severity, FilePathWrapper FilePath, int Line, string Snippet);
/// <summary>A call site that invokes a method decorated with <see cref = "ObsoleteAttribute"/>.</summary>
/// <param name = "ObsoleteMethodName">Simple name of the [Obsolete]-decorated method.</param>
/// <param name = "SymbolId">Roslyn documentation-comment ID uniquely identifying the bridge method (e.g. <c>M:Avaal.Service.CommonSearch.search(System.String)</c>). Pass as <c>symbolId</c> to <see cref = "AntiPatternEngine.FindObsoleteCallersAsync"/> to target this exact symbol.</param>
/// <param name = "ObsoleteMessage">The message from the [Obsolete] attribute, or empty string.</param>
/// <param name = "DeclaringType">Fully-qualified type name that declares the obsolete method.</param>
/// <param name = "CallerMethod">Name of the method containing the call site.</param>
/// <param name = "CallerType">Fully-qualified type name containing the caller.</param>
/// <param name = "FilePath">Absolute path of the file containing the call site.</param>
/// <param name = "Line">1-based line number of the call site.</param>
/// <param name = "CodeSnippet">Short source snippet around the call site.</param>
public record ObsoleteCallerFinding(string ObsoleteMethodName, string SymbolId, string ObsoleteMessage, string DeclaringType, string CallerMethod, string CallerType, FilePathWrapper FilePath, int Line, string CodeSnippet);
public class AntiPatternEngine
{
    private readonly SentinelConfiguration _config;
    private readonly IWorkspaceManager _workspaceManager;
    private static readonly HashSet<string> AllPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlockingTaskWait",
        "AsyncVoidMethod",
        "StringConcatInLoop",
        "CatchExceptionSwallow",
        "DisposedObjectUsage",
        "MissingCancellationToken",
        "MagicNumber",
        "FireAndForgetTask",
        "MissingDispose",
        "DisposedAfterUsing",
        "SyncCallInAsyncContext",
        "TaskRunBlocking",
        "NamedHandlerLeak",
        "NamedHandlerThisCapture",
        "ThrowInFinally",
        "StaticEventSubscription"
    };
    public AntiPatternEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<List<AntiPatternFinding>> DetectAntiPatternsAsync(string? filePath = null, string? projectName = null, string[]? patternFilter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
            documents = solution.Projects.Where(p => !p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) && !p.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents).Cast<Document?>();
        }

        var activePatterns = patternFilter != null && patternFilter.Length > 0 ? new HashSet<string>(patternFilter, StringComparer.OrdinalIgnoreCase) : AllPatterns;
        var findings = new List<AntiPatternFinding>();
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

            var path = document.FilePath ?? document.Name;
            if (activePatterns.Contains("BlockingTaskWait"))
            {
                var model = await document.GetSemanticModelAsync(cancellationToken);
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
    private static IEnumerable<AntiPatternFinding> DetectBlockingTaskWait(SyntaxNode root, FilePathWrapper filePath, SemanticModel? model = null)
    {
        // .Result and .Wait() -> use semantic model to verify Task/ValueTask type when available
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = ma.Name.Identifier.Text;
            if (name != "Result" && name != "Wait")
            {
                continue;
            }

            // Skip if parent is an invocation whose callee has 'Result' as a method -> e.g. IActionResult
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
                    var isTask = fullName.StartsWith("System.Threading.Tasks.Task") || fullName.StartsWith("System.Threading.Tasks.ValueTask");
                    if (!isTask)
                    {
                        continue;
                    }
                }
            }

            var line = ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(ma.ToString());
            var desc = name == "Result" ? "Accessing .Result on a Task blocks the current thread and can cause deadlocks in async contexts." : "Calling .Wait() on a Task blocks the current thread and can cause deadlocks in async contexts.";
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
            yield return new AntiPatternFinding("BlockingTaskWait", ".GetAwaiter().GetResult() synchronously blocks the thread and can cause deadlocks.", "High", filePath, line, snippet);
        }
    }

    // ── AsyncVoidMethod ───────────────────────────────────────────────────────
    private static IEnumerable<AntiPatternFinding> DetectAsyncVoidMethod(SyntaxNode root, FilePathWrapper filePath)
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
            yield return new AntiPatternFinding("AsyncVoidMethod", $"'async void' method '{method.Identifier.Text}' cannot be awaited; unhandled exceptions will crash the process.", "High", filePath, line, snippet);
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
        return (bareFirstType == "object" || bareFirstType == "Object") && secondType.EndsWith("EventArgs");
    }

    // ── StringConcatInLoop ────────────────────────────────────────────────────
    private static IEnumerable<AntiPatternFinding> DetectStringConcatInLoop(SyntaxNode root, FilePathWrapper filePath)
    {
        static bool IsInsideLoop(SyntaxNode node) => node.Ancestors().Any(a => a is ForEachStatementSyntax || a is ForStatementSyntax || a is WhileStatementSyntax || a is DoStatementSyntax);
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
            bool rhsIsString = (rhs is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) || rhs is InterpolatedStringExpressionSyntax;
            bool lhsLooksLikeString = LooksLikeStringVar(lhsText);
            if (!rhsIsString && !lhsLooksLikeString)
            {
                continue;
            }

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(assignment.ToString());
            yield return new AntiPatternFinding("StringConcatInLoop", $"String '+=' in a loop creates many intermediate allocations. Use StringBuilder instead.", "Medium", filePath, line, snippet);
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

            bool rhsIsString = (binary.Right is LiteralExpressionSyntax lit2 && lit2.IsKind(SyntaxKind.StringLiteralExpression)) || binary.Right is InterpolatedStringExpressionSyntax;
            bool lhsLooksLikeString = LooksLikeStringVar(lhsText);
            if (!rhsIsString && !lhsLooksLikeString)
            {
                continue;
            }

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(assignment.ToString());
            yield return new AntiPatternFinding("StringConcatInLoop", $"String '=' with '+' in a loop ('{lhsText} = {lhsText} + ...') creates many intermediate allocations. Use StringBuilder instead.", "Medium", filePath, line, snippet);
        }
    }

    private static bool LooksLikeStringVar(string name)
    {
        var lower = name.ToLowerInvariant().TrimStart('_');
        return lower.EndsWith("str") || lower.EndsWith("string") || lower.EndsWith("text") || lower.EndsWith("html") || lower.EndsWith("csv") || lower.EndsWith("xml") || lower.EndsWith("json") || lower.EndsWith("sql") || lower.EndsWith("sb") || lower.EndsWith("msg") || lower.EndsWith("message") || lower.EndsWith("output");
    }

    // ── CatchExceptionSwallow ─────────────────────────────────────────────────
    private static bool CatchBlockHasJustifyingComment(CatchClauseSyntax c) => c.Block.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
    private static IEnumerable<AntiPatternFinding> DetectCatchExceptionSwallow(SyntaxNode root, FilePathWrapper filePath)
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
            // indicates the developer has explicitly acknowledged the swallow -> skip.
            if (CatchBlockHasJustifyingComment(catchClause))
            {
                continue;
            }

            var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(catchClause.ToString());
            yield return new AntiPatternFinding("CatchExceptionSwallow", "Empty catch block silently swallows exceptions, hiding errors and making debugging extremely difficult.", "High", filePath, line, snippet);
        }
    }

    // ── DisposedObjectUsage ───────────────────────────────────────────────────
    private static IEnumerable<AntiPatternFinding> DetectDisposedObjectUsage(SyntaxNode root, FilePathWrapper filePath)
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
                        yield return new AntiPatternFinding("DisposedObjectUsage", $"Variable '{varExpr}' is accessed after Dispose() was called on it.", "Medium", filePath, line, Truncate(ma.ToString()));
                    }
                }

                // Then: record any new Dispose() calls found in this statement
                foreach (var inv in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is MemberAccessExpressionSyntax disposeAccess && disposeAccess.Name.Identifier.Text == "Dispose")
                    {
                        disposedVars.Add(disposeAccess.Expression.ToString());
                    }
                }
            }
        }
    }

    // ── MissingCancellationToken ──────────────────────────────────────────────
    private static IEnumerable<AntiPatternFinding> DetectMissingCancellationToken(SyntaxNode root, FilePathWrapper filePath)
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
            // so callers can cancel long-running operations -> do NOT skip them.
            var hasCt = parameters.Any(p => p.Type?.ToString()is string t && (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
            if (hasCt)
            {
                continue;
            }

            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(method.Identifier.Text + method.ParameterList.ToString());
            yield return new AntiPatternFinding("MissingCancellationToken", $"Public async method '{method.Identifier.Text}' has {parameters.Count} parameter(s) but no CancellationToken; callers cannot cancel long-running operations.", "Medium", filePath, line, snippet);
        }
    }

    // ── MagicNumber ───────────────────────────────────────────────────────────
    private static readonly HashSet<double> ExemptNumbers = new()
    {
        -1,
        0,
        1
    };
    private static IEnumerable<AntiPatternFinding> DetectMagicNumbers(SyntaxNode root, FilePathWrapper filePath)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                continue;
            }

            // Exclude field declarations, enum members, attribute arguments, switch labels
            if (literal.Ancestors().Any(a => a is FieldDeclarationSyntax || a is EnumMemberDeclarationSyntax || a is AttributeArgumentSyntax || a is CaseSwitchLabelSyntax))
            {
                continue;
            }

            // Must be inside a method/constructor/local function body
            if (!literal.Ancestors().Any(a => a is MethodDeclarationSyntax || a is ConstructorDeclarationSyntax || a is LocalFunctionStatementSyntax))
            {
                continue;
            }

            // Skip if the containing local variable declaration name suggests it is intentionally named
            var localDecl = literal.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
            if (localDecl != null)
            {
                var varName = localDecl.Declaration.Variables.FirstOrDefault()?.Identifier.Text?.ToLowerInvariant() ?? "";
                if (varName.Contains("timeout") || varName.Contains("max") || varName.Contains("min") || varName.Contains("limit") || varName.Contains("capacity") || varName.Contains("size") || varName.Contains("delay") || varName.Contains("interval") || varName.Contains("threshold"))
                {
                    continue;
                }
            }

            double numValue;
            try
            {
                numValue = Convert.ToDouble(literal.Token.Value);
            }
            catch
            {
                continue;
            }

            if (ExemptNumbers.Contains(numValue))
            {
                continue;
            }

            var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(literal.Parent?.ToString() ?? literal.ToString());
            yield return new AntiPatternFinding("MagicNumber", $"Magic number '{literal.Token.Text}' used directly in code. Extract to a named constant for clarity.", "Low", filePath, line, snippet);
        }
    }

    // ── FireAndForgetTask ─────────────────────────────────────────────────────
    private static readonly HashSet<string> TaskFireMethods = new(StringComparer.Ordinal)
    {
        "Run",
        "StartNew",
        "Factory"
    };
    private static IEnumerable<AntiPatternFinding> DetectFireAndForgetTask(SyntaxNode root, FilePathWrapper filePath)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Match Task.Run(...) and Task.Factory.StartNew(...)
            string? methodName = null;
            if (invocation.Expression is MemberAccessExpressionSyntax ma)
            {
                var receiver = ma.Expression.ToString();
                methodName = ma.Name.Identifier.Text;
                bool isTaskRun = (receiver == "Task" && methodName == "Run") || (receiver == "Task.Factory" && methodName == "StartNew");
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
            yield return new AntiPatternFinding("FireAndForgetTask", $"'{snippet}' is not awaited and not stored - exceptions thrown inside will be silently swallowed. Assign to a Task variable or await it.", "High", filePath, line, snippet);
        }
    }

    // ── MissingDispose ────────────────────────────────────────────────────────
    // Well-known IDisposable types allocated with 'new' or factory methods that
    // should always be wrapped in 'using' or try/finally.
    private static readonly HashSet<string> KnownDisposableTypes = new(StringComparer.Ordinal)
    {
        "StreamReader",
        "StreamWriter",
        "FileStream",
        "BinaryReader",
        "BinaryWriter",
        "MemoryStream",
        "BufferedStream",
        "GZipStream",
        "DeflateStream",
        "SqlConnection",
        "SqlCommand",
        "SqlDataReader",
        "SqlTransaction",
        "SqliteConnection",
        "NpgsqlConnection",
        "OleDbConnection",
        "OdbcConnection",
        "HttpClient",
        "HttpClientHandler",
        "HttpResponseMessage",
        "WebClient",
        "TcpClient",
        "UdpClient",
        "Socket",
        "Mutex",
        "Semaphore",
        "SemaphoreSlim",
        "EventWaitHandle",
        "ManualResetEvent",
        "AutoResetEvent",
        "CancellationTokenSource",
        "Timer",
        "Process",
        "RegistryKey",
        "X509Certificate",
        "X509Certificate2"
    };
    private static readonly HashSet<string> KnownDisposableFactories = new(StringComparer.Ordinal)
    {
        "OpenText",
        "OpenRead",
        "OpenWrite",
        "CreateText",
        "Open",
        "Create",
        "AppendText"
    };
    private static IEnumerable<AntiPatternFinding> DetectMissingDispose(SyntaxNode root, FilePathWrapper filePath)
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
                        ObjectCreationExpressionSyntax oc => KnownDisposableTypes.Contains(oc.Type.ToString().Split('.')[^1]),
                        // File.OpenText(...), File.OpenRead(...), etc.
                        InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax fma => KnownDisposableFactories.Contains(fma.Name.Identifier.Text),
                        _ => false
                    };
                    if (!isKnownDisposable)
                    {
                        continue;
                    }

                    // Check if this variable is contained in a using-statement block
                    bool inUsing = localDecl.Ancestors().Any(a => a is UsingStatementSyntax || (a is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)));
                    if (inUsing)
                    {
                        continue;
                    }

                    // Check if enclosed in a try/finally that calls Dispose
                    bool inTryFinally = localDecl.Ancestors().OfType<TryStatementSyntax>().Any(ts => ts.Finally?.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inv => inv.Expression is MemberAccessExpressionSyntax dma && dma.Name.Identifier.Text == "Dispose" && dma.Expression.ToString() == variable.Identifier.Text) == true);
                    if (inTryFinally)
                    {
                        continue;
                    }

                    var line = localDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var typeName = init switch
                    {
                        ObjectCreationExpressionSyntax oc => oc.Type.ToString().Split('.')[^1],
                        InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax fma => fma.Name.Identifier.Text,
                        _ => "disposable resource"
                    };
                    yield return new AntiPatternFinding("MissingDispose", $"'{variable.Identifier.Text}' ({typeName}) implements IDisposable but is not wrapped in 'using' or try/finally. Resource leak if an exception occurs.", "Medium", filePath, line, Truncate(localDecl.ToString()));
                }
            }
        }
    }

    // ── DisposedAfterUsing ────────────────────────────────────────────────────
    // Detects: variable assigned inside a using() statement body, then accessed after the block.
    // The using-statement form (using (s = expr) { }) disposes on exit but does not scope the
    // variable -> s remains accessible after and is now disposed. Use 'using var' to prevent this.
    private static IEnumerable<AntiPatternFinding> DetectDisposedAfterUsing(SyntaxNode root, FilePathWrapper filePath)
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

                var accessedAfter = statements.Skip(usingIndex + 1).SelectMany(s => s.DescendantNodes().OfType<IdentifierNameSyntax>()).Any(id => id.Identifier.Text == varName);
                if (!accessedAfter)
                {
                    continue;
                }

                var line = usingStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new AntiPatternFinding("DisposedAfterUsing", $"Variable '{varName}' is used after its 'using' block - it is already disposed. " + "Declare the variable inside the using block ('using var') to prevent this.", "High", filePath, line, Truncate(usingStmt.ToString()));
            }
        }
    }

    // ── SyncCallInAsyncContext ────────────────────────────────────────────────
    // Detects synchronous blocking API calls inside async methods where an async alternative exists.
    // Thread.Sleep -> await Task.Delay; File.ReadAllText -> await File.ReadAllTextAsync; etc.
    private static readonly Dictionary<string, string> SyncToAsyncSuggestions = new(StringComparer.Ordinal)
    {
        // Thread
        {
            "Thread.Sleep",
            "await Task.Delay(...)"
        },
        // File I/O
        {
            "File.ReadAllText",
            "await File.ReadAllTextAsync(...)"
        },
        {
            "File.WriteAllText",
            "await File.WriteAllTextAsync(...)"
        },
        {
            "File.ReadAllBytes",
            "await File.ReadAllBytesAsync(...)"
        },
        {
            "File.WriteAllBytes",
            "await File.WriteAllBytesAsync(...)"
        },
        {
            "File.ReadAllLines",
            "await File.ReadAllLinesAsync(...)"
        },
        {
            "File.WriteAllLines",
            "await File.WriteAllLinesAsync(...)"
        },
        // StreamReader/StreamWriter
        {
            "StreamReader.ReadToEnd",
            "await StreamReader.ReadToEndAsync()"
        },
        {
            "StreamWriter.Flush",
            "await StreamWriter.FlushAsync()"
        },
        // WebClient (obsolete -> prefer HttpClient)
        {
            "WebClient.DownloadString",
            "await HttpClient.GetStringAsync(...)"
        },
        {
            "WebClient.UploadString",
            "await HttpClient.PostAsync(...)"
        },
        {
            "WebClient.DownloadData",
            "await HttpClient.GetByteArrayAsync(...)"
        },
    };
    private static IEnumerable<AntiPatternFinding> DetectSyncCallInAsyncContext(SyntaxNode root, FilePathWrapper filePath)
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
                yield return new AntiPatternFinding("SyncCallInAsyncContext", $"'{fullKey}' blocks the thread inside an async method. Use '{suggestion}' instead.", "Medium", filePath, line, Truncate(invocation.ToString()));
            }
        }
    }

    // ── TaskRunBlocking ───────────────────────────────────────────────────────
    // Detects .Wait()/.Result/.GetAwaiter().GetResult() specifically on Task.Run(...)
    // This blocks one thread-pool thread waiting for ANOTHER thread-pool thread -> under
    // load the pool saturates and new work cannot start (thread starvation cascade).
    private static IEnumerable<AntiPatternFinding> DetectTaskRunBlocking(SyntaxNode root, FilePathWrapper filePath)
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
            if (receiver is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax runMa && runMa.Expression.ToString()is "Task" or "Task.Factory" && runMa.Name.Identifier.Text is "Run" or "StartNew")
            {
                isTaskRun = true;
            }

            // Variable: var t = Task.Run(...); t.Result  -> harder to track, skip for now
            if (!isTaskRun)
            {
                continue;
            }

            var line = ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(ma.ToString());
            yield return new AntiPatternFinding("TaskRunBlocking", $"'.{memberName}' on Task.Run() blocks a thread-pool thread while waiting for another thread-pool thread. " + "Under load this causes thread starvation. Use 'await Task.Run(...)' instead.", "High", filePath, line, snippet);
        }
    }

    // ── NamedHandlerLeak / NamedHandlerThisCapture ────────────────────────────
    // NamedHandlerLeak: event += this.Handler (or external.Method) without paired -= in Dispose
    // NamedHandlerThisCapture: += this.Method on an external publisher -> keeps 'this' alive
    private static IEnumerable<AntiPatternFinding> DetectNamedHandlerLeaks(SyntaxNode root, FilePathWrapper filePath, HashSet<string> activePatterns)
    {
        foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            bool implementsDisposable = classNode.BaseList?.Types.Any(t => t.ToString().Contains("IDisposable")) ?? false;
            // Collect += subscriptions where the right-hand side is a named method (not a lambda)
            var subscriptions = classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && // RHS is a method group (MemberAccess or Identifier), not lambda/anonymous
            a.Right is MemberAccessExpressionSyntax or IdentifierNameSyntax).ToList();
            if (subscriptions.Count == 0)
            {
                continue;
            }

            // Collect unsubscriptions
            var unsubKeys = new HashSet<string>(classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)).Select(a => $"{a.Left}|{a.Right}"));
            foreach (var sub in subscriptions)
            {
                // Is the event on an external object (not 'this')? Skip 'this.Event += ...' (subscribing to own events is fine)
                bool externalPublisher = sub.Left is MemberAccessExpressionSyntax lma && lma.Expression is not ThisExpressionSyntax;
                if (!externalPublisher)
                {
                    continue;
                }

                bool hasUnsubscribe = unsubKeys.Contains($"{sub.Left}|{sub.Right}");
                if (activePatterns.Contains("NamedHandlerLeak") && (!implementsDisposable || !hasUnsubscribe))
                {
                    var line = sub.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var reason = !implementsDisposable ? "class does not implement IDisposable" : "Dispose does not unsubscribe";
                    yield return new AntiPatternFinding("NamedHandlerLeak", $"'{sub.Left}' subscribed with named handler '{sub.Right}' but {reason}. " + "The publisher will keep this instance alive until the event is unsubscribed.", "Medium", filePath, line, Truncate(sub.ToString()));
                }

                // NamedHandlerThisCapture: RHS is 'this.Method' -> keeps 'this' alive via publisher
                if (activePatterns.Contains("NamedHandlerThisCapture") && sub.Right is MemberAccessExpressionSyntax rma && rma.Expression is ThisExpressionSyntax)
                {
                    var line = sub.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return new AntiPatternFinding("NamedHandlerThisCapture", $"'{sub.Left} += this.{rma.Name}' on an external publisher holds a reference to 'this'. " + "This instance will stay alive as long as the publisher lives. " + "Unsubscribe in Dispose() with '{sub.Left} -= this.{rma.Name};'", "Medium", filePath, line, Truncate(sub.ToString()));
                }
            }
        }
    }

    // ── MutablePublicApi ──────────────────────────────────────────────────────
    private static readonly HashSet<string> DtoSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Request",
        "Response",
        "Dto",
        "ViewModel",
        "Model",
        "Options",
        "Settings",
        "Config",
        "Configuration",
        "Entity",
        "Projection",
        "Args",
        "Arguments",
        "Event",
        "Command",
        "Query",
        "Message",
        "Payload"
    };
    public async Task<List<AntiPatternFinding>> FindMutablePublicPropertiesAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

        var findings = new List<AntiPatternFinding>();
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

            var path = document.FilePath ?? document.Name;
            foreach (var classDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax))
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

                    var setAccessor = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                    if (setAccessor == null)
                    {
                        continue;
                    }

                    // A private or protected setter is NOT a public API surface
                    if (setAccessor.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)))
                    {
                        continue;
                    }

                    var line = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add(new AntiPatternFinding("MutablePublicApi", $"Public class '{className}' exposes mutable property '{prop.Identifier.Text}' with a public setter. Consider init-only or a dedicated mutator method.", "Low", path, line, Truncate(prop.Identifier.Text + " { get; set; }")));
                }
            }
        }

        return findings;
    }

    // ── NamingViolation ───────────────────────────────────────────────────────
    public async Task<List<AntiPatternFinding>> FindNamingViolationsAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

        var findings = new List<AntiPatternFinding>();
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

    private static IEnumerable<AntiPatternFinding> CheckFieldNamingConventions(ClassDeclarationSyntax classDecl, FilePathWrapper filePath)
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

            bool isPrivateOrImplicit = field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) || (!field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword)));
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
                    yield return new AntiPatternFinding("NamingViolation", $"Private field '{name}' does not follow the '_camelCase' convention (e.g. '_myField').", "Low", filePath, line, name);
                }
            }
        }
    }

    private static IEnumerable<AntiPatternFinding> CheckMethodNamingConventions(ClassDeclarationSyntax classDecl, FilePathWrapper filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            bool isNonPrivate = method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword));
            if (isNonPrivate && char.IsLower(name[0]))
            {
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new AntiPatternFinding("NamingViolation", $"Method '{name}' is non-private but starts with a lowercase letter; C# convention requires PascalCase.", "Low", filePath, line, Truncate(name + method.ParameterList.ToString()));
            }
        }
    }

    private static IEnumerable<AntiPatternFinding> CheckParameterNamingConventions(ClassDeclarationSyntax classDecl, FilePathWrapper filePath)
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
                    yield return new AntiPatternFinding("NamingViolation", $"Parameter '{name}' starts with an uppercase letter; C# convention requires camelCase for parameters.", "Low", filePath, line, name);
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
    public async Task<List<MagicValueFinding>> FindStringMagicValuesAsync(string? filePath = null, string? projectName = null, int minOccurrences = 3, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

        var occurrences = new Dictionary<string, List<MagicValueLocation>>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var docPath = document.FilePath ?? document.Name;
            // Skip test files
            if (docPath.Contains("Test", StringComparison.OrdinalIgnoreCase) || docPath.Contains("Spec", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
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
                if (literal.Ancestors().OfType<InvocationExpressionSyntax>().Any(inv => inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof"))
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

        return occurrences.Where(kv => kv.Value.Count >= minOccurrences).Select(kv => new MagicValueFinding(Value: kv.Key, OccurrenceCount: kv.Value.Count, SuggestedConstantName: ToConstantName(kv.Key), Locations: kv.Value)).OrderByDescending(f => f.OccurrenceCount).ToList();
    }

    private static string ToConstantName(string value)
    {
        var segments = System.Text.RegularExpressions.Regex.Split(value, @"[^a-zA-Z0-9]+").Where(s => !string.IsNullOrEmpty(s)).Select(s => char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1).ToLowerInvariant() : ""));
        var result = string.Concat(segments);
        return string.IsNullOrEmpty(result) ? "MagicString" : result;
    }

    // ── FindMissingCancellationTokens ─────────────────────────────────────────
    public async Task<List<MissingCancellationTokenFinding>> FindMissingCancellationTokensAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
            documents = solution.Projects.Where(p => !p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) && !p.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents).Cast<Document?>();
        }

        var findings = new List<MissingCancellationTokenFinding>();
        foreach (var document in documents)
        {
            if (document == null)
            {
                continue;
            }

            var docPath = document.FilePath ?? document.Name;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken);
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
                bool hasCt = method.ParameterList.Parameters.Any(p => p.Type?.ToString()is string t && (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
                if (hasCt)
                {
                    continue;
                }

                // Skip event handlers -> their delegate signature is fixed (object sender, XxxEventArgs e)
                // and cannot be extended with a CancellationToken parameter.
                if (IsEventHandlerSignature(method))
                {
                    continue;
                }

                var body = (SyntaxNode? )method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                var calleesAcceptingToken = new List<string>();
                var seenCallees = new HashSet<string>();
                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var si = model.GetSymbolInfo(invocation, cancellationToken);
                    var callee = si.Symbol as IMethodSymbol ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
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

                var containingType = model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name ?? "";
                var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new MissingCancellationTokenFinding(MethodName: method.Identifier.Text, ContainingType: containingType, FilePath: docPath, Line: line, CalleesAcceptingToken: calleesAcceptingToken));
            }
        }

        return findings;
    }

    // ── AnalyzeExceptionHandling ──────────────────────────────────────────────
    public async Task<List<ExceptionHandlingFinding>> AnalyzeExceptionHandlingAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        var findings = new List<ExceptionHandlingFinding>();
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

            var path = document.FilePath ?? document.Name;
            foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var snippet = Truncate(catchClause.ToString());
                var typeName = catchClause.Declaration?.Type.ToString();
                // 1. CatchAll
                bool isCatchAll = catchClause.Declaration == null || typeName == "Exception" || typeName == "System.Exception";
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

                    findings.Add(new ExceptionHandlingFinding("CatchAll", "Catching System.Exception (or bare catch) swallows all exception types, including those that should propagate." + suggestion, "High", path, line, snippet));
                }

                // 2. EmptyRethrow: catch (Exception ex) { throw ex; }
                var statements = catchClause.Block.Statements;
                if (statements.Count == 1 && statements[0] is ThrowStatementSyntax throwStmt && throwStmt.Expression != null)
                {
                    var catchVar = catchClause.Declaration?.Identifier.Text;
                    if (catchVar != null && throwStmt.Expression.ToString() == catchVar)
                    {
                        findings.Add(new ExceptionHandlingFinding("EmptyRethrow", $"'throw {catchVar};' loses the original stack trace. Use 'throw;' to preserve it.", "High", path, line, snippet));
                    }
                }

                // 3. SwallowedException: no rethrow, no log, no return
                bool hasRethrow = statements.Any(s => s is ThrowStatementSyntax t && t.Expression == null);
                bool hasThrowExpr = statements.Any(s => s is ThrowStatementSyntax t && t.Expression != null);
                bool hasReturn = statements.Any(s => s is ReturnStatementSyntax);
                bool hasLog = catchClause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inv =>
                {
                    var text = inv.ToString();
                    return text.Contains("Log") || text.Contains("log") || text.Contains("Console.Write") || text.Contains("Debug.Write");
                });
                if (!hasRethrow && !hasThrowExpr && !hasReturn && !hasLog)
                {
                    bool isEmpty = statements.Count == 0;
                    // An empty block with a comment (/* best-effort */, // intentional) is
                    // acknowledged by the developer -> downgrade to Info rather than High.
                    bool hasComment = CatchBlockHasJustifyingComment(catchClause);
                    var severity = isEmpty ? (hasComment ? "Info" : "High") : "Medium";
                    var desc = isEmpty ? (hasComment ? "Empty catch block with justifying comment. Verify the swallow is truly intentional." : "Empty catch block silently swallows the exception with no logging, rethrowing, or error return.") : "Exception is caught but neither rethrown nor logged, making the failure invisible.";
                    findings.Add(new ExceptionHandlingFinding("SwallowedException", desc, severity, path, line, snippet));
                }

                // 4. ExceptionAsControlFlow: catch of validation-type exceptions inside a loop
                bool isInsideLoop = catchClause.Ancestors().Any(a => a is ForEachStatementSyntax || a is ForStatementSyntax || a is WhileStatementSyntax || a is DoStatementSyntax);
                if (isInsideLoop && typeName != null)
                {
                    bool isExpectedExType = typeName is "FormatException" or "System.FormatException" or "ParseException" or "InvalidCastException" or "System.InvalidCastException" or "OverflowException" or "System.OverflowException" or "ArgumentException" or "System.ArgumentException";
                    if (isExpectedExType)
                    {
                        findings.Add(new ExceptionHandlingFinding("ExceptionAsControlFlow", $"Catching '{typeName}' inside a loop uses exceptions for control flow. Use TryParse/TryXxx methods instead.", "Medium", path, line, snippet));
                    }
                }
            }

            // 5. throw new Exception("message") -> too broad; suggest specific exception type
            foreach (var throwStmt in root.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression is not ObjectCreationExpressionSyntax oc)
                {
                    continue;
                }

                if (oc.Type.ToString()is not ("Exception" or "System.Exception"))
                {
                    continue;
                }

                var msgArg = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                var suggested = InferSpecificExceptionType(msgArg?.Token.ValueText);
                var desc = suggested != null ? $"'throw new Exception()' is too broad. Based on the message, consider 'throw new {suggested}(...)' or a custom exception type." : "'throw new Exception()' is too broad. Use a specific BCL exception (ArgumentException, InvalidOperationException, etc.) or create a custom exception class.";
                var tLoc = throwStmt.GetLocation().GetLineSpan().StartLinePosition;
                findings.Add(new ExceptionHandlingFinding("GenericThrowExpression", desc, "Medium", path, tLoc.Line + 1, Truncate(throwStmt.ToString())));
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
                    if (anc is TryStatementSyntax || anc is UsingStatementSyntax)
                    {
                        isProtected = true;
                        break;
                    }

                    if (anc is LocalDeclarationStatementSyntax lds3 && lds3.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                    {
                        isProtected = true;
                        break;
                    }
                }

                if (!isProtected)
                {
                    var dLoc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    findings.Add(new ExceptionHandlingFinding("UnprotectedDispose", "Explicit .Dispose() call is not protected by try/catch. If Dispose() throws, cleanup is incomplete. Prefer 'using', or wrap in try { } catch { /* log */ }.", "Medium", path, dLoc.Line + 1, Truncate(inv.ToString())));
                }
            }

            // 7. Dispose() implementation with multiple unprotected sub-Dispose calls
            // If one sub-resource throws in Dispose(), the others are never released.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "Dispose" && m.ParameterList.Parameters.Count == 0))
            {
                if (method.Body == null)
                {
                    continue;
                }

                var unprotected = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(inv => inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Dispose" && inv.ArgumentList.Arguments.Count == 0 && !IsProtectedByTryWithin(inv, method)).ToList();
                if (unprotected.Count >= 2)
                {
                    var mLoc = method.GetLocation().GetLineSpan().StartLinePosition;
                    findings.Add(new ExceptionHandlingFinding("UnsafeDisposeImplementation", $"Dispose() calls {unprotected.Count} sub-resources without individual try/catch. If one throws, the remaining resources are not disposed. Wrap each .Dispose() call in its own try/catch.", "Medium", path, mLoc.Line + 1, "Dispose()"));
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
            if (expr.Contains("File.") || expr.Contains("Directory.") || expr.Contains("Stream") || expr.Contains("ReadAllText") || expr.Contains("WriteAllText") || expr.Contains("ReadAllBytes"))
            {
                AddIfAbsent(suggestions, "IOException");
                AddIfAbsent(suggestions, "UnauthorizedAccessException");
            }

            if (expr.Contains("HttpClient") || expr.Contains("GetAsync") || expr.Contains("PostAsync") || expr.Contains("PutAsync") || expr.Contains("SendAsync") || expr.Contains("GetStringAsync"))
            {
                AddIfAbsent(suggestions, "HttpRequestException");
                AddIfAbsent(suggestions, "TaskCanceledException");
            }

            if (expr.Contains(".Parse(") && !expr.Contains("Enum.Parse"))
            {
                AddIfAbsent(suggestions, "FormatException");
                AddIfAbsent(suggestions, "OverflowException");
            }

            if (expr.Contains("SqlCommand") || expr.Contains("ExecuteReader") || expr.Contains("ExecuteNonQuery") || expr.Contains("ExecuteScalar") || expr.Contains(".Open()"))
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

        if (tryBlock.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(b => b.IsKind(SyntaxKind.DivideExpression)))
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

    public async Task<List<AntiPatternFinding>> FindLongParameterListAsync(string? filePath = null, string? projectName = null, int minParameters = 4, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var diSuffixes = new[]
        {
            "Service",
            "Repository",
            "Options",
            "Factory"
        };
        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
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
                    bool allDi = parameters.All(p => p.Type != null && diSuffixes.Any(s => p.Type.ToString().EndsWith(s)));
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
                results.Add(new AntiPatternFinding("LongParameterList", $"'{memberName}' has {parameters.Count} parameters ({paramNames}). Consider introducing a Parameter Object.", "Medium", doc.FilePath, lineSpan.StartLinePosition.Line + 1, method.ParameterList.ToString()));
            }
        }

        return results;
    }

    public async Task<List<AntiPatternFinding>> FindPrimitiveObsessionAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            var normalizedPath = Path.GetFullPath(filePath);
            documents = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument);
            if (!documents.Any(d => d != null))
            {
                documents = solution.Projects.SelectMany(p => p.Documents).Where(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase)).Cast<Document?>();
            }
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

        var primitiveTypes = new HashSet<string>
        {
            "string",
            "int",
            "long",
            "Guid",
            "bool",
            "String",
            "Int32",
            "Int64",
            "Boolean"
        };
        var results = new List<AntiPatternFinding>();
        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var parameters = method.ParameterList.Parameters.Where(p => p.Modifiers.All(m => !m.IsKind(SyntaxKind.ParamsKeyword)) && p.Type != null).ToList();
                var typeCounts = parameters.GroupBy(p => p.Type!.ToString()).Where(g => primitiveTypes.Contains(g.Key) && g.Count() >= 3).ToList();
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
                    results.Add(new AntiPatternFinding("PrimitiveObsession", $"'{memberName}' has {group.Count()} parameters of type '{group.Key}': {paramNames}. Consider a dedicated type.", "Medium", doc.FilePath, lineSpan.StartLinePosition.Line + 1, method.ParameterList.ToString()));
                }
            }
        }

        return results;
    }

    public async Task<List<AntiPatternFinding>> FindInconsistentAsyncSuffixAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

        static bool IsAsync(MethodDeclarationSyntax m) => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)) || IsTaskReturning(m);
        static bool IsEventHandler(MethodDeclarationSyntax m)
        {
            var name = m.Identifier.Text;
            return name.StartsWith("On") || (m.ParameterList.Parameters.Count == 2 && m.ParameterList.Parameters[1].Type?.ToString().Contains("EventArgs") == true);
        }

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
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
                    results.Add(new AntiPatternFinding("InconsistentAsyncSuffix", $"Method '{name}' is async/Task-returning but does not end with 'Async'. Rename to '{name}Async'.", "Low", doc.FilePath, lineSpan.StartLinePosition.Line + 1, name));
                }
                else if (!isAsync && hasAsyncSuffix)
                {
                    results.Add(new AntiPatternFinding("InconsistentAsyncSuffix", $"Method '{name}' ends with 'Async' but is not async and does not return Task/ValueTask. Remove 'Async' suffix.", "Low", doc.FilePath, lineSpan.StartLinePosition.Line + 1, name));
                }
            }
        }

        return results;
    }

    // ── ThrowInFinally ────────────────────────────────────────────────────────
    // Throwing from a finally block suppresses the original exception; the caller
    // sees the finally-throw instead of the real error, destroying stack context.
    private static IEnumerable<AntiPatternFinding> DetectThrowInFinally(SyntaxNode root, FilePathWrapper filePath)
    {
        foreach (var tryStmt in root.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (tryStmt.Finally == null)
            {
                continue;
            }

            foreach (var throwStmt in tryStmt.Finally.Block.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                // Bare `throw;` re-throws current exception -> that is fine
                if (throwStmt.Expression == null)
                {
                    continue;
                }

                var loc = throwStmt.GetLocation().GetLineSpan().StartLinePosition;
                yield return new AntiPatternFinding("ThrowInFinally", "Throwing in a finally block silently swallows the original exception. " + "The caller sees the finally-throw instead of the real error, losing the original stack trace. " + "Move error handling into catch blocks.", "Warning", filePath, loc.Line + 1, throwStmt.ToString());
            }
        }
    }

    // ── StaticEventSubscription ───────────────────────────────────────────────
    // Subscribing an instance method to a static event without unsubscribing in
    // Dispose pins the instance in memory for the lifetime of the AppDomain.
    private static IEnumerable<AntiPatternFinding> DetectStaticEventSubscription(SyntaxNode root, FilePathWrapper filePath)
    {
        // Collect unsubscribe targets: everything on the RHS of a -= assignment
        var unsubscribeTargets = root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)).Select(a => a.Right.ToString()).ToHashSet(StringComparer.Ordinal);
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

            // Heuristic: receiver starts with uppercase letter -> likely a class name (static access)
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
            yield return new AntiPatternFinding("StaticEventSubscription", $"Subscribing '{rhsText}' to static event '{lhsMa}' without a paired '-=' in Dispose. " + "Static events hold references to subscribers for the lifetime of the AppDomain, preventing GC. " + "Unsubscribe in Dispose() or use a WeakEventManager.", "Warning", filePath, loc.Line + 1, assignment.ToString());
        }
    }

    // ── Multiple out-parameter methods ────────────────────────────────────────
    public async Task<List<OutParamMethodFinding>> FindMultipleOutParameterMethodsAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var model = await doc.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var outParams = method.ParameterList.Parameters.Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword))).ToList();
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
                results.Add(new OutParamMethodFinding(method.Identifier.Text, containingType, doc.FilePath, line, currentReturn, outParamNames, outParamTypes, suggestedReturn));
            }
        }

        return results;
    }

    // ── Value-type mutation intent warnings ───────────────────────────────────
    public async Task<List<AntiPatternFinding>> FindValueTypeMutationIntentAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            var normalizedPath = Path.GetFullPath(filePath);
            documents = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument);
            if (!documents.Any(d => d != null))
            {
                documents = solution.Projects.SelectMany(p => p.Documents).Where(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase)).Cast<Document?>();
            }
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

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var model = await doc.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                continue;
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var body = (SyntaxNode? )method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                // Map parameter name -> parameter symbol for fast lookup
                var paramSymbols = new Dictionary<string, (IParameterSymbol Symbol, bool IsValueType)>();
                foreach (var p in method.ParameterList.Parameters)
                {
                    // Skip ref/out/in -> those are intentionally pass-by-reference
                    if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword)))
                    {
                        continue;
                    }

                    if (model.GetDeclaredSymbol(p, cancellationToken)is not IParameterSymbol sym)
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
                    var resolvedSym = model.GetSymbolInfo(lhsId, cancellationToken).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(resolvedSym, entry.Symbol))
                    {
                        continue;
                    }

                    var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    bool isReturned = returnedSymbols.Contains(paramName);
                    if (entry.IsValueType && !isReturned)
                    {
                        // Value type param reassigned but not returned -> caller will never see the change
                        results.Add(new AntiPatternFinding("ValueTypeParameterReassigned", $"Parameter '{paramName}' ({entry.Symbol.Type.Name}) is a value type reassigned inside the method but not returned. " + "The caller's copy is unaffected. If caller visibility was the intent, use 'ref' or return the value.", "Warning", doc.FilePath, line, Truncate(assignment.ToString())));
                    }
                    else if (!entry.IsValueType && !isReturned)
                    {
                        // Reference type param replaced with new instance -> caller's reference is unaffected
                        // Only flag if RHS is an object creation (new ...) to reduce false positives
                        bool rhsIsNewInstance = assignment.Right is ObjectCreationExpressionSyntax || assignment.Right is ImplicitObjectCreationExpressionSyntax;
                        if (rhsIsNewInstance)
                        {
                            results.Add(new AntiPatternFinding("ReferenceTypeParameterReplaced", $"Parameter '{paramName}' ({entry.Symbol.Type.Name}) is replaced with a new instance but the new reference is not returned. " + "The caller's reference still points to the original object. If the intent was to return the new instance, add it to the return value.", "Warning", doc.FilePath, line, Truncate(assignment.ToString())));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds all call sites that invoke a method decorated with <see cref = "ObsoleteAttribute"/>.
    /// Useful for tracking CS0618 migration progress -> every result is a caller that still needs
    /// to be migrated away from the deprecated (bridge) method.
    /// </summary>
    /// <param name = "messagePattern">Optional substring to filter by the [Obsolete] message text (case-insensitive).</param>
    /// <param name = "filePath">Optional: restrict results to call sites in this file.</param>
    /// <param name = "projectName">Optional: restrict results to call sites in this project.</param>
    /// <param name = "cancellationToken">Cancellation token.</param>
    public async Task<List<ObsoleteCallerFinding>> FindObsoleteCallersAsync(string? messagePattern = null, string? filePath = null, string? projectName = null, string? symbolId = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

                    var obsoleteAttr = methodSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "ObsoleteAttribute" or "Obsolete");
                    if (obsoleteAttr == null)
                    {
                        continue;
                    }

                    // Extract the message text from the attribute constructor.
                    var obsoleteMessage = obsoleteAttr.ConstructorArguments.Length > 0 ? obsoleteAttr.ConstructorArguments[0].Value?.ToString() ?? string.Empty : string.Empty;
                    // Filter by message pattern if provided.
                    if (messagePattern != null && !obsoleteMessage.Contains(messagePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Filter by exact symbol ID (documentation-comment ID) when provided.
                    // This ensures only the specific overload on the specific type is targeted,
                    // even when unrelated types have methods with the same name.
                    if (symbolId != null && methodSymbol.GetDocumentationCommentId() != symbolId)
                    {
                        continue;
                    }

                    // Use SymbolFinder to find all references to this symbol across the solution.
                    var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken).ConfigureAwait(false);
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
                            if (filePath != null && !refFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Skip the declaration itself.
                            if (refFilePath == (syntaxTree.FilePath ?? string.Empty) && location.Location.SourceSpan == methodDecl.Identifier.Span)
                            {
                                continue;
                            }

                            var refRoot = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
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
                                    var propName = acc.Parent is PropertyDeclarationSyntax prop ? prop.Identifier.Text : "<property>";
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
                            var refSyntaxTree = await location.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
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
                                        var refCompilation = await refProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                                        if (refCompilation != null && refCompilation.ContainsSyntaxTree(refSyntaxTree))
                                        {
                                            refSemanticModel = refCompilation.GetSemanticModel(refSyntaxTree);
                                        }
                                    }
                                }
                            }

                            string callerTypeName = "<unknown>";
                            if (callerContainer != null && refSemanticModel != null)
                            {
                                var callerSymbol = refSemanticModel.GetDeclaredSymbol(callerContainer, cancellationToken);
                                callerTypeName = callerSymbol?.ContainingType?.ToDisplayString() ?? "<unknown>";
                            }

                            results.Add(new ObsoleteCallerFinding(ObsoleteMethodName: methodSymbol.Name, SymbolId: methodSymbol.GetDocumentationCommentId() ?? "", ObsoleteMessage: obsoleteMessage, DeclaringType: methodSymbol.ContainingType?.ToDisplayString() ?? string.Empty, CallerMethod: callerMethodName, CallerType: callerTypeName, FilePath: refFilePath, Line: refLine, CodeSnippet: snippet));
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
    /// <param name = "projectName">
    /// When non-null, only the named project is scanned; otherwise the entire solution
    /// (excluding test and benchmark projects) is scanned.
    /// </param>
    /// <param name = "cancellationToken">Propagated to all Roslyn compilation calls.</param>
    public async Task<AsyncMigrationProgressReport> GetAsyncMigrationProgressAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken) ?? throw new InvalidOperationException("No solution is loaded.");
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
                    return; // generated file or excluded document - skip
                }

                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                    var returnType = method.ReturnType.ToString();
                    bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");
                    // Count async void methods (event handlers -> informational only).
                    if (isAsync && method.ReturnType.IsKind(SyntaxKind.PredefinedType) && method.ReturnType.ToString() == "void")
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
                    bool hasCt = method.ParameterList.Parameters.Any(p => p.Type?.ToString()is string t && (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
                    if (hasCt)
                    {
                        withCt++;
                    }

                    // Count Asyncify-bridge wrapper methods via [Obsolete] attribute.
                    var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                    if (methodSymbol != null)
                    {
                        var obsoleteAttr = methodSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute");
                        if (obsoleteAttr != null)
                        {
                            var msg = obsoleteAttr.ConstructorArguments.Length > 0 ? obsoleteAttr.ConstructorArguments[0].Value?.ToString() ?? string.Empty : string.Empty;
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
        var pendingCallers = await FindObsoleteCallersAsync(messagePattern: "Asyncify-bridge", filePath: null, projectName: projectName, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AsyncMigrationProgressReport(TotalAsyncMethods: totalAsync, WithCancellationToken: withCt, WithoutCancellationToken: withoutCt, CancellationTokenPct: pct, BridgeWrappers: bridgeWrappers, PendingObsoleteCallers: pendingCallers.Count, AsyncVoidEventHandlers: asyncVoidHandlers);
    }

    private async Task<IEnumerable<(Document Document, SyntaxNode Root, SemanticModel? SemanticModel)>> GetTargetDocumentsAsync(Solution solution, string? projectName, string? filePath, bool includeSemantic = false, CancellationToken cancellationToken = default)
    {
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        var documentList = new List<(Document, SyntaxNode, SemanticModel? )>();
        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (!string.IsNullOrEmpty(filePath))
            {
                docs = docs.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var doc in docs)
            {
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    continue;
                }

                var model = includeSemantic ? await doc.GetSemanticModelAsync(cancellationToken) : null;
                documentList.Add((doc, root, model));
            }
        }

        return documentList;
    }

    public async Task<List<LargeTypeReport>> FindLargeTypesAsync(int maxLines = 500, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeTypes"))
        {
            return new List<LargeTypeReport>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var reports = new List<LargeTypeReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var types = target.Root.DescendantNodes().OfType<TypeDeclarationSyntax>();
            foreach (var type in types)
            {
                var lines = type.GetLocation().GetLineSpan().EndLinePosition.Line - type.GetLocation().GetLineSpan().StartLinePosition.Line;
                if (lines > maxLines)
                {
                    reports.Add(new LargeTypeReport(target.Document.FilePath ?? target.Document.Name, type.Identifier.Text, lines));
                }
            }
        }

        return reports.OrderByDescending(r => r.LineCount).ToList();
    }

    public async Task<List<LargeMethodReport>> FindLargeMethodsAsync(int maxLines = 50, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeMethods"))
        {
            return new List<LargeMethodReport>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var reports = new List<LargeMethodReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var lines = method.GetLocation().GetLineSpan().EndLinePosition.Line - method.GetLocation().GetLineSpan().StartLinePosition.Line;
                if (lines > maxLines)
                {
                    var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Global";
                    reports.Add(new LargeMethodReport(target.Document.FilePath ?? target.Document.Name, typeName, method.Identifier.Text, lines));
                }
            }
        }

        return reports.OrderByDescending(r => r.LineCount).ToList();
    }

    public async Task<List<DuplicateMethodGroup>> FindDuplicateMethodsAsync(int minStatements = 5, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("DuplicateMethods"))
        {
            return new List<DuplicateMethodGroup>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var methodHashes = new Dictionary<string, List<MethodLocation>>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Body != null && m.Body.Statements.Count >= minStatements);
            foreach (var method in methods)
            {
                var hash = ComputeStructuralHash(method.Body!);
                if (!methodHashes.TryGetValue(hash, out List<MethodLocation>? value))
                {
                    value = new List<MethodLocation>();
                    methodHashes[hash] = value;
                }

                var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Global";
                value.Add(new MethodLocation(target.Document.FilePath ?? target.Document.Name, typeName, method.Identifier.Text));
            }
        }

        return methodHashes.Where(kvp => kvp.Value.Count > 1).Select(kvp => new DuplicateMethodGroup(kvp.Key, kvp.Value)).OrderByDescending(g => g.Locations.Count).ToList();
    }

    public async Task<List<InterfaceCandidateReport>> FindInterfaceExtractionCandidatesAsync(int minPublicMethods = 3, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UnusedInterfaces"))
        {
            return new List<InterfaceCandidateReport>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var candidates = new List<InterfaceCandidateReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var classes = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) && c.BaseList == null);
            foreach (var classNode in classes)
            {
                var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword))).Select(m => m.Identifier.Text).ToList();
                if (publicMethods.Count >= minPublicMethods)
                {
                    candidates.Add(new InterfaceCandidateReport(target.Document.FilePath ?? target.Document.Name, classNode.Identifier.Text, publicMethods));
                }
            }
        }

        return candidates;
    }

    public async Task<List<string>> DetectLongParameterListsAsync(int threshold = 5, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LongParameterLists"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.ParameterList.Parameters.Count > threshold);
            foreach (var method in methods)
            {
                results.Add($"Method '{method.Identifier.Text}' in {target.Document.Name} has {method.ParameterList.Parameters.Count} parameters.");
            }
        }

        return results;
    }

    public async Task<List<string>> DetectUnreachableCodeAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new List<string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new List<string>();
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new List<string>();
        }

        var diagnostics = semanticModel.GetDiagnostics(method.Span, cancellationToken);
        return diagnostics.Where(d => d.Id == "CS0162").Select(d => $"Unreachable code: {d.GetMessage()}").ToList();
    }

    private bool HasCycle(ProjectId current, HashSet<ProjectId> visited, List<ProjectId> path, Dictionary<ProjectId, Project> projects)
    {
        if (path.Contains(current))
        {
            return true;
        }

        if (visited.Contains(current))
        {
            return false;
        }

        visited.Add(current);
        path.Add(current);
        foreach (var reference in projects[current].ProjectReferences)
        {
            if (HasCycle(reference.ProjectId, visited, path, projects))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    public async Task<DocumentEditResult> GenerateCallTreeAsync(FilePathWrapper filePath, string methodName, int depth = 3, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = $"// File not found: {filePath}"};
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        // Parse methodName which may be in format "ClassName.MethodName" or just "MethodName"
        string? className = null;
        string actualMethodName = methodName;
        if (methodName.Contains('.'))
        {
            var parts = methodName.Split('.');
            if (parts.Length == 2)
            {
                className = parts[0];
                actualMethodName = parts[1];
            }
        }

        // Find the method(s) matching the criteria
        IEnumerable<MethodDeclarationSyntax> candidateMethods = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == actualMethodName) ?? Enumerable.Empty<MethodDeclarationSyntax>();
        // If className was provided, filter to implementations in that class
        if (className != null)
        {
            candidateMethods = candidateMethods.Where(m =>
            {
                var classNode = m.Parent;
                while (classNode != null && classNode is not ClassDeclarationSyntax && classNode is not StructDeclarationSyntax)
                {
                    classNode = classNode.Parent;
                }

                if (classNode is ClassDeclarationSyntax classDecl)
                {
                    return classDecl.Identifier.Text == className;
                }

                if (classNode is StructDeclarationSyntax structDecl)
                {
                    return structDecl.Identifier.Text == className;
                }

                return false;
            });
        }

        var methodNode = candidateMethods.FirstOrDefault();
        if (methodNode == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        if (methodSymbol == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var sb = new StringBuilder();
        await BuildCallTree(methodSymbol, 0, depth, sb, new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = sb.ToString(),
            FilePath = filePath
        };
    }

    private async Task BuildCallTree(IMethodSymbol symbol, int currentDepth, int maxDepth, StringBuilder sb, HashSet<ISymbol> visited, CancellationToken cancellationToken = default)
    {
        if (currentDepth > maxDepth || !visited.Add(symbol))
        {
            return;
        }

        var indent = new string (' ', currentDepth * 2);
        sb.AppendLine($"{indent}- {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = await syntaxRef.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(node.SyntaxTree);
            if (document == null)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                continue;
            }

            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(inv, cancellationToken);
                if (info.Symbol is IMethodSymbol callee && callee.ContainingAssembly?.Name == symbol.ContainingAssembly?.Name)
                {
                    await BuildCallTree(callee, currentDepth + 1, maxDepth, sb, visited, cancellationToken);
                }
            }
        }
    }

    public async Task<DocumentEditResult> GenerateEqualityOverridesAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (root == null || classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        // Gather fields with their types (private instance fields only -> skip const, static, backing fields)
        var fieldsWithTypes = classNode.Members.OfType<FieldDeclarationSyntax>().Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.StaticKeyword))).SelectMany(f => f.Declaration.Variables.Select(v => (Name: v.Identifier.Text, Type: f.Declaration.Type.ToString()))).ToList();
        // Prefer auto-properties when available -> they represent the class's semantic identity
        var propertyFields = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true).Select(p => (Name: p.Identifier.Text, Type: p.Type.ToString())).ToList();
        if (propertyFields.Count > 0)
        {
            fieldsWithTypes = propertyFields;
        }

        if (fieldsWithTypes.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var fieldNames = fieldsWithTypes.Select(f => f.Name).ToList();
        // Build: obj is ClassName other
        ExpressionSyntax body = SyntaxFactory.IsPatternExpression(SyntaxFactory.IdentifierName("obj"), SyntaxFactory.DeclarationPattern(SyntaxFactory.IdentifierName(className), SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other"))));
        // Chain: && field == other.field  (or SequenceEqual for collection types)
        foreach (var(name, typeName)in fieldsWithTypes)
        {
            ExpressionSyntax equality;
            if (IsCollectionType(typeName))
            {
                // Use Enumerable.SequenceEqual for collection types (reference equality is wrong)
                equality = SyntaxFactory.ParseExpression($"Enumerable.SequenceEqual({name} ?? Enumerable.Empty<{GetElementType(typeName)}>(), other.{name} ?? Enumerable.Empty<{GetElementType(typeName)}>())");
            }
            else
            {
                equality = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, SyntaxFactory.IdentifierName(name), SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("other"), SyntaxFactory.IdentifierName(name)));
            }

            body = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, body.WithTrailingTrivia(SyntaxFactory.Space), equality);
        }

        var equalsMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "Equals").AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)).AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj")).WithType(SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))))).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        // Build GetHashCode: HashCode.Combine(f1, f2, ...) -> handles up to 8 args natively
        ExpressionSyntax hashBody;
        if (fieldNames.Count <= 8)
        {
            hashBody = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("HashCode"), SyntaxFactory.IdentifierName("Combine"))).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(fieldNames.Select(n => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(n))))));
        }
        else
        {
            // For >8 fields use a HashCode builder
            // Emit: { var hc = new HashCode(); hc.Add(f1); ...; return hc.ToHashCode(); }
            var statements = new List<StatementSyntax>
            {
                SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")).AddVariables(SyntaxFactory.VariableDeclarator("hc").WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("HashCode")).WithArgumentList(SyntaxFactory.ArgumentList())))))
            };
            foreach (var name in fieldNames)
            {
                statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("hc"), SyntaxFactory.IdentifierName("Add"))).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name)))))));
            }

            statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("hc"), SyntaxFactory.IdentifierName("ToHashCode")))));
            var getHashBlockBody = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), "GetHashCode").AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)).WithBody(SyntaxFactory.Block(statements));
            var newClassForBlock = classNode.AddMembers(equalsMethod, getHashBlockBody);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, classNode, newClassForBlock, cancellationToken),
                FilePath = filePath
            };
        }

        var getHashMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), "GetHashCode").AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(hashBody)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var newClass = classNode.AddMembers(equalsMethod, getHashMethod);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, classNode, newClass, cancellationToken),
            FilePath = filePath
        };
    }

    private static bool IsCollectionType(string typeName)
    {
        var t = typeName.Trim().TrimEnd('?');
        return t.EndsWith("[]") || t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IEnumerable<") || t.StartsWith("ICollection<") || t.StartsWith("IReadOnlyList<") || t.StartsWith("IReadOnlyCollection<") || t.StartsWith("HashSet<") || t.StartsWith("SortedSet<") || t.StartsWith("Collection<");
    }

    private static string GetElementType(string typeName)
    {
        var t = typeName.Trim().TrimEnd('?');
        if (t.EndsWith("[]"))
        {
            return t[..^2];
        }

        var open = t.IndexOf('<');
        var close = t.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            return t[(open + 1)..close];
        }

        return "object";
    }

    public async Task<List<string>> AnalyzeSemaphoreUsageAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SemaphoreLeaks"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, true, cancellationToken);
        foreach (var target in targets)
        {
            var semaphoreWaits = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(inv => inv.ToString().Contains(".WaitAsync("));
            foreach (var wait in semaphoreWaits)
            {
                // Semantic verification: confirm the receiver is actually SemaphoreSlim, not some
                // other class that happens to have a WaitAsync() method.
                if (target.SemanticModel != null && wait.Expression is MemberAccessExpressionSyntax waitMa)
                {
                    var receiverType = target.SemanticModel.GetTypeInfo(waitMa.Expression, cancellationToken).Type;
                    if (receiverType != null && !SemanticTypeHelper.IsSemaphoreSlim(receiverType))
                    {
                        continue;
                    }
                }

                var method = wait.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var containingType = wait.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (method == null)
                {
                    continue;
                }

                // If this method already contains its own Release call, it handles the semaphore correctly.
                // Use ".Release" (without parens) to catch both Release() and Release(n).
                bool methodHandlesOwnRelease = method.ToString().Contains(".Release(");
                if (methodHandlesOwnRelease)
                {
                    continue;
                }

                // Check if ANY other member of the class contains a release call.
                // Covers methods, constructors, properties, and finalizers -> catches helper-method patterns.
                bool classHasReleaseElsewhere = containingType?.Members.Where(m => !ReferenceEquals(m, method)).Any(m => m.ToString().Contains(".Release(")) == true;
                if (!classHasReleaseElsewhere && target.SemanticModel != null && containingType != null)
                {
                    // Second pass: follow 1-level-deep method calls made from class members.
                    // Catches the pattern where Release is delegated to a helper in another class:
                    // e.g. this.Return() calls _helper.ReleaseSlot(_sem) which calls _sem.Release().
                    classHasReleaseElsewhere = containingType.Members.Where(m => !ReferenceEquals(m, method)).SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()).Any(inv =>
                    {
                        var calledMethod = target.SemanticModel.GetSymbolInfo(inv, cancellationToken).Symbol as IMethodSymbol;
                        if (calledMethod == null)
                        {
                            return false;
                        }

                        var syntaxRef = calledMethod.DeclaringSyntaxReferences.FirstOrDefault();
                        return syntaxRef?.GetSyntax(cancellationToken).ToString().Contains(".Release(") == true;
                    });
                }

                if (classHasReleaseElsewhere)
                {
                    // Pool pattern -> semaphore lifetime spans method boundaries intentionally.
                    // Report as advisory so callers know to verify the release path is always reachable.
                    results.Add($"Advisory (pool pattern): '{method.Identifier.Text}' in {target.Document.Name} acquires a semaphore slot; Release() is in another method of the same class. Verify the release path is always reachable (e.g., via try/finally or a paired return method).");
                }
                else
                {
                    // Genuine leak -> WaitAsync is called but no Release() exists anywhere in the class.
                    results.Add($"Semaphore leak in '{method.Identifier.Text}' in {target.Document.Name}: WaitAsync() is called but no Release() was found in this class - pool slots will be permanently lost on exceptions.");
                }
            }
        }

        return results;
    }

    public async Task<List<string>> FindPossibleInfiniteLoopsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, false, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            // while (true) { ... }
            foreach (var loop in target.Root.DescendantNodes().OfType<WhileStatementSyntax>().Where(w => w.Condition is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.TrueLiteralExpression)))
            {
                if (!HasExitStatement(loop.Statement))
                {
                    var method = loop.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var line = loop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{line} " + $"- Potential infinite loop (while(true)) in '{method?.Identifier.Text ?? "<unknown>"}' - no break/return/throw found.");
                }
            }

            // for (;;) { ... } -> ForStatement with no condition
            foreach (var loop in target.Root.DescendantNodes().OfType<ForStatementSyntax>().Where(f => f.Condition == null))
            {
                if (!HasExitStatement(loop.Statement))
                {
                    var method = loop.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var line = loop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{line} " + $"- Potential infinite loop (for(;;)) in '{method?.Identifier.Text ?? "<unknown>"}' - no break/return/throw found.");
                }
            }
        }

        return results;
    }

    private static bool HasExitStatement(StatementSyntax body) => body.DescendantNodes().OfType<BreakStatementSyntax>().Any() || body.DescendantNodes().OfType<ReturnStatementSyntax>().Any() || body.DescendantNodes().OfType<ThrowStatementSyntax>().Any() || body.DescendantNodes().OfType<GotoStatementSyntax>().Any();
    public async Task<List<string>> DetectMismatchedAwaitAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MismatchedAwait"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            // Pre-collect all variables in this document that feed Task.WhenAll/WhenAny
            var whenAllVars = new HashSet<string>();
            foreach (var whenAllInv in target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var invName = whenAllInv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                if (invName is "WhenAll" or "WhenAny")
                {
                    foreach (var arg in whenAllInv.ArgumentList.Arguments)
                    {
                        if (arg.Expression is IdentifierNameSyntax argId)
                        {
                            whenAllVars.Add(argId.Identifier.Text);
                        }
                    }
                }
            }

            // Pre-collect variables that are awaited anywhere in the document.
            // var t = DoAsync(); ... await t; -> t should not be flagged at the assignment site.
            var awaitedLocalVars = new HashSet<string>();
            foreach (var awaitExpr in target.Root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                if (awaitExpr.Expression is IdentifierNameSyntax awaitedId)
                {
                    awaitedLocalVars.Add(awaitedId.Identifier.Text);
                }
            }

            var invocations = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var symbol = target.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                ITypeSymbol? returnType;
                string invocationName;
                if (symbol != null)
                {
                    returnType = symbol.ReturnType;
                    invocationName = symbol.Name;
                }
                else
                {
                    // Delegate invocations (e.g. Func<Task> factory = ...; factory()) return null from GetSymbolInfo.
                    // Fall back to the invocation expression's type to catch unawaited delegate calls.
                    returnType = target.SemanticModel.GetTypeInfo(invocation, cancellationToken).Type;
                    invocationName = invocation.Expression.ToString();
                }

                if (returnType == null || !SemanticTypeHelper.IsTaskOrValueTask(returnType))
                {
                    continue;
                }

                // Direct await
                if (invocation.Parent is AwaitExpressionSyntax)
                {
                    continue;
                }

                // await expr! -> null-forgiving wraps the invocation; parent is PostfixUnary, grandparent is Await
                if (invocation.Parent is PostfixUnaryExpressionSyntax pue && pue.IsKind(SyntaxKind.SuppressNullableWarningExpression) && pue.Parent is AwaitExpressionSyntax)
                {
                    continue;
                }

                // Skip: assigned to a discard (  _ = SomeAsync()  )
                if (invocation.Parent is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax discardId && discardId.Identifier.Text == "_")
                {
                    continue;
                }

                // Skip: the invocation IS the return expression (expression-bodied or return statement).
                // e.g.  public Task<T> FooAsync() => Task.FromResult(x);
                //        return Task.FromResult(x);
                // These are not fire-and-forget -> the Task is propagated to the caller.
                if (invocation.Parent is ArrowExpressionClauseSyntax)
                {
                    continue;
                }

                if (invocation.Parent is ReturnStatementSyntax)
                {
                    continue;
                }

                // Skip: the invocation IS the entire body of a lambda expression.
                // Covers Moq setup chains: .Setup(x => x.FooAsync(...)).ReturnsAsync(...)
                // and similar fluent API patterns where the Task is implicitly returned.
                if (invocation.Parent is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax)
                {
                    continue;
                }

                // Skip: the invocation is an argument to a new ValueTask<T>(...) constructor.
                // e.g.  cancellationToken => new ValueTask<T>(GetByIdFromDbAsync(id, cancellationToken))
                // The Task is propagated to the caller via the ValueTask wrapper.
                if (invocation.Parent is ArgumentSyntax && invocation.Parent.Parent?.Parent is ObjectCreationExpressionSyntax valueTaskCtor && valueTaskCtor.Type.ToString().Contains("ValueTask"))
                {
                    continue;
                }

                // Skip: assigned to a local variable that is later passed to Task.WhenAll/WhenAny
                if (invocation.Parent is EqualsValueClauseSyntax evc && evc.Parent is VariableDeclaratorSyntax vd && whenAllVars.Contains(vd.Identifier.Text))
                {
                    continue;
                }

                // Skip: direct argument to Task.WhenAll / Task.WhenAny / Task.WhenEach
                // e.g. await Task.WhenAll(RemoveAsync(id), UpdateAsync(id))
                if (invocation.Parent is ArgumentSyntax directArg && directArg.Parent?.Parent is InvocationExpressionSyntax composerInv && composerInv.Expression is MemberAccessExpressionSyntax composerMa && composerMa.Name.Identifier.Text is "WhenAll" or "WhenAny" or "WhenEach")
                {
                    continue;
                }

                // Skip: Task / ValueTask factory methods -> synchronous, no actual async work
                // e.g. Task.FromResult(x), Task.FromException(ex), Task.FromCanceled(cancellationToken)
                if (invocation.Expression is MemberAccessExpressionSyntax factoryMa && (factoryMa.Expression.ToString()is "Task" or "ValueTask") && factoryMa.Name.Identifier.Text is "FromResult" or "FromException" or "FromCanceled")
                {
                    continue;
                }

                // Skip: await using -> the await keyword is on the using declaration, not the invocation directly
                // e.g.  await using var conn = OpenConnectionAsync(cancellationToken);
                var ancestorLocalDecl = invocation.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
                if (ancestorLocalDecl?.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) == true)
                {
                    continue;
                }

                // Skip: the invocation is the receiver in a method chain consumed by the chain itself
                // e.g. MethodAsync().ContinueWith(...)  -> the returned Task feeds the chain
                // Also skip .GetAwaiter().GetResult() chains -> these are intentional blocking
                // sync-over-async wrappers (bridge pattern). find_blocking_calls_in covers them.
                if (invocation.Parent is MemberAccessExpressionSyntax chainedMa && chainedMa.Parent is InvocationExpressionSyntax chainedCall && chainedMa.Name.Identifier.Text is "ContinueWith" or "Unwrap" or "ConfigureAwait" or "AsTask" or "GetAwaiter" or "GetResult")
                {
                    continue;
                }

                // Skip: result stored in a local variable that is later awaited in this document
                // e.g. var t = DoAsync(); ... await t;
                if (invocation.Parent is EqualsValueClauseSyntax evc2 && evc2.Parent is VariableDeclaratorSyntax vd2 && awaitedLocalVars.Contains(vd2.Identifier.Text))
                {
                    continue;
                }

                // Skip: result stored in a field or property (deferred consumption).
                // Covers: this._task = X()  (MemberAccess) and  _task = X()  (Identifier, underscore convention).
                if (invocation.Parent is AssignmentExpressionSyntax fieldAssign && (fieldAssign.Left is MemberAccessExpressionSyntax || (fieldAssign.Left is IdentifierNameSyntax lhsId && lhsId.Identifier.Text.StartsWith('_'))))
                {
                    continue;
                }

                // Skip: invocation is inside a ternary or null-coalescing expression
                // The Task value is used as a value in the expression, likely returned or stored.
                if (invocation.Ancestors().OfType<ConditionalExpressionSyntax>().Any())
                {
                    continue;
                }

                if (invocation.Parent is BinaryExpressionSyntax coalesceOp && coalesceOp.IsKind(SyntaxKind.CoalesceExpression))
                {
                    continue;
                }

                // Skip: invocation inside an anonymous method expression
                // e.g. Action fn = delegate { DoWorkAsync(); };
                if (invocation.Ancestors().OfType<AnonymousMethodExpressionSyntax>().Any())
                {
                    continue;
                }

                // Skip: invocation inside an object or collection initializer
                // e.g. new Foo { BackgroundTask = StartAsync() }
                if (invocation.Ancestors().OfType<InitializerExpressionSyntax>().Any())
                {
                    continue;
                }

                // Skip: argument to collection/task-composition methods that consume tasks
                // e.g. _tasks.Add(DoWorkAsync())  or  Task.Run(DoWorkAsync)
                if (invocation.Parent is ArgumentSyntax taskConsumerArg && taskConsumerArg.Parent?.Parent is InvocationExpressionSyntax taskConsumerInv && taskConsumerInv.Expression is MemberAccessExpressionSyntax taskConsumerMa && taskConsumerMa.Name.Identifier.Text is "Add" or "TryAdd" or "Append" or "Push" or "Enqueue" or "Run" or "StartNew" or "Schedule" or "Post")
                {
                    continue;
                }

                results.Add($"Potential mismatched await in {target.Document.Name} at line {invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Call to '{invocationName}' is not awaited.");
            }
        }

        return results;
    }

    // A catch block with a comment inside (e.g., /* best-effort */, // intentional) is
    // considered justified -> the developer has explicitly acknowledged the swallow.
    private static bool HasJustifyingComment(BlockSyntax block) => block.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
    private static bool IsTestFile(string? filePath) => filePath != null && (filePath.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) || filePath.Contains("Tests\\", StringComparison.OrdinalIgnoreCase) || filePath.Contains("Tests/", StringComparison.OrdinalIgnoreCase) || filePath.Contains("TestFactory", StringComparison.OrdinalIgnoreCase) || filePath.Contains("TestHelper", StringComparison.OrdinalIgnoreCase));
    public async Task<List<string>> CheckForEmptyCatchBlocksAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("EmptyCatchBlocks"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        foreach (var target in targets)
        {
            // Test teardown patterns (e.g. catch (IOException){} on Directory.Delete) are
            // intentional best-effort cleanup -> no value flagging them in test infrastructure.
            if (IsTestFile(target.Document.FilePath))
            {
                continue;
            }

            var catchBlocks = target.Root.DescendantNodes().OfType<CatchClauseSyntax>().Where(c => c.Block.Statements.Count == 0 && !HasJustifyingComment(c.Block));
            results.AddRange(catchBlocks.Select(c => $"Empty catch block in {target.Document.Name} at line {c.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Silent failures are risky."));
        }

        return results;
    }

    public async Task<List<string>> FindLargeSwitchStatementsAsync(int threshold = 10, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeSwitchStatements"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        foreach (var target in targets)
        {
            var switches = target.Root.DescendantNodes().OfType<SwitchStatementSyntax>().Where(s => s.Sections.Count > threshold);
            foreach (var sw in switches)
            {
                results.Add($"Large switch statement in {target.Document.Name} has {sw.Sections.Count} cases. Consider refactoring.");
            }
        }

        return results;
    }

    public async Task<List<string>> CheckForRedundantCastAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("RedundantCasts"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            var casts = target.Root.DescendantNodes().OfType<CastExpressionSyntax>();
            foreach (var cast in casts)
            {
                var typeInfo = target.SemanticModel.GetTypeInfo(cast.Expression, cancellationToken);
                var castTypeInfo = target.SemanticModel.GetTypeInfo(cast, cancellationToken);
                if (typeInfo.Type != null && castTypeInfo.Type != null && SymbolEqualityComparer.Default.Equals(typeInfo.Type, castTypeInfo.Type))
                {
                    results.Add($"Redundant cast in {target.Document.Name} at line {cast.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Expression is already of type {castTypeInfo.Type.Name}.");
                }
            }
        }

        return results;
    }

    public async Task<List<string>> DetectReflectionUsageAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ReflectionUsage"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        foreach (var target in targets)
        {
            var memberAccesses = target.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
            foreach (var access in memberAccesses)
            {
                var name = access.Name.Identifier.Text;
                if (name is "GetProperty" or "GetMethod" or "GetField" or "GetCustomAttribute" or "Invoke")
                {
                    results.Add($"[REFLECTION] Potential reflection usage in {target.Document.Name} at line {access.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (.{name}). Reflection can impact performance and AOT compatibility.");
                }
            }
        }

        return results;
    }

    public async Task<List<string>> FindPossibleDeadlocksAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("Deadlocks"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        foreach (var target in targets)
        {
            var lockStatements = target.Root.DescendantNodes().OfType<LockStatementSyntax>();
            foreach (var lockStmt in lockStatements)
            {
                if (lockStmt.DescendantNodes().OfType<LockStatementSyntax>().Any())
                {
                    results.Add($"Potential deadlock risk: Nested lock statement found at line {lockStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1} in {target.Document.Name}.");
                }
            }
        }

        return results;
    }

    private static readonly HashSet<string> UnboundedCollectionTypes = new(StringComparer.Ordinal)
    {
        "Dictionary",
        "List",
        "HashSet",
        "SortedDictionary",
        "SortedSet",
        "ConcurrentDictionary",
        "ConcurrentBag",
        "ConcurrentQueue",
        "Queue",
        "Stack",
        "LinkedList"
    };
    /// <summary>
    /// Detects static fields that hold unbounded collections (Dictionary, List, etc.) with
    /// no size cap, Clear(), or expiry -> a memory exhaustion DoS vector when populated from
    /// user-controlled data. Flags when: static field is a known collection type AND the class
    /// adds to it (.Add / .TryAdd) but never calls .Clear() or checks Count against a limit.
    /// </summary>
    public async Task<List<string>> FindUnboundedStaticCollectionsAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                // Find static fields of known collection types
                var staticCollectionFields = classDecl.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))).SelectMany(f => f.Declaration.Variables.Select(v => new { Name = v.Identifier.Text, TypeName = f.Declaration.Type.ToString().Split('<')[0].Split('.')[^1] })).Where(x => UnboundedCollectionTypes.Contains(x.TypeName)).ToList();
                if (staticCollectionFields.Count == 0)
                {
                    continue;
                }

                var classText = classDecl.ToString();
                foreach (var field in staticCollectionFields)
                {
                    // Must be populated via Add/TryAdd anywhere in the class
                    bool isPopulated = classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inv => inv.Expression is MemberAccessExpressionSyntax ma && ma.Expression.ToString().Contains(field.Name) && ma.Name.Identifier.Text is "Add" or "TryAdd" or "TryGetOrAdd" or "Enqueue" or "Push");
                    if (!isPopulated)
                    {
                        continue;
                    }

                    // Flag if there's no Clear(), no Count check, and no capacity/max constant
                    bool hasClear = classText.Contains(field.Name + ".Clear()");
                    bool hasCountCheck = classDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(ma => ma.Expression.ToString().Contains(field.Name) && ma.Name.Identifier.Text == "Count");
                    bool hasMaxConstant = classDecl.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)) && f.Declaration.Variables.Any(v => v.Identifier.Text.Contains("max", StringComparison.OrdinalIgnoreCase) || v.Identifier.Text.Contains("limit", StringComparison.OrdinalIgnoreCase)));
                    if (!hasClear && !hasCountCheck && !hasMaxConstant)
                    {
                        var loc = classDecl.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add($"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " + $"- Static field '{field.Name}' ({field.TypeName}) in '{classDecl.Identifier.Text}' " + $"is populated without a size cap or Clear() call. " + $"This can cause unbounded memory growth (DoS) on user-controlled input.");
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Detects directly recursive methods -> methods that call themselves on every code path
    /// without a depth parameter or an early-return base case that does NOT itself recurse.
    /// Unbounded recursion causes StackOverflowException on deep or adversarial input.
    /// </summary>
    public async Task<List<string>> FindUnboundedRecursionAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            var model = target.SemanticModel;
            foreach (var method in target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body == null && method.ExpressionBody == null)
                {
                    continue;
                }

                var methodName = method.Identifier.Text;
                IMethodSymbol? containingSymbol = model?.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                // Find self-recursive calls -> use semantic model to skip overload chaining
                SyntaxNode body = (SyntaxNode? )method.Body ?? method.ExpressionBody!;
                var selfCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(inv =>
                {
                    var name = inv.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        _ => null
                    };
                    if (name != methodName)
                    {
                        return false;
                    }

                    // When semantic model is available, verify same overload
                    if (containingSymbol != null && model != null)
                    {
                        var info = model.GetSymbolInfo(inv, cancellationToken);
                        var calledSymbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                        if (calledSymbol != null && !SymbolEqualityComparer.Default.Equals(calledSymbol.OriginalDefinition, containingSymbol.OriginalDefinition))
                        {
                            return false;
                        }
                    }

                    return true;
                }).ToList();
                if (selfCalls.Count == 0)
                {
                    continue;
                }

                // Look for a depth parameter (int depth, int level, int maxDepth, etc.)
                bool hasDepthParam = method.ParameterList.Parameters.Any(p => p.Identifier.Text.Equals("depth", StringComparison.OrdinalIgnoreCase) || p.Identifier.Text.Equals("level", StringComparison.OrdinalIgnoreCase) || p.Identifier.Text.Equals("maxdepth", StringComparison.OrdinalIgnoreCase) || p.Identifier.Text.Equals("currentdepth", StringComparison.OrdinalIgnoreCase) || p.Identifier.Text.Equals("recursionlevel", StringComparison.OrdinalIgnoreCase) || p.Identifier.Text.Equals("limit", StringComparison.OrdinalIgnoreCase));
                // Look for an early-return guard (if ... return without self-call)
                bool hasBaseCase = false;
                if (method.Body != null)
                {
                    foreach (var ifStmt in method.Body.DescendantNodes().OfType<IfStatementSyntax>())
                    {
                        // The if-statement's then/else contains a return/throw but no recursive call
                        var ifBody = ifStmt.Statement.ToString() + (ifStmt.Else?.Statement.ToString() ?? "");
                        bool hasSelfCall = selfCalls.Any(sc => ifBody.Contains(methodName + "("));
                        bool hasReturn = ifStmt.Statement.DescendantNodesAndSelf().Any(n => n is ReturnStatementSyntax or ThrowStatementSyntax);
                        if (hasReturn && !hasSelfCall)
                        {
                            hasBaseCase = true;
                            break;
                        }
                    }
                }

                if (!hasDepthParam && !hasBaseCase)
                {
                    var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " + $"- Method '{methodName}' recurses without a depth guard or base case. " + $"Deep or adversarial input will cause StackOverflowException. " + $"Add a depth parameter or a non-recursive early-return guard.");
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Validates overload chains across the solution. For each group of same-named methods,
    /// reports: missing parameter forwarding, inverted argument order, and mutual-delegation
    /// cycles between overloads. Uses semantic model to identify chain calls precisely.
    /// </summary>
    public async Task<List<string>> FindMisboundOverloadChainsAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            var findings = StackOverflowEngine.DetectMisboundOverloadChains(target.Root, target.SemanticModel, target.Document.FilePath ?? target.Document.Name);
            foreach (var f in findings)
            {
                results.Add($"{f.FilePath}:{f.LineNumber} [{f.Kind}] - {f.Description}. Fix: {f.Recommendation}");
            }
        }

        return results;
    }

    /// <summary>
    /// Detects generic type parameters that are used in the method body in ways that imply
    /// a missing constraint -> for example, null-comparing T without "where T : class",
    /// or calling new T() without "where T : new()".  These are not compile errors but are
    /// design gaps that can surprise callers and lead to confusing runtime exceptions.
    /// </summary>
    public async Task<List<string>> FindMissingGenericConstraintsAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            foreach (var method in target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.TypeParameterList?.Parameters.Any() == true)
                {
                    continue;
                }

                if (method.Body == null && method.ExpressionBody == null)
                {
                    continue;
                }

                foreach (var typeParam in method.TypeParameterList!.Parameters)
                {
                    var tName = typeParam.Identifier.Text;
                    // Find existing constraints declared for this type parameter
                    var constraintClause = method.ConstraintClauses.FirstOrDefault(cc => cc.Name.Identifier.Text == tName);
                    var existingConstraints = constraintClause?.Constraints.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                    SyntaxNode bodyNode = (SyntaxNode? )method.Body ?? method.ExpressionBody!;
                    // Collect parameter names whose declared type is exactly this type parameter.
                    // These are the expressions whose null comparison would be meaningless for value types.
                    var paramNamesOfT = method.ParameterList.Parameters.Where(p => p.Type?.ToString() == tName).Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
                    // Check: body uses 'new T()' but no 'new()' constraint
                    bool usesNew = bodyNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(oc => oc.Type.ToString() == tName);
                    if (usesNew && !existingConstraints.Contains("new()"))
                    {
                        var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add($"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " + $"- Method '{method.Identifier.Text}': type parameter '{tName}' is instantiated " + $"with 'new {tName}()' but is missing 'where {tName} : new()' constraint.");
                    }

                    if (paramNamesOfT.Count == 0)
                    {
                        continue; // no parameters typed as T - skip null checks
                    }

                    // Check: a parameter of type T is compared to null, but no 'class' constraint exists.
                    // Value types can never be null, so this comparison is always false for structs.
                    bool comparesToNull = bodyNode.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(b => (b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)) && ((b.Left is IdentifierNameSyntax li && paramNamesOfT.Contains(li.Identifier.Text) && b.Right is LiteralExpressionSyntax rn && rn.IsKind(SyntaxKind.NullLiteralExpression)) || (b.Right is IdentifierNameSyntax ri && paramNamesOfT.Contains(ri.Identifier.Text) && b.Left is LiteralExpressionSyntax ln && ln.IsKind(SyntaxKind.NullLiteralExpression))));
                    // Also catch 'x is null' / 'x is not null' patterns on T-typed params
                    bool isNullPattern = bodyNode.DescendantNodes().OfType<IsPatternExpressionSyntax>().Any(ip => ip.Expression is IdentifierNameSyntax id && paramNamesOfT.Contains(id.Identifier.Text));
                    bool hasClassConstraint = existingConstraints.Contains("class") || existingConstraints.Contains("notnull") || existingConstraints.Any(c => c.StartsWith("class"));
                    if ((comparesToNull || isNullPattern) && !hasClassConstraint)
                    {
                        var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add($"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " + $"- Method '{method.Identifier.Text}': type parameter '{tName}' is compared to null " + $"but is missing 'where {tName} : class' constraint - value types will never be null.");
                    }
                }
            }
        }

        return results;
    }

    private string ComputeStructuralHash(BlockSyntax body)
    {
        var kinds = body.DescendantNodes().Select(n => (int)n.Kind());
        var bytes = kinds.SelectMany(BitConverter.GetBytes).ToArray();
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }
}