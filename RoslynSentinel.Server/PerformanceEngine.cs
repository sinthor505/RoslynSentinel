using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record PerformanceIssueReport(string FilePath, int Line, int Column, string IssueType, string Description);

public class PerformanceEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public PerformanceEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<PerformanceIssueReport>> AnalyzePerformanceAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();

        // 1. Find String Concatenations (especially in loops) — literal-based: "str" + x
        var stringConcats = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && 
                       (b.Left.IsKind(SyntaxKind.StringLiteralExpression) || b.Right.IsKind(SyntaxKind.StringLiteralExpression)));

        foreach (var concat in stringConcats)
        {
            var inLoop = concat.Ancestors().Any(a => a is ForStatementSyntax || a is ForEachStatementSyntax || a is WhileStatementSyntax || a is DoStatementSyntax);
            if (inLoop)
            {
                var loc = concat.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "StringConcatenationInLoop", "Avoid using '+' for string concatenation inside a loop. Use StringBuilder instead."));
            }
        }

        // 1b. string += in loops (compound assignment) — use semantic model for type check
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var assignConcats = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression));

        foreach (var assign in assignConcats)
        {
            var inLoop = assign.Ancestors().Any(a => a is ForStatementSyntax || a is ForEachStatementSyntax || a is WhileStatementSyntax || a is DoStatementSyntax);
            if (!inLoop) continue;

            // Use semantic model if available, otherwise fall back to literal heuristic
            bool isStringAssign = false;
            if (semanticModel != null)
            {
                var typeInfo = semanticModel.GetTypeInfo(assign.Left, cancellationToken);
                isStringAssign = typeInfo.Type?.SpecialType == SpecialType.System_String;
            }
            else
            {
                isStringAssign = assign.Right.IsKind(SyntaxKind.StringLiteralExpression)
                    || assign.Right.IsKind(SyntaxKind.InterpolatedStringExpression);
            }

            if (isStringAssign)
            {
                var loc = assign.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "StringConcatenationInLoop", "Avoid using '+=' for string concatenation inside a loop. Use StringBuilder instead."));
            }
        }

        // 1c. .ToList() / .ToArray() calls inside loops
        var toListOrArray = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                          (ma.Name.Identifier.Text == "ToList" || ma.Name.Identifier.Text == "ToArray") &&
                          inv.ArgumentList.Arguments.Count == 0);

        foreach (var inv in toListOrArray)
        {
            var inLoop = inv.Ancestors().Any(a => a is ForStatementSyntax || a is ForEachStatementSyntax || a is WhileStatementSyntax);
            if (inLoop)
            {
                var methodName = ((MemberAccessExpressionSyntax)inv.Expression).Name.Identifier.Text;
                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "AllocationInLoop", $"Avoid calling .{methodName}() inside a loop — it allocates a new collection on every iteration. Move it outside the loop."));
            }
        }

        // 2. Find Poor LINQ Usage (e.g., .Count() > 0 instead of .Any())
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Name.Identifier.Text == "Count" && inv.Parent is BinaryExpressionSyntax binExpr)
                {
                    if ((binExpr.IsKind(SyntaxKind.GreaterThanExpression) && binExpr.Right.ToString() == "0") ||
                        (binExpr.IsKind(SyntaxKind.EqualsExpression) && binExpr.Right.ToString() == "0") ||
                        (binExpr.IsKind(SyntaxKind.NotEqualsExpression) && binExpr.Right.ToString() == "0"))
                    {
                        var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                        issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "PoorLinqCountUsage", "Use .Any() instead of .Count() > 0 for better performance."));
                    }
                }

                if (memberAccess.Name.Identifier.Text == "FirstOrDefault" || memberAccess.Name.Identifier.Text == "Any")
                {
                    if (memberAccess.Expression is InvocationExpressionSyntax innerInv && innerInv.Expression is MemberAccessExpressionSyntax innerMemberAccess)
                    {
                        if (innerMemberAccess.Name.Identifier.Text == "Where")
                        {
                            var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "InefficientLinqWhere", $"Combine .Where() and .{memberAccess.Name.Identifier.Text}() into a single .{memberAccess.Name.Identifier.Text}(condition) call."));
                        }
                    }
                }
            }
        }

        // 3. Detect HttpClient instantiated directly in methods (should use IHttpClientFactory)
        // Semantic model: exact type match. Fallback: name-suffix heuristic for unresolved projects.
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var oc in objectCreations)
        {
            bool isHttpClient;
            if (semanticModel != null)
            {
                var createdType = semanticModel.GetTypeInfo(oc, cancellationToken).Type;
                isHttpClient = (createdType != null && createdType.Kind != Microsoft.CodeAnalysis.SymbolKind.ErrorType)
                    ? SemanticTypeHelper.IsHttpClient(createdType)
                    : oc.Type.ToString().EndsWith("HttpClient", StringComparison.Ordinal);
            }
            else
            {
                isHttpClient = oc.Type.ToString().EndsWith("HttpClient", StringComparison.Ordinal);
            }

            if (isHttpClient && oc.Ancestors().OfType<MethodDeclarationSyntax>().Any())
            {
                var loc = oc.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "HttpClientPerRequest",
                    "HttpClient created directly in a method — use IHttpClientFactory.CreateClient() to avoid socket exhaustion."));
            }
        }

        // 4. Detect .Result or .GetAwaiter().GetResult() — synchronous blocking on async work
        // Semantic model: exact Task/ValueTask receiver check eliminates all non-Task .Result false positives.
        // Fallback: ReceiverLooksLikeTask heuristic for unresolved projects.
        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (memberAccess.Name.Identifier.Text == "Result" &&
                !(memberAccess.Parent is AssignmentExpressionSyntax lhsAssign && lhsAssign.Left == memberAccess))
            {
                bool isTaskResult;
                if (semanticModel != null)
                {
                    var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;
                    isTaskResult = (receiverType != null && receiverType.Kind != Microsoft.CodeAnalysis.SymbolKind.ErrorType)
                        ? SemanticTypeHelper.IsTaskOrValueTask(receiverType)
                        : ReceiverLooksLikeTask(memberAccess.Expression);
                }
                else
                {
                    isTaskResult = ReceiverLooksLikeTask(memberAccess.Expression);
                }

                if (isTaskResult)
                {
                    var loc = memberAccess.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "BlockingAsyncCall",
                        ".Result accessed on a Task — synchronous blocking risks deadlocks and starves the thread pool. Use 'await' instead."));
                }
            }

            if (memberAccess.Name.Identifier.Text == "GetResult" &&
                memberAccess.Expression is InvocationExpressionSyntax getAwaiterCall &&
                getAwaiterCall.Expression is MemberAccessExpressionSyntax getAwaiterMa &&
                getAwaiterMa.Name.Identifier.Text == "GetAwaiter")
            {
                var loc = memberAccess.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "BlockingAsyncCall",
                    ".GetAwaiter().GetResult() found — synchronous blocking risks deadlocks. Use 'await' instead."));
            }
        }

        // 5. Detect .Wait() on Task — synchronous blocking
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax waitMa &&
                waitMa.Name.Identifier.Text == "Wait" &&
                inv.ArgumentList.Arguments.Count == 0)
            {
                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "BlockingAsyncCall",
                    ".Wait() on a Task — synchronous blocking risks deadlocks. Use 'await' instead."));
            }
        }

        // 6. Detect .ToList().Count or .ToArray().Length — materializes just to get count
        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var propName = memberAccess.Name.Identifier.Text;
            if ((propName == "Count" || propName == "Length") &&
                memberAccess.Expression is InvocationExpressionSyntax innerInv2 &&
                innerInv2.Expression is MemberAccessExpressionSyntax innerMa2 &&
                (innerMa2.Name.Identifier.Text == "ToList" || innerMa2.Name.Identifier.Text == "ToArray"))
            {
                var loc = memberAccess.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "UnnecessaryMaterialization",
                    $".{innerMa2.Name.Identifier.Text}().{propName} materializes the collection just to count it. Use .Count() directly."));
            }
        }

        // 7. New collection allocation inside a loop — repeated heap allocation on every iteration
        var newCollections = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(oc =>
            {
                var typeName = oc.Type.ToString();
                return typeName.StartsWith("List<", StringComparison.Ordinal) ||
                       typeName.StartsWith("Dictionary<", StringComparison.Ordinal) ||
                       typeName.StartsWith("HashSet<", StringComparison.Ordinal) ||
                       typeName.StartsWith("Queue<", StringComparison.Ordinal) ||
                       typeName.StartsWith("Stack<", StringComparison.Ordinal) ||
                       typeName.EndsWith("List", StringComparison.Ordinal) ||
                       typeName.EndsWith("Dictionary", StringComparison.Ordinal) ||
                       typeName.EndsWith("HashSet", StringComparison.Ordinal);
            });
        foreach (var oc in newCollections)
        {
            var inLoop = oc.Ancestors().Any(a =>
                a is ForStatementSyntax || a is ForEachStatementSyntax ||
                a is WhileStatementSyntax || a is DoStatementSyntax);
            if (inLoop)
            {
                var shortType = (oc.Type as GenericNameSyntax)?.Identifier.Text ?? oc.Type.ToString();
                var loc = oc.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "CollectionAllocationInLoop",
                    $"new {shortType}() inside a loop allocates on every iteration. Move the allocation outside the loop and Clear() it per iteration if needed."));
            }
        }

        // 8. Select().Select() — two projections can be merged into one
        foreach (var outerSelect in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (outerSelect.Expression is not MemberAccessExpressionSyntax outerMa) continue;
            if (outerMa.Name.Identifier.Text != "Select") continue;

            if (outerMa.Expression is InvocationExpressionSyntax innerSelect &&
                innerSelect.Expression is MemberAccessExpressionSyntax innerMa &&
                innerMa.Name.Identifier.Text == "Select")
            {
                var loc = outerSelect.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "ChainedSelectProjection",
                    ".Select().Select() applies two projections separately — merge into a single .Select() to iterate the source only once."));
            }
        }

        // 10. Thread.Sleep in async methods — blocks the thread pool; use Task.Delay instead
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax sleepMa &&
                sleepMa.Expression.ToString() is "Thread" or "System.Threading.Thread" &&
                sleepMa.Name.Identifier.Text == "Sleep")
            {
                var containingMethod = inv.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                bool isAsync = containingMethod?.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) == true;
                if (isAsync)
                {
                    var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "ThreadSleepInAsync",
                        "Thread.Sleep() in an async method blocks the thread pool. Use 'await Task.Delay()' instead."));
                }
            }
        }

        // 11. lock with async calls inside an async method — cannot await inside lock;
        // if the locked region calls async methods, consider SemaphoreSlim.WaitAsync() instead.
        foreach (var lockStmt in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            var containingMethod = lockStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            bool isAsync = containingMethod?.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) == true;
            if (!isAsync) continue;

            bool hasAsyncCalls = lockStmt.Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text.EndsWith("Async", StringComparison.OrdinalIgnoreCase),
                    IdentifierNameSyntax id => id.Identifier.Text.EndsWith("Async", StringComparison.OrdinalIgnoreCase),
                    _ => false
                });
            if (hasAsyncCalls)
            {
                var loc = lockStmt.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "LockWithAsyncInAsyncMethod",
                    "lock statement in async method contains async calls — use SemaphoreSlim.WaitAsync() for async-compatible mutual exclusion."));
            }
        }

        // 12. .OrderBy(...).First() or .OrderByDescending(...).First() — use MinBy/MaxBy
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax outerMa) continue;
            var outerName = outerMa.Name.Identifier.Text;
            if (outerName is not ("First" or "FirstOrDefault")) continue;
            if (inv.ArgumentList.Arguments.Count != 0) continue;

            if (outerMa.Expression is InvocationExpressionSyntax innerInvSort &&
                innerInvSort.Expression is MemberAccessExpressionSyntax innerMaSort)
            {
                if (innerMaSort.Name.Identifier.Text == "OrderBy")
                {
                    var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "OrderByThenFirst",
                        $".OrderBy(...).{outerName}() sorts the entire sequence to get the minimum element. Use .MinBy(...) instead."));
                }
                else if (innerMaSort.Name.Identifier.Text == "OrderByDescending")
                {
                    var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "OrderByThenFirst",
                        $".OrderByDescending(...).{outerName}() sorts the entire sequence to get the maximum element. Use .MaxBy(...) instead."));
                }
            }
        }

        // 13. Double enumeration — IEnumerable<T> variable consumed more than once
        // Each enumeration re-executes the entire LINQ chain; use .ToList()/.ToArray() to materialize once.
        if (semanticModel != null)
        {
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var declarator in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    var localSymbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
                    if (localSymbol == null) continue;
                    if (!SemanticTypeHelper.IsNonMaterializedSequence(localSymbol.Type)) continue;

                    // Count references to this symbol inside the method (excluding the declaration itself)
                    var uses = method.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Where(id =>
                            id.Identifier.Text == localSymbol.Name &&
                            id.Parent is not VariableDeclaratorSyntax &&
                            SymbolEqualityComparer.Default.Equals(
                                semanticModel.GetSymbolInfo(id, cancellationToken).Symbol, localSymbol))
                        .Count();

                    if (uses >= 2)
                    {
                        var loc = declarator.GetLocation().GetLineSpan().StartLinePosition;
                        issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                            "PotentialDoubleEnumeration",
                            $"'{localSymbol.Name}' ({localSymbol.Type.Name}) is a lazy sequence used {uses} times — each use re-executes the full query. Call .ToList() or .ToArray() once and reuse."));
                    }
                }
            }
        }

        // 14. Inline Regex instantiation — re-compiles the pattern on every call
        // Correct pattern: private static readonly Regex _re = new(pattern);
        foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            bool isRegex;
            if (semanticModel != null)
            {
                var created = semanticModel.GetTypeInfo(oc, cancellationToken).Type;
                // Use semantic type when fully resolved; fall back to name check for error/unresolvable types
                isRegex = (created != null && created.Kind != Microsoft.CodeAnalysis.SymbolKind.ErrorType)
                    ? SemanticTypeHelper.IsRegex(created)
                    : oc.Type.ToString() is "Regex" or "System.Text.RegularExpressions.Regex";
            }
            else
            {
                isRegex = oc.Type.ToString() is "Regex" or "System.Text.RegularExpressions.Regex";
            }
            if (!isRegex) continue;

            // OK if it's inside a static field initializer (that IS the correct pattern)
            var containingField = oc.Ancestors().OfType<FieldDeclarationSyntax>().FirstOrDefault();
            if (containingField != null &&
                containingField.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                continue;

            // Flag if created inside a method body
            if (oc.Ancestors().OfType<MethodDeclarationSyntax>().Any())
            {
                var loc = oc.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "InlineRegexInstantiation",
                    "new Regex(...) inside a method re-compiles the pattern on every call. Use a 'private static readonly Regex' field or a Regex source-generator ([GeneratedRegex])."));
            }
        }

        // 15. String method called with single-character string literal — use char overload
        // e.g. s.Contains("x") → s.Contains('x') — avoids string allocation, uses faster comparison
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax sma) continue;
            var mName = sma.Name.Identifier.Text;
            if (mName is not ("Contains" or "IndexOf" or "StartsWith" or "EndsWith" or "LastIndexOf")) continue;
            if (inv.ArgumentList.Arguments.Count < 1) continue;

            var firstArg = inv.ArgumentList.Arguments[0].Expression;
            if (firstArg is not LiteralExpressionSyntax lit ||
                !lit.IsKind(SyntaxKind.StringLiteralExpression) ||
                lit.Token.ValueText.Length != 1) continue;

            bool receiverIsString = true;
            if (semanticModel != null)
            {
                var rt = semanticModel.GetTypeInfo(sma.Expression, cancellationToken).Type;
                if (rt != null) receiverIsString = rt.SpecialType == SpecialType.System_String;
            }

            if (receiverIsString)
            {
                var ch = lit.Token.ValueText[0];
                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "StringMethodWithSingleCharArg",
                    $".{mName}(\"{ch}\") — use the char overload .{mName}('{ch}') to avoid string allocation and enable faster comparison."));
            }
        }

        // 16. Where().Where() — two filter passes when one would do
        foreach (var outerWhere in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (outerWhere.Expression is not MemberAccessExpressionSyntax owMa) continue;
            if (owMa.Name.Identifier.Text != "Where") continue;
            if (owMa.Expression is InvocationExpressionSyntax inner &&
                inner.Expression is MemberAccessExpressionSyntax iwMa &&
                iwMa.Name.Identifier.Text == "Where")
            {
                var loc = outerWhere.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "ChainedWhereFilters",
                    ".Where(...).Where(...) iterates the source twice. Merge into a single .Where(x => condition1 && condition2)."));
            }
        }

        // 17. Loop invariant condition — .Count() or .Count/.Length evaluated on every for-loop iteration
        foreach (var forLoop in root.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (forLoop.Condition == null) continue;

            // .Count() LINQ extension — O(n) per-iteration check
            foreach (var call in forLoop.Condition.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax cma &&
                              cma.Name.Identifier.Text == "Count" &&
                              inv.ArgumentList.Arguments.Count == 0))
            {
                var loc = call.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "LoopInvariantCondition",
                    "for loop condition calls .Count() on every iteration — O(n) per check. Cache before the loop: 'var count = source.Count();'"));
            }

            // .Count or .Length property — skip arrays (JIT hoists array.Length automatically)
            foreach (var ma in forLoop.Condition.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(ma => ma.Name.Identifier.Text is "Count" or "Length" &&
                             ma.Parent is not InvocationExpressionSyntax))
            {
                if (semanticModel != null)
                {
                    var receiverType = semanticModel.GetTypeInfo(ma.Expression, cancellationToken).Type;
                    if (receiverType?.TypeKind == TypeKind.Array) continue;
                }
                var loc = ma.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "LoopInvariantCondition",
                    $"for loop condition reads .{ma.Name.Identifier.Text} on every iteration. Cache before the loop: 'var count = collection.{ma.Name.Identifier.Text};'"));
            }
        }

        // 18. Use of 'dynamic' — disables compile-time type checking, forces DLR dispatch on every member access
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == "dynamic" &&
                         id.Parent is VariableDeclarationSyntax or ParameterSyntax or
                                      MethodDeclarationSyntax or PropertyDeclarationSyntax))
        {
            var loc = id.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                "DynamicTypeUsage",
                "'dynamic' disables compile-time type checking and forces DLR dispatch on every call. Prefer a specific type, generics, or an interface."));
        }

        // 19. Local variable typed as 'object' — prefer specific type or generics to avoid boxing
        foreach (var pts in root.DescendantNodes().OfType<PredefinedTypeSyntax>())
        {
            if (!pts.Keyword.IsKind(SyntaxKind.ObjectKeyword)) continue;
            if (pts.Parent is not VariableDeclarationSyntax objVd) continue;
            if (objVd.Parent is not LocalDeclarationStatementSyntax) continue;
            var varName = objVd.Variables.FirstOrDefault()?.Identifier.Text ?? "variable";
            var loc = pts.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                "ObjectTypeUsage",
                $"Local variable '{varName}' is typed as 'object' — prefer a specific type or generic to avoid boxing and improve type safety."));
        }

        // 20. Enum.Parse / Enum.TryParse inside a loop — re-parses string on every iteration
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax enumMa) continue;
            if (enumMa.Expression.ToString() is not ("Enum" or "System.Enum")) continue;
            if (enumMa.Name.Identifier.Text is not ("Parse" or "TryParse")) continue;
            if (!inv.Ancestors().Any(a => a is ForStatementSyntax || a is ForEachStatementSyntax ||
                                         a is WhileStatementSyntax || a is DoStatementSyntax)) continue;
            var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                "EnumParseInLoop",
                $"Enum.{enumMa.Name.Identifier.Text}() inside a loop re-parses the string on every iteration. Cache results in a Dictionary<string, TEnum> before the loop."));
        }

        // 21. .Aggregate() with string concatenation in lambda — string.Join() avoids N-1 intermediate allocations
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax aggMa) continue;
            if (aggMa.Name.Identifier.Text != "Aggregate") continue;
            if (inv.ArgumentList.Arguments.Count < 1) continue;

            var firstArg = inv.ArgumentList.Arguments[0].Expression;
            var lambdaBody = (firstArg as SimpleLambdaExpressionSyntax)?.Body
                          ?? (firstArg as ParenthesizedLambdaExpressionSyntax)?.Body;
            if (lambdaBody == null) continue;

            var hasStringLiteralConcat = lambdaBody.DescendantNodesAndSelf()
                .OfType<BinaryExpressionSyntax>()
                .Any(b => b.IsKind(SyntaxKind.AddExpression) &&
                          (b.Left is LiteralExpressionSyntax l1 && l1.IsKind(SyntaxKind.StringLiteralExpression) ||
                           b.Right is LiteralExpressionSyntax l2 && l2.IsKind(SyntaxKind.StringLiteralExpression)));

            if (hasStringLiteralConcat)
            {
                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "StringJoinOpportunity",
                    ".Aggregate() with string concatenation creates N-1 intermediate strings. Use string.Join(separator, source) instead."));
            }
        }

        // 22. Same method call repeated 3+ times without caching — each call re-executes the work
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var groups = method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => !IsNoisyRepeatedCall(inv))
                .GroupBy(inv => inv.ToString())
                .Where(g => g.Count() >= 3);

            foreach (var group in groups)
            {
                var first = group.First();
                var callText = group.Key.Length > 60 ? group.Key[..57] + "..." : group.Key;
                var loc = first.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "RepeatedMethodCallNotCached",
                    $"'{callText}' called {group.Count()} times in this method. If the result is stable, store it in a local variable."));
            }
        }

        // Check 23: Reflection calls inside loops — GetMethod/GetProperty/GetField/GetType are expensive
        foreach (var loopNode in root.DescendantNodes().Where(n =>
            n is ForStatementSyntax or ForEachStatementSyntax or
            WhileStatementSyntax or DoStatementSyntax))
        {
            foreach (var inv in loopNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax refMa) continue;
                var name = refMa.Name.Identifier.Text;
                if (name is not ("GetMethod" or "GetProperty" or "GetField" or "GetEvent" or
                                  "GetConstructor" or "GetMembers" or "GetMethods" or "GetProperties" or
                                  "GetFields" or "GetInterface" or "GetInterfaces" or "GetType")) continue;

                // Skip GetType() called on instances (not typeof), which is commonly needed
                if (name == "GetType" && inv.ArgumentList.Arguments.Count == 0 &&
                    refMa.Expression is not IdentifierNameSyntax { Identifier.Text: "Type" } and
                    not MemberAccessExpressionSyntax { Name.Identifier.Text: "Assembly" }) continue;

                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "ReflectionInLoop",
                    $"Reflection call '{name}()' inside a loop is expensive. Cache the result in a local variable outside the loop."));
            }
        }

        // Check 24: Collection allocated without capacity when source size is known
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            foreach (var localDecl in method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in localDecl.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is not ObjectCreationExpressionSyntax oc) continue;

                    var typeName = oc.Type.ToString();
                    bool isGrowableCollection =
                        typeName.StartsWith("List<") || typeName.StartsWith("System.Collections.Generic.List<") ||
                        typeName.StartsWith("HashSet<") || typeName.StartsWith("Dictionary<");
                    if (!isGrowableCollection) continue;

                    // Already has a capacity argument — fine
                    if (oc.ArgumentList != null && oc.ArgumentList.Arguments.Count > 0) continue;

                    var varName = variable.Identifier.Text;

                    // Check: is there a foreach/for loop in the same method body that calls .Add()?
                    bool hasLoopAdd = method.DescendantNodes().Where(n =>
                            n is ForStatementSyntax or ForEachStatementSyntax)
                        .Any(loop => loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Any(inv => inv.Expression is MemberAccessExpressionSyntax lma &&
                                        lma.Name.Identifier.Text == "Add" &&
                                        lma.Expression.ToString() == varName));

                    if (!hasLoopAdd) continue;

                    var loc = localDecl.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                        "CollectionWithoutCapacity",
                        $"'{varName}' is a {typeName.Split('<')[0]} with no initial capacity but items are added in a loop. " +
                        "If the count is known, pass it to the constructor to avoid resizing allocations."));
                }
            }
        }

        // Deduplicate by (filePath, line, issueType) — chained string concat expressions
        // produce one node per '+' operator, all pointing to adjacent lines on the same expression.
        var seen = new HashSet<(string, int, string)>();
        var deduped = new List<PerformanceIssueReport>();
        foreach (var issue in issues)
        {
            var key = (issue.FilePath, issue.Line, issue.IssueType);
            if (seen.Add(key))
                deduped.Add(issue);
        }
        return deduped;
    }

    public async Task<List<PerformanceIssueReport>> OptimizeResourceDisposalAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();

        foreach (var creation in objectCreations)
        {
            var typeInfo = semanticModel.GetTypeInfo(creation, cancellationToken).Type;
            // Exclude types whose correct lifecycle is NOT per-use disposal:
            // HttpClient must be long-lived (IHttpClientFactory manages lifetime);
            // Socket is pooled by the OS; disposing per-use is harmful.
            if (typeInfo != null && (SemanticTypeHelper.IsHttpClient(typeInfo) ||
                typeInfo.Name is "Socket" or "TcpClient" or "UdpClient")) continue;
            if (typeInfo != null && typeInfo.AllInterfaces.Any(i => i.Name == "IDisposable"))
            {
                var isDisposed = creation.Ancestors().Any(a => a is UsingStatementSyntax || a is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
                if (!isDisposed)
                {
                    var loc = creation.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "MissingDisposal", $"Type '{typeInfo.Name}' implements IDisposable but is not wrapped in a 'using' statement."));
                }
            }
        }

        return issues;
    }

    public async Task<List<PerformanceIssueReport>> DetectInefficientStringComparisonsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        var binaryExprs = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression));

        foreach (var bin in binaryExprs)
        {
            var left = bin.Left.ToString();
            var right = bin.Right.ToString();

            if (left.Contains(".ToLower()") || left.Contains(".ToUpper()") || 
                right.Contains(".ToLower()") || right.Contains(".ToUpper()"))
            {
                var loc = bin.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "InefficientStringComparison", "Avoid using .ToLower() or .ToUpper() for comparison. Use string.Equals with StringComparison.OrdinalIgnoreCase instead."));
            }
        }

        return issues;
    }

    public async Task<List<PerformanceIssueReport>> FindBoxingAllocationsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<PerformanceIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<PerformanceIssueReport>();

        var issues = new List<PerformanceIssueReport>();
        // Simple heuristic: passing value type to method expecting object or dynamic
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(inv, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                for (int i = 0; i < Math.Min(inv.ArgumentList.Arguments.Count, methodSymbol.Parameters.Length); i++)
                {
                    var arg = inv.ArgumentList.Arguments[i];
                    var param = methodSymbol.Parameters[i];
                    
                    if (param.Type.SpecialType == SpecialType.System_Object)
                    {
                        var argType = semanticModel.GetTypeInfo(arg.Expression, cancellationToken).Type;
                        if (argType != null && argType.IsValueType)
                        {
                            var loc = arg.GetLocation().GetLineSpan().StartLinePosition;
                            issues.Add(new PerformanceIssueReport(filePath, loc.Line + 1, loc.Character + 1, "BoxingAllocation", $"Boxing detected: converting value type '{argType.Name}' to 'object' in parameter '{param.Name}'."));
                        }
                    }
                }
            }
        }

        return issues;
    }

    // Excludes noisy invocations from the repeated-call check: logging, collection mutation,
    // and trivially short calls that aren't worth flagging.
    private static bool IsNoisyRepeatedCall(InvocationExpressionSyntax inv)
    {
        var expr = inv.Expression.ToString();
        return expr.Contains("Log") || expr.Contains("Write") || expr.Contains("Append") ||
               expr.StartsWith("Console.") || expr.StartsWith("Debug.") ||
               expr.EndsWith(".Add") || expr.EndsWith(".TryAdd") ||
               expr.EndsWith(".Push") || expr.EndsWith(".Enqueue") ||
               expr.EndsWith(".Remove") || expr.EndsWith(".Clear") ||
               inv.ToString().Length < 8;
    }

    // ── LinqN1Pattern ─────────────────────────────────────────────────────────

    private static readonly HashSet<string> LinqTerminals = new(StringComparer.Ordinal)
    {
        "ToList", "ToArray", "ToDictionary", "ToHashSet",
        "FirstOrDefault", "First", "SingleOrDefault", "Single",
        "Any", "All", "Count", "Sum", "Min", "Max", "Average"
    };

    /// <summary>
    /// Detects LINQ terminal operations (FirstOrDefault, ToList, Any, etc.) called inside
    /// a loop body where the LINQ chain filters on the loop variable — the classic N+1 pattern
    /// that fires one query per iteration instead of one batch query.
    /// </summary>
    public async Task<List<PerformanceIssueReport>> FindLinqN1PatternsAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<PerformanceIssueReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? (string.IsNullOrEmpty(projectName)
                ? solution.Projects.SelectMany(p => p.Documents)
                : solution.Projects.Where(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Documents))
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            // Find all for/foreach/while loop bodies
            var loops = root.DescendantNodes()
                .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

            foreach (var loop in loops)
            {
                // Get the loop variable name (foreach only — for/while iteration variable detection is harder)
                string? loopVar = loop is ForEachStatementSyntax fe ? fe.Identifier.Text : null;

                // Find all LINQ terminal invocations inside this loop
                foreach (var inv in loop.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                    var terminalName = ma.Name.Identifier.Text;
                    if (!LinqTerminals.Contains(terminalName)) continue;

                    // The receiver must look like a LINQ chain (contains .Where / .Select / etc.)
                    var chain = ma.Expression.ToString();
                    bool isLinqChain = chain.Contains(".Where") || chain.Contains(".Select") ||
                                       chain.Contains(".OrderBy") || chain.Contains(".GroupBy") ||
                                       chain.Contains(".Join") || chain.Contains(".Include");

                    // Also flag direct terminal on a collection/dbSet that receives the loop var as argument
                    bool terminalTakesLoopVar = loopVar != null &&
                        inv.ArgumentList.Arguments.Any(a => a.ToString().Contains(loopVar));

                    if (!isLinqChain && !terminalTakesLoopVar) continue;

                    // Confirm the loop variable appears inside the LINQ chain (the filter uses the per-item value)
                    bool usesLoopVar = loopVar == null ||
                        ma.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                            .Any(id => id.Identifier.Text == loopVar) ||
                        inv.ArgumentList.Arguments.Any(a => a.ToString().Contains(loopVar));

                    if (!usesLoopVar && loopVar != null) continue;

                    var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new PerformanceIssueReport(fp, loc.Line + 1, loc.Character + 1,
                        "LinqN1Pattern",
                        $"LINQ terminal '{terminalName}' called inside a loop — potential N+1: " +
                        $"one query fires per iteration. Batch with a single query outside the loop."));
                }
            }
        }
        return issues;
    }

    // ── InterpolatedStringInLoop ──────────────────────────────────────────────

    /// <summary>
    /// Detects string.Format() and $"..." interpolated string expressions inside loops.
    /// These allocate a new string object every iteration. The existing StringConcatenationInLoop
    /// check covers += and literal +; this covers Format() and interpolation.
    /// </summary>
    public async Task<List<PerformanceIssueReport>> FindStringFormatInLoopsAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<PerformanceIssueReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            static bool IsInsideLoop(SyntaxNode n) =>
                n.Ancestors().Any(a => a is ForStatementSyntax or ForEachStatementSyntax
                                    or WhileStatementSyntax or DoStatementSyntax);

            // $"..." interpolated strings in loops that contain non-literal expressions
            foreach (var interp in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
            {
                if (!IsInsideLoop(interp)) continue;
                // Only flag when the interpolation has dynamic content (not a pure constant)
                if (!interp.Contents.OfType<InterpolationSyntax>().Any()) continue;
                var loc = interp.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "InterpolatedStringInLoop",
                    "Interpolated string '$\"...\"' inside a loop allocates a new string every iteration. " +
                    "Consider StringBuilder.AppendFormat or composing outside the loop."));
            }

            // string.Format(...) in loops
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsInsideLoop(inv)) continue;
                if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                if (ma.Expression.ToString() != "string" && ma.Expression.ToString() != "String") continue;
                if (ma.Name.Identifier.Text != "Format") continue;
                var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "StringFormatInLoop",
                    "string.Format() inside a loop allocates per iteration. " +
                    "Use StringBuilder.AppendFormat or compose the string before the loop."));
            }
        }
        return issues;
    }

    // ── MultipleEnumeration ───────────────────────────────────────────────────

    private static readonly HashSet<string> MaterializingMethods = new(StringComparer.Ordinal)
    {
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "ToImmutableList",
        "ToImmutableArray", "ToImmutableDictionary", "ToImmutableHashSet"
    };

    /// <summary>
    /// Detects local variables whose declared type is IEnumerable/IQueryable and that are
    /// iterated or queried more than once without a materializing call (.ToList() etc.)
    /// in between — which can execute a DB query or expensive generator twice.
    /// </summary>
    public async Task<List<PerformanceIssueReport>> FindMultipleEnumerationAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<PerformanceIssueReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body == null) continue;

                // Collect IEnumerable/IQueryable locals and parameters (not already materialized)
                var enumerableLocals = new HashSet<string>(StringComparer.Ordinal);

                // Parameters typed as IEnumerable<T> or IQueryable<T> are never materialized here
                foreach (var param in method.ParameterList.Parameters)
                {
                    var typeName = param.Type?.ToString() ?? "";
                    if (typeName.StartsWith("IEnumerable") || typeName.StartsWith("IQueryable") ||
                        typeName.StartsWith("IOrderedEnumerable"))
                        enumerableLocals.Add(param.Identifier.Text);
                }

                foreach (var decl in method.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    var typeName = decl.Declaration.Type.ToString();
                    bool isEnumerable = typeName.StartsWith("IEnumerable") ||
                                        typeName.StartsWith("IQueryable") ||
                                        typeName.StartsWith("IOrderedEnumerable") ||
                                        typeName == "var"; // conservative: include var-typed, filter by usage

                    if (!isEnumerable) continue;

                    // Skip if the initializer itself materializes (= someList.Where(...).ToList())
                    foreach (var v in decl.Declaration.Variables)
                    {
                        var init = v.Initializer?.Value?.ToString() ?? "";
                        bool alreadyMaterialized = MaterializingMethods.Any(m => init.Contains("." + m + "("));
                        if (!alreadyMaterialized)
                            enumerableLocals.Add(v.Identifier.Text);
                    }
                }

                if (enumerableLocals.Count == 0) continue;

                // For each candidate, count how many times it's enumerated (foreach + LINQ terminal calls)
                foreach (var varName in enumerableLocals)
                {
                    // Collect all "use sites" in order (foreach loops and LINQ terminals on the variable)
                    var useSites = new List<int>(); // line numbers

                    foreach (var fe in method.Body.DescendantNodes().OfType<ForEachStatementSyntax>())
                    {
                        if (fe.Expression is IdentifierNameSyntax id && id.Identifier.Text == varName)
                            useSites.Add(fe.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    }

                    foreach (var inv in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
                        if (!LinqTerminals.Contains(ma.Name.Identifier.Text)) continue;
                        // The receiver (before the terminal) must reference our variable
                        if (!ma.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                                .Any(i => i.Identifier.Text == varName)) continue;
                        useSites.Add(inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    }

                    if (useSites.Count < 2) continue;

                    issues.Add(new PerformanceIssueReport(fp, useSites[0], 1,
                        "MultipleEnumeration",
                        $"'{varName}' (IEnumerable) is enumerated {useSites.Count} times " +
                        $"(lines {string.Join(", ", useSites)}) without materializing. " +
                        "Call .ToList() once and reuse the list."));
                }
            }
        }
        return issues;
    }

    // ── LinqRedundantWhere ────────────────────────────────────────────────────
    // .Where(pred).First() allocates a filtered enumerable, then finds the first element —
    // when .First(pred) does the same thing in a single pass with no intermediate allocation.

    private static readonly HashSet<string> RedundantAfterWhere = new(StringComparer.Ordinal)
    {
        "First", "FirstOrDefault", "Last", "LastOrDefault",
        "Single", "SingleOrDefault", "Any", "Count"
    };

    /// <summary>
    /// Detects LINQ chains of the form .Where(pred).Terminal() where Terminal accepts a
    /// predicate directly (e.g. .First(pred), .Any(pred)). The Where creates an intermediate
    /// IEnumerable allocation that can be eliminated.
    /// </summary>
    public async Task<List<PerformanceIssueReport>> FindLinqRedundantWhereAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<PerformanceIssueReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            foreach (var outer in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // Pattern: outer = something.Where(pred).Terminal()
                if (outer.Expression is not MemberAccessExpressionSyntax outerMa) continue;
                var terminalName = outerMa.Name.Identifier.Text;
                if (!RedundantAfterWhere.Contains(terminalName)) continue;

                // The receiver of Terminal() must itself be a .Where(...) call
                if (outerMa.Expression is not InvocationExpressionSyntax whereInv) continue;
                if (whereInv.Expression is not MemberAccessExpressionSyntax whereMa) continue;
                if (whereMa.Name.Identifier.Text != "Where") continue;

                // Where must have exactly one argument (the predicate)
                if (whereInv.ArgumentList.Arguments.Count != 1) continue;

                // Terminal must be called with NO arguments (Where already provides the predicate)
                if (outer.ArgumentList.Arguments.Count != 0) continue;

                var loc = outer.GetLocation().GetLineSpan().StartLinePosition;
                var pred = whereInv.ArgumentList.Arguments[0].ToString();
                issues.Add(new PerformanceIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "LinqRedundantWhere",
                    $".Where({pred}).{terminalName}() creates an intermediate filtered enumerable. " +
                    $"Use .{terminalName}({pred}) directly to avoid the allocation."));
            }
        }
        return issues;
    }

    // ── ImplicitNullableBoxing ────────────────────────────────────────────────
    // Casting a Nullable<T> to object boxes the value — if it is null the box contains
    // a null reference, which can cause surprising equality results and GC pressure.

    /// <summary>
    /// Detects explicit casts of Nullable&lt;T&gt; values to object or dynamic, which cause boxing
    /// and can produce surprising null-equality behavior. Uses the semantic model for accuracy.
    /// </summary>
    public async Task<List<PerformanceIssueReport>> FindImplicitNullableBoxingAsync(
        string? filePath = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<PerformanceIssueReport>();

        IEnumerable<Document> docs = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath!).Select(id => solution.GetDocument(id)!).Where(d => d != null);

        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var model = await doc.GetSemanticModelAsync(ct);
            if (model == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            foreach (var cast in root.DescendantNodes().OfType<CastExpressionSyntax>())
            {
                var targetType = cast.Type.ToString();
                if (targetType is not ("object" or "Object" or "dynamic")) continue;

                var exprTypeInfo = model.GetTypeInfo(cast.Expression, ct);
                var exprType = exprTypeInfo.Type as INamedTypeSymbol;
                if (exprType == null) continue;

                // Nullable<T> is a generic struct whose original definition is Nullable<T>
                if (!exprType.IsValueType) continue;
                if (exprType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) continue;

                var loc = cast.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new PerformanceIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "ImplicitNullableBoxing",
                    $"Casting '{cast.Expression}' (Nullable<T>) to {targetType} boxes the value. " +
                    "If the nullable is null the resulting box is null — not a null reference to a T. " +
                    "Use .HasValue/.Value or null-coalescing to avoid boxing."));
            }
        }
        return issues;
    }

    // Heuristic: the expression before .Result looks like it holds a Task.
    // Checks if it's an async invocation (method name ends in Async) or a variable/field
    // whose name suggests it stores a Task, to avoid flagging OperationResult.Result etc.
    private static bool ReceiverLooksLikeTask(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case InvocationExpressionSyntax inv:
                string? calledName = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                return calledName?.EndsWith("Async", StringComparison.OrdinalIgnoreCase) == true;

            case IdentifierNameSyntax varId:
                var vn = varId.Identifier.Text;
                return vn.EndsWith("Task", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(vn, "task", StringComparison.OrdinalIgnoreCase);

            case MemberAccessExpressionSyntax fieldMa:
                var fn = fieldMa.Name.Identifier.Text;
                return fn.EndsWith("Task", StringComparison.OrdinalIgnoreCase) ||
                       fn.EndsWith("Async", StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }
}
