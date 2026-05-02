using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record AntiPatternFinding(
    string Pattern,
    string Description,
    string Severity,
    string FilePath,
    int Line,
    string Snippet
);

public record MagicValueFinding(
    string Value,
    int OccurrenceCount,
    string SuggestedConstantName,
    List<(string FilePath, int Line, string Snippet)> Locations
);

public record MissingCancellationTokenFinding(
    string MethodName,
    string ContainingType,
    string FilePath,
    int Line,
    List<string> CalleesAcceptingToken
);

public record ExceptionHandlingFinding(
    string Pattern,
    string Description,
    string Severity,
    string FilePath,
    int Line,
    string Snippet
);

public class AntiPatternEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    private static readonly HashSet<string> AllPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlockingTaskWait", "AsyncVoidMethod", "StringConcatInLoop",
        "CatchExceptionSwallow", "DisposedObjectUsage", "MissingCancellationToken", "MagicNumber"
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
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var activePatterns = patternFilter != null && patternFilter.Length > 0
            ? new HashSet<string>(patternFilter, StringComparer.OrdinalIgnoreCase)
            : AllPatterns;

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            var path = document.FilePath ?? document.Name;

            if (activePatterns.Contains("BlockingTaskWait"))
            {
                var model = await document.GetSemanticModelAsync(ct);
                findings.AddRange(DetectBlockingTaskWait(root, path, model));
            }

            if (activePatterns.Contains("AsyncVoidMethod"))
                findings.AddRange(DetectAsyncVoidMethod(root, path));

            if (activePatterns.Contains("StringConcatInLoop"))
                findings.AddRange(DetectStringConcatInLoop(root, path));

            if (activePatterns.Contains("CatchExceptionSwallow"))
                findings.AddRange(DetectCatchExceptionSwallow(root, path));

            if (activePatterns.Contains("DisposedObjectUsage"))
                findings.AddRange(DetectDisposedObjectUsage(root, path));

            if (activePatterns.Contains("MissingCancellationToken"))
                findings.AddRange(DetectMissingCancellationToken(root, path));

            if (activePatterns.Contains("MagicNumber"))
                findings.AddRange(DetectMagicNumbers(root, path));
        }

        return findings;
    }

    // ── BlockingTaskWait ──────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectBlockingTaskWait(SyntaxNode root, string filePath, SemanticModel? model = null)
    {
        // .Result and .Wait() — use semantic model to verify Task/ValueTask type when available
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = ma.Name.Identifier.Text;
            if (name != "Result" && name != "Wait") continue;

            // Skip if parent is an invocation whose callee has 'Result' as a method — e.g. IActionResult
            if (name == "Result" && ma.Parent is InvocationExpressionSyntax)
                continue;

            // Skip if .Result is on the left side of an assignment (property setter, not Task.Result read)
            // e.g. context.Result = new UnauthorizedResult()
            if (name == "Result" && ma.Parent is AssignmentExpressionSyntax assign && assign.Left == ma)
                continue;

            // Use semantic model to verify the expression is a Task/ValueTask type
            if (name == "Result" && model != null)
            {
                var exprType = model.GetTypeInfo(ma.Expression).Type;
                if (exprType != null)
                {
                    var fullName = exprType.OriginalDefinition.ToDisplayString();
                    var isTask = fullName.StartsWith("System.Threading.Tasks.Task") ||
                                 fullName.StartsWith("System.Threading.Tasks.ValueTask");
                    if (!isTask) continue;
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
            if (invocation.Expression is not MemberAccessExpressionSyntax outer) continue;
            if (outer.Name.Identifier.Text != "GetResult") continue;
            if (outer.Expression is not InvocationExpressionSyntax getAwaiterCall) continue;
            if (getAwaiterCall.Expression is not MemberAccessExpressionSyntax inner) continue;
            if (inner.Name.Identifier.Text != "GetAwaiter") continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(invocation.ToString());
            yield return new AntiPatternFinding("BlockingTaskWait",
                ".GetAwaiter().GetResult() synchronously blocks the thread and can cause deadlocks.",
                "High", filePath, line, snippet);
        }
    }

    // ── AsyncVoidMethod ───────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectAsyncVoidMethod(SyntaxNode root, string filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) continue;
            if (method.ReturnType.ToString() != "void") continue;
            if (IsEventHandlerSignature(method)) continue;

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
        if (parameters.Count != 2) return false;
        var firstType = parameters[0].Type?.ToString() ?? "";
        var secondType = parameters[1].Type?.ToString() ?? "";
        // Standard pattern: (object sender, XxxEventArgs e)
        return (firstType == "object" || firstType == "Object") &&
               secondType.EndsWith("EventArgs");
    }

    // ── StringConcatInLoop ────────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectStringConcatInLoop(SyntaxNode root, string filePath)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)) continue;

            // Must be inside a loop
            if (!assignment.Ancestors().Any(a =>
                    a is ForEachStatementSyntax ||
                    a is ForStatementSyntax ||
                    a is WhileStatementSyntax ||
                    a is DoStatementSyntax))
                continue;

            // Determine whether this looks like string concatenation
            var lhsText = assignment.Left.ToString();
            var rhs = assignment.Right;
            bool rhsIsString = rhs is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
                               || rhs is InterpolatedStringExpressionSyntax;
            bool lhsLooksLikeString = LooksLikeStringVar(lhsText);

            if (!rhsIsString && !lhsLooksLikeString) continue;

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(assignment.ToString());
            yield return new AntiPatternFinding("StringConcatInLoop",
                $"String '+=' in a loop creates many intermediate allocations. Use StringBuilder instead.",
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

    private static IEnumerable<AntiPatternFinding> DetectCatchExceptionSwallow(SyntaxNode root, string filePath)
    {
        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            // Include bare `catch {}` (no declaration) and `catch (Exception ...)` blocks
            if (catchClause.Declaration != null)
            {
                var typeName = catchClause.Declaration.Type.ToString();
                if (typeName != "Exception" && !typeName.EndsWith(".Exception"))
                    continue;
            }

            if (catchClause.Block.Statements.Count > 0) continue;

            var line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(catchClause.ToString());
            yield return new AntiPatternFinding("CatchExceptionSwallow",
                "Empty catch block silently swallows exceptions, hiding errors and making debugging extremely difficult.",
                "High", filePath, line, snippet);
        }
    }

    // ── DisposedObjectUsage ───────────────────────────────────────────────────

    private static IEnumerable<AntiPatternFinding> DetectDisposedObjectUsage(SyntaxNode root, string filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body == null) continue;

            var disposedVars = new HashSet<string>();

            foreach (var statement in method.Body.Statements)
            {
                // First: flag any member access on already-disposed variables (excluding the dispose call itself)
                if (disposedVars.Count > 0)
                {
                    foreach (var ma in statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                    {
                        if (ma.Name.Identifier.Text == "Dispose") continue;
                        var varExpr = ma.Expression.ToString();
                        if (!disposedVars.Contains(varExpr)) continue;

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

    private static IEnumerable<AntiPatternFinding> DetectMissingCancellationToken(SyntaxNode root, string filePath)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) continue;
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

            var returnType = method.ReturnType.ToString();
            if (!returnType.StartsWith("Task") && !returnType.StartsWith("ValueTask")) continue;

            var parameters = method.ParameterList.Parameters;
            if (parameters.Count < 3) continue;

            var hasCt = parameters.Any(p =>
                p.Type?.ToString() is string t &&
                (t == "CancellationToken" || t.EndsWith(".CancellationToken")));

            if (hasCt) continue;

            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(method.Identifier.Text + method.ParameterList.ToString());
            yield return new AntiPatternFinding("MissingCancellationToken",
                $"Public async method '{method.Identifier.Text}' has {parameters.Count} parameters but no CancellationToken; callers cannot cancel long-running operations.",
                "Medium", filePath, line, snippet);
        }
    }

    // ── MagicNumber ───────────────────────────────────────────────────────────

    private static readonly HashSet<double> ExemptNumbers = new() { -1, 0, 1 };

    private static IEnumerable<AntiPatternFinding> DetectMagicNumbers(SyntaxNode root, string filePath)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression)) continue;

            // Exclude field declarations, enum members, attribute arguments, switch labels
            if (literal.Ancestors().Any(a =>
                    a is FieldDeclarationSyntax ||
                    a is EnumMemberDeclarationSyntax ||
                    a is AttributeArgumentSyntax ||
                    a is CaseSwitchLabelSyntax))
                continue;

            // Must be inside a method/constructor/local function body
            if (!literal.Ancestors().Any(a =>
                    a is MethodDeclarationSyntax ||
                    a is ConstructorDeclarationSyntax ||
                    a is LocalFunctionStatementSyntax))
                continue;

            // Skip if the containing local variable declaration name suggests it is intentionally named
            var localDecl = literal.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
            if (localDecl != null)
            {
                var varName = localDecl.Declaration.Variables.FirstOrDefault()?.Identifier.Text?.ToLowerInvariant() ?? "";
                if (varName.Contains("timeout") || varName.Contains("max") || varName.Contains("min") ||
                    varName.Contains("limit") || varName.Contains("capacity") || varName.Contains("size") ||
                    varName.Contains("delay") || varName.Contains("interval") || varName.Contains("threshold"))
                    continue;
            }

            double numValue;
            try { numValue = Convert.ToDouble(literal.Token.Value); }
            catch { continue; }

            if (ExemptNumbers.Contains(numValue)) continue;

            var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = Truncate(literal.Parent?.ToString() ?? literal.ToString());
            yield return new AntiPatternFinding("MagicNumber",
                $"Magic number '{literal.Token.Text}' used directly in code. Extract to a named constant for clarity.",
                "Low", filePath, line, snippet);
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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            var path = document.FilePath ?? document.Name;

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

                var className = classDecl.Identifier.Text;
                if (DtoSuffixes.Any(s => className.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

                foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (!prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;
                    if (prop.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;

                    var setAccessor = prop.AccessorList?.Accessors
                        .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                    if (setAccessor == null) continue;

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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var findings = new List<AntiPatternFinding>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

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
        ClassDeclarationSyntax classDecl, string filePath)
    {
        foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) continue;
            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;

            bool isPrivateOrImplicit =
                field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) ||
                (!field.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.PublicKeyword) ||
                    m.IsKind(SyntaxKind.ProtectedKeyword) ||
                    m.IsKind(SyntaxKind.InternalKeyword)));

            if (!isPrivateOrImplicit) continue;

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.Text;
                if (name.Contains('<') || name.Contains('>')) continue; // compiler-generated
                if (name == "_") continue; // discard

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
        ClassDeclarationSyntax classDecl, string filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            if (string.IsNullOrEmpty(name)) continue;

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
        ClassDeclarationSyntax classDecl, string filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            foreach (var param in method.ParameterList.Parameters)
            {
                var name = param.Identifier.Text;
                if (string.IsNullOrEmpty(name) || name == "_") continue;
                if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword))) continue;

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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var occurrences = new Dictionary<string, List<(string FilePath, int Line, string Snippet)>>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            if (document == null) continue;
            var docPath = document.FilePath ?? document.Name;

            // Skip test files
            if (docPath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                docPath.Contains("Spec", StringComparison.OrdinalIgnoreCase))
                continue;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;

                var value = literal.Token.ValueText;
                if (string.IsNullOrWhiteSpace(value) || value.Length < 3) continue;

                // Skip inside nameof()
                if (literal.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(inv => inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof"))
                    continue;

                // Skip inside attribute constructor args
                if (literal.Ancestors().Any(a => a is AttributeArgumentSyntax)) continue;

                // Skip inside using directives
                if (literal.Ancestors().Any(a => a is UsingDirectiveSyntax)) continue;

                var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var snippet = Truncate(literal.Parent?.ToString() ?? literal.ToString());

                if (!occurrences.TryGetValue(value, out var list))
                {
                    list = new List<(string, int, string)>();
                    occurrences[value] = list;
                }
                list.Add((docPath, line, snippet));
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
            .Select(s => char.ToUpper(s[0]) + (s.Length > 1 ? s.Substring(1).ToLower() : ""));
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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var findings = new List<MissingCancellationTokenFinding>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var docPath = document.FilePath ?? document.Name;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) continue;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                var returnType = method.ReturnType.ToString();
                bool returnsTask = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");
                if (!isAsync && !returnsTask) continue;

                // Skip abstract methods (no body)
                if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))) continue;

                // Skip if already has CancellationToken
                bool hasCt = method.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString() is string t &&
                    (t == "CancellationToken" || t.EndsWith(".CancellationToken")));
                if (hasCt) continue;

                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null) continue;

                var calleesAcceptingToken = new List<string>();
                var seenCallees = new HashSet<string>();

                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var si = model.GetSymbolInfo(invocation, ct);
                    var callee = si.Symbol as IMethodSymbol
                        ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (callee == null) continue;

                    bool acceptsCt = callee.Parameters.Any(p => p.Type.Name == "CancellationToken");
                    if (!acceptsCt) continue;

                    if (seenCallees.Add(callee.Name))
                        calleesAcceptingToken.Add(callee.Name);
                }

                if (calleesAcceptingToken.Count == 0) continue;

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
        string filePath,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        var findings = new List<ExceptionHandlingFinding>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) continue;

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
                    findings.Add(new ExceptionHandlingFinding(
                        "CatchAll",
                        "Catching System.Exception (or bare catch) swallows all exception types, including those that should propagate.",
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
                    var severity = statements.Count == 0 ? "High" : "Medium";
                    var desc = statements.Count == 0
                        ? "Empty catch block silently swallows the exception with no logging, rethrowing, or error return."
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
        }

        return findings;
    }

    public async Task<List<AntiPatternFinding>> FindLongParameterListAsync(
        string? filePath = null, string? projectName = null, int minParameters = 4, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var results = new List<AntiPatternFinding>();
        var diSuffixes = new[] { "Service", "Repository", "Options", "Factory" };

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var parameters = method.ParameterList.Parameters;
                if (parameters.Count < minParameters) continue;

                // For constructors: if ALL params end in DI suffixes, skip
                if (method is ConstructorDeclarationSyntax)
                {
                    bool allDi = parameters.All(p =>
                        p.Type != null && diSuffixes.Any(s => p.Type.ToString().EndsWith(s)));
                    if (allDi) continue;
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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

        var primitiveTypes = new HashSet<string> { "string", "int", "long", "Guid", "bool", "String", "Int32", "Int64", "Boolean" };
        var results = new List<AntiPatternFinding>();

        foreach (var doc in documents)
        {
            if (doc == null || doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

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
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();

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
            if (doc == null || doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (IsEventHandler(method)) continue;

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
}
