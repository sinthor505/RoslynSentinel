using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace RoslynSentinel.Server;

public record PathInputConstraint(
    string ParameterName,
    string ConstraintDescription,
    string? SuggestedValue);

public record PathDrivenTestCase(
    string TestMethodName,
    string ScenarioDescription,
    List<PathInputConstraint> InputConstraints,
    string ExpectedOutcome,
    string ArrangeCode,
    string ActCode,
    string AssertCode,
    string? Notes = null);

public record PathDrivenTestReport(
    string MethodName,
    string FilePath,
    string ClassName,
    int PathCount,
    List<PathDrivenTestCase> TestCases,
    string GeneratedTestCode,
    string Note = "Generated stubs are starting points — input constraints are inferred heuristically and may not precisely trigger each intended path.");

public class PathDrivenTestEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public PathDrivenTestEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Generates path-driven test stubs for a method by walking its AST for decision points
    /// (if/else, switch, foreach, for, while, do-while) and emitting one test case per distinct
    /// execution path. Always includes a happy-path test first. Loop decision points are only
    /// included when the loop condition references a method parameter (incoming path) or the
    /// loop body writes to a variable that appears in a return statement (outgoing path).
    /// </summary>
    public async Task<PathDrivenTestReport> GeneratePathDrivenTestsAsync(
        string filePath,
        string methodName,
        string framework = "NUnit",
        int? disambiguateLine = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath))
            .Select(solution.GetDocument)
            .FirstOrDefault();

        if (document == null)
            return new PathDrivenTestReport(methodName, filePath, "?", 0, [],
                $"// Error: File not found: {filePath}");

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
            return new PathDrivenTestReport(methodName, filePath, "?", 0, [],
                "// Error: Could not parse syntax root.");

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (methods.Count == 0)
            return new PathDrivenTestReport(methodName, filePath, "?", 0, [],
                $"// Error: Method '{methodName}' not found in {filePath}.");

        var method = methods.Count == 1
            ? methods[0]
            : disambiguateLine.HasValue
                ? methods.FirstOrDefault(m =>
                {
                    var span = m.GetLocation().GetLineSpan();
                    return disambiguateLine.Value >= span.StartLinePosition.Line + 1
                        && disambiguateLine.Value <= span.EndLinePosition.Line + 1;
                }) ?? methods[0]
                : methods[0];

        var classNode = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var className = classNode?.Identifier.Text ?? "UnknownClass";
        var ns = classNode?.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString() ?? "Global";

        var ctorParams = classNode?.Members
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault()?.ParameterList.Parameters.ToList() ?? [];

        var interfaceParams = ctorParams
            .Where(p =>
            {
                var typeName = p.Type?.ToString() ?? "";
                var baseName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
                return baseName.Length > 1 && baseName[0] == 'I' && char.IsUpper(baseName[1]);
            })
            .ToList();

        var methodParams = method.ParameterList.Parameters.ToList();
        var isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
        var returnsTask = method.ReturnType.ToString().StartsWith("Task");
        var needsAsync = isAsync || returnsTask;

        var testCases = new List<PathDrivenTestCase>();
        testCases.Add(GenerateHappyPath(methodName, methodParams, interfaceParams, method, needsAsync));

        if (method.Body != null)
        {
            var paramNames = methodParams.Select(p => p.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);
            var returnIdentifiers = method.Body.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .SelectMany(r => r.DescendantNodes().OfType<IdentifierNameSyntax>())
                .Select(id => id.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);

            var decisionPoints = CollectDecisionPoints(method.Body, paramNames, returnIdentifiers);
            foreach (var dp in decisionPoints)
            {
                var cases = GenerateTestCasesForDecisionPoint(
                    dp, methodName, methodParams, needsAsync, testCases.Count);
                testCases.AddRange(cases);
            }
        }

        var code = RenderTestClass(className, methodName, ns, framework, interfaceParams, testCases, needsAsync);

        return new PathDrivenTestReport(
            methodName, filePath, className,
            testCases.Count, testCases, code);
    }

    // ── Decision point model ─────────────────────────────────────────────────────

    private record DecisionPoint(string Kind, string Condition, bool HasElse, List<string> Cases);

    private static List<DecisionPoint> CollectDecisionPoints(
        BlockSyntax body,
        HashSet<string> paramNames,
        HashSet<string> returnIdentifiers)
    {
        var points = new List<DecisionPoint>();
        var seenConditions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax ifStmt:
                {
                    var cond = ifStmt.Condition.ToString();
                    if (seenConditions.Add(cond))
                        points.Add(new DecisionPoint("If", cond, ifStmt.Else != null, []));
                    break;
                }
                case SwitchStatementSyntax sw:
                {
                    var cond = sw.Expression.ToString();
                    if (seenConditions.Add("switch:" + cond))
                    {
                        var labels = sw.Sections
                            .SelectMany(s => s.Labels)
                            .Select(l => l.ToString().TrimEnd(':').Trim())
                            .Where(l => l != "default")
                            .ToList();
                        points.Add(new DecisionPoint("Switch", cond, false, labels));
                    }
                    break;
                }
                case ForEachStatementSyntax fe:
                {
                    var cond = fe.Expression.ToString();
                    if (seenConditions.Add("foreach:" + cond))
                        points.Add(new DecisionPoint("ForEach", cond, false, []));
                    break;
                }
                case ForStatementSyntax forStmt:
                {
                    if (!LoopTouchesVariablesOfInterest(forStmt, paramNames, returnIdentifiers))
                        break;
                    var cond = forStmt.Condition?.ToString() ?? "counter";
                    if (seenConditions.Add("for:" + cond))
                        points.Add(new DecisionPoint("For", cond, false, []));
                    break;
                }
                case WhileStatementSyntax whileStmt:
                {
                    if (!LoopTouchesVariablesOfInterest(whileStmt, paramNames, returnIdentifiers))
                        break;
                    var cond = whileStmt.Condition.ToString();
                    if (seenConditions.Add("while:" + cond))
                        points.Add(new DecisionPoint("While", cond, false, []));
                    break;
                }
                case DoStatementSyntax doStmt:
                {
                    if (!LoopTouchesVariablesOfInterest(doStmt, paramNames, returnIdentifiers))
                        break;
                    var cond = doStmt.Condition.ToString();
                    if (seenConditions.Add("do:" + cond))
                        points.Add(new DecisionPoint("DoWhile", cond, false, []));
                    break;
                }
            }
        }

        return points;
    }

    // ── Loop variable-of-interest filter ─────────────────────────────────────────

    /// <summary>
    /// Returns true when a loop is in the path of incoming or outgoing variables.
    ///
    /// For all loop types — Incoming check:
    ///   The loop condition or (for 'for' loops) initializer references a method parameter.
    ///   A constant-bound for loop is deterministic, so boundary tests on input are meaningless.
    ///
    /// For while/do-while only — Outgoing check:
    ///   The loop body writes to a variable that appears in a return statement.
    ///   'for' loops are excluded from the outgoing check because a constant bound makes the
    ///   loop deterministic regardless of what accumulates in the body.
    /// </summary>
    private static bool LoopTouchesVariablesOfInterest(
        SyntaxNode loop,
        HashSet<string> paramNames,
        HashSet<string> returnIdentifiers)
    {
        // Incoming: loop condition references a method parameter
        var conditionText = loop switch
        {
            ForStatementSyntax f   => f.Condition?.ToString() ?? "",
            WhileStatementSyntax w => w.Condition.ToString(),
            DoStatementSyntax d    => d.Condition.ToString(),
            _                      => ""
        };

        if (paramNames.Any(p => conditionText.Contains(p, StringComparison.Ordinal)))
            return true;

        // For 'for' loops also check the initializer, then stop — no outgoing check.
        // A for loop with a constant bound (no param ref) is deterministic: the iteration
        // count cannot be varied by the caller, so zero/one/multi boundary stubs add no value.
        if (loop is ForStatementSyntax forStmt)
        {
            var initText = forStmt.Declaration?.ToString()
                ?? string.Join(" ", forStmt.Initializers.Select(i => i.ToString()));
            return paramNames.Any(p => initText.Contains(p, StringComparison.Ordinal));
        }

        // Outgoing (while/do-while only): body writes to a variable that is returned
        var loopBody = loop switch
        {
            WhileStatementSyntax w => w.Statement,
            DoStatementSyntax d    => d.Statement,
            _                      => null
        };

        if (loopBody != null && returnIdentifiers.Count > 0)
        {
            var writtenInLoop = loopBody.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left)
                .OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.Text);

            if (writtenInLoop.Any(v => returnIdentifiers.Contains(v)))
                return true;
        }

        return false;
    }

    // ── Happy path ───────────────────────────────────────────────────────────────

    private static PathDrivenTestCase GenerateHappyPath(
        string methodName,
        List<ParameterSyntax> methodParams,
        List<ParameterSyntax> interfaceParams,
        MethodDeclarationSyntax method,
        bool needsAsync)
    {
        var arrange = new StringBuilder();
        foreach (var p in methodParams)
        {
            var typeName = p.Type?.ToString() ?? "object";
            var name = p.Identifier.Text;
            arrange.AppendLine($"            var {name} = {InferHappyValue(typeName, name)};");
        }

        foreach (var p in interfaceParams)
        {
            var typeName = p.Type!.ToString().TrimEnd('?');
            var mockField = GetMockFieldName(typeName);
            var fieldRef = GetFieldRef(typeName);

            if (method.Body != null)
            {
                var calls = method.Body.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                        && ma.Expression.ToString().Contains(fieldRef, StringComparison.OrdinalIgnoreCase))
                    .Take(2);

                foreach (var call in calls)
                {
                    if (call.Expression is MemberAccessExpressionSyntax ma2)
                        arrange.AppendLine(
                            $"            // TODO: {mockField}.Setup(m => m.{ma2.Name.Identifier.Text}(...)).ReturnsAsync(...);");
                }
            }
        }

        var paramList = string.Join(", ", methodParams.Select(p => p.Identifier.Text));
        var await_ = needsAsync ? "await " : "";

        var constraints = methodParams
            .Select(p => new PathInputConstraint(
                p.Identifier.Text,
                "valid non-null value",
                InferHappyValue(p.Type?.ToString() ?? "object", p.Identifier.Text)))
            .ToList();

        return new PathDrivenTestCase(
            $"{methodName}_HappyPath_ReturnsExpectedResult",
            "All inputs valid — method completes normally",
            constraints,
            "Method returns successfully without exception",
            arrange.ToString().TrimEnd(),
            $"            var result = {await_}_sut.{methodName}({paramList});",
            "            // TODO: Assert result equals expected value",
            "Happy path — mock setups are marked TODO; replace with real return values.");
    }

    // ── Per-decision-point cases ─────────────────────────────────────────────────

    private static List<PathDrivenTestCase> GenerateTestCasesForDecisionPoint(
        DecisionPoint dp,
        string methodName,
        List<ParameterSyntax> methodParams,
        bool needsAsync,
        int offset)
    {
        var cases = new List<PathDrivenTestCase>();
        var paramList = string.Join(", ", methodParams.Select(p => p.Identifier.Text));
        var await_ = needsAsync ? "await " : "";
        var shortCond = TruncateCondition(dp.Condition);

        switch (dp.Kind)
        {
            case "If":
            {
                var (trueParam, trueVal) = InferConstraint(dp.Condition, true, methodParams);
                if (trueParam != null)
                {
                    cases.Add(new PathDrivenTestCase(
                        $"{methodName}_{SanitizeName(dp.Condition)}_IsTrue_{offset + cases.Count + 1}",
                        $"Condition '{shortCond}' evaluates to true",
                        [new PathInputConstraint(trueParam, "satisfies true branch", trueVal)],
                        "Takes the if-body branch",
                        $"            // TODO: Arrange so '{shortCond}' is true" +
                        (trueVal != null && !trueVal.StartsWith("//")
                            ? $"\n            // Suggested: {trueParam} = {trueVal}" : ""),
                        $"            {await_}_sut.{methodName}({paramList});",
                        $"            // TODO: Assert behavior when '{shortCond}' is true"));
                }

                if (dp.HasElse)
                {
                    var (falseParam, falseVal) = InferConstraint(dp.Condition, false, methodParams);
                    if (falseParam != null)
                    {
                        cases.Add(new PathDrivenTestCase(
                            $"{methodName}_{SanitizeName(dp.Condition)}_IsFalse_{offset + cases.Count + 1}",
                            $"Condition '{shortCond}' evaluates to false — takes else branch",
                            [new PathInputConstraint(falseParam, "satisfies false/else branch", falseVal)],
                            "Takes the else branch",
                            $"            // TODO: Arrange so '{shortCond}' is false" +
                            (falseVal != null && !falseVal.StartsWith("//")
                                ? $"\n            // Suggested: {falseParam} = {falseVal}" : ""),
                            $"            {await_}_sut.{methodName}({paramList});",
                            $"            // TODO: Assert behavior when '{shortCond}' is false"));
                    }
                }
                break;
            }

            case "Switch":
            {
                foreach (var label in dp.Cases.Take(5))
                {
                    cases.Add(new PathDrivenTestCase(
                        $"{methodName}_{SanitizeName(dp.Condition)}_{SanitizeName(label)}_{offset + cases.Count + 1}",
                        $"Switch on '{dp.Condition}' — case {label}",
                        [new PathInputConstraint(dp.Condition, $"equals {label}", label)],
                        $"Executes case {label} branch",
                        $"            // TODO: Arrange so '{dp.Condition}' equals {label}",
                        $"            {await_}_sut.{methodName}({paramList});",
                        $"            // TODO: Assert behavior for case {label}"));
                }
                break;
            }

            case "ForEach":
            {
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_EmptyCollection_{offset + cases.Count + 1}",
                    $"ForEach over '{dp.Condition}' — empty collection, loop body never executes",
                    [new PathInputConstraint(dp.Condition, "empty collection", "[]")],
                    "Loop body is skipped entirely",
                    $"            // TODO: Arrange so '{dp.Condition}' is empty",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert behavior when collection is empty (e.g. no side-effects)"));

                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_WithItems_{offset + cases.Count + 1}",
                    $"ForEach over '{dp.Condition}' — collection has items, loop body executes",
                    [new PathInputConstraint(dp.Condition, "collection with at least one item", "[item]")],
                    "Loop body executes at least once",
                    $"            // TODO: Arrange so '{dp.Condition}' has at least one item",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert loop body effect (e.g. item processed)"));
                break;
            }

            case "For":
            {
                // Zero iterations: condition false on entry (boundary at 0)
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_ZeroIterations_{offset + cases.Count + 1}",
                    $"For loop '{shortCond}' — condition false on entry, body never executes",
                    [new PathInputConstraint(dp.Condition, "loop bound = 0 so condition is false immediately", "0")],
                    "Loop body never runs; result is the initial/default value",
                    $"            // TODO: Arrange so '{shortCond}' is false on first evaluation\n" +
                    $"            // e.g. set the upper-bound variable to 0",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert the result equals the identity/default (e.g. 0, empty, null)"));

                // One iteration: boundary test — condition true exactly once
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_OneIteration_{offset + cases.Count + 1}",
                    $"For loop '{shortCond}' — condition true exactly once (single-element boundary)",
                    [new PathInputConstraint(dp.Condition, "loop bound = 1 so body executes once", "1")],
                    "Loop body runs exactly once; good for single-element boundary validation",
                    $"            // TODO: Arrange so '{shortCond}' allows exactly one iteration\n" +
                    $"            // e.g. set the upper-bound variable to 1",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result for single-element input"));

                // Multiple iterations: representative working case
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_MultipleIterations_{offset + cases.Count + 1}",
                    $"For loop '{shortCond}' — multiple iterations (N > 1)",
                    [new PathInputConstraint(dp.Condition, "loop bound > 1", "3 or more")],
                    "Loop body runs N times; result accumulates across all iterations",
                    $"            // TODO: Arrange so '{shortCond}' allows multiple iterations",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result reflects all N iterations (e.g. sum, transformed list)"));
                break;
            }

            case "While":
            {
                // Never enters: condition false before first check
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_NeverEnters_{offset + cases.Count + 1}",
                    $"While loop '{shortCond}' — condition false before first check, body never executes",
                    [new PathInputConstraint(dp.Condition, "condition false immediately", "make condition evaluate to false")],
                    "Loop is entirely skipped; result is the pre-loop value",
                    $"            // TODO: Arrange so '{shortCond}' is false on first evaluation",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result equals initial/pre-loop value"));

                // Terminates after some iterations: condition eventually becomes false
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_Terminates_{offset + cases.Count + 1}",
                    $"While loop '{shortCond}' — executes then terminates when condition becomes false",
                    [new PathInputConstraint(dp.Condition, "condition true initially, false after N iterations", "finite input")],
                    "Loop executes at least once and exits normally",
                    $"            // TODO: Arrange so '{shortCond}' is true initially and becomes false after processing",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result reflects all processed iterations"));
                break;
            }

            case "DoWhile":
            {
                // Single pass: condition false immediately after first iteration
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_SinglePass_{offset + cases.Count + 1}",
                    $"Do-while loop '{shortCond}' — body executes once, condition false on first check",
                    [new PathInputConstraint(dp.Condition, "condition false after first pass", "make post-body condition false")],
                    "Body runs exactly once (do-while always executes at least once)",
                    $"            // TODO: Arrange so '{shortCond}' evaluates to false after the first iteration",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result reflects exactly one iteration"));

                // Multiple passes: condition stays true for several iterations
                cases.Add(new PathDrivenTestCase(
                    $"{methodName}_{SanitizeName(dp.Condition)}_MultiplePasses_{offset + cases.Count + 1}",
                    $"Do-while loop '{shortCond}' — body executes multiple times before condition becomes false",
                    [new PathInputConstraint(dp.Condition, "condition true for N passes", "N > 1")],
                    "Body runs N times; result accumulates across all passes",
                    $"            // TODO: Arrange so '{shortCond}' stays true for multiple passes before exiting",
                    $"            {await_}_sut.{methodName}({paramList});",
                    $"            // TODO: Assert result reflects all N passes"));
                break;
            }
        }

        return cases;
    }

    // ── Constraint inference ─────────────────────────────────────────────────────

    private static (string? paramName, string? value) InferConstraint(
        string condition, bool trueBranch, List<ParameterSyntax> methodParams)
    {
        condition = condition.Trim();

        if (condition.Contains("== null"))
        {
            var varName = condition.Replace("== null", "").Trim();
            return trueBranch ? (varName, "null") : (varName, "// TODO: non-null value");
        }
        if (condition.Contains("!= null"))
        {
            var varName = condition.Replace("!= null", "").Trim();
            return trueBranch ? (varName, "// TODO: non-null value") : (varName, "null");
        }
        if (condition.StartsWith("string.IsNullOrEmpty(") || condition.StartsWith("string.IsNullOrWhiteSpace("))
        {
            var start = condition.IndexOf('(') + 1;
            var varName = condition[start..^1].Trim();
            return trueBranch ? (varName, "\"\"") : (varName, "\"validValue\"");
        }
        if (condition.Contains(".Count == 0") || condition.Contains(".Count() == 0")
            || condition.Contains("!") && condition.Contains(".Any()"))
        {
            var varName = condition.TrimStart('!')
                [..condition.TrimStart('!').IndexOf('.', StringComparison.Ordinal)].Trim();
            return trueBranch ? (varName, "empty collection") : (varName, "collection with items");
        }
        if (condition.Contains("> 0"))
        {
            var varName = condition[..condition.IndexOf('>', StringComparison.Ordinal)].Trim();
            return trueBranch ? (varName, "1") : (varName, "0");
        }
        if (condition.Contains("< 0"))
        {
            var varName = condition[..condition.IndexOf('<', StringComparison.Ordinal)].Trim();
            return trueBranch ? (varName, "-1") : (varName, "0");
        }
        if (condition.Contains(">= 0"))
        {
            var varName = condition[..condition.IndexOf('>', StringComparison.Ordinal)].Trim();
            return trueBranch ? (varName, "0") : (varName, "-1");
        }

        // Simple boolean: if (flag) or if (!flag)
        if (condition.StartsWith("!") && !condition.Contains('(') && !condition.Contains(' '))
        {
            var varName = condition[1..].Trim();
            return (varName, trueBranch ? "false" : "true");
        }
        if (!condition.Contains(' ') && !condition.Contains('(') && condition.Length > 0)
        {
            return (condition, trueBranch ? "true" : "false");
        }

        // Match against method parameters
        foreach (var param in methodParams)
        {
            if (condition.Contains(param.Identifier.Text))
            {
                return (param.Identifier.Text,
                    trueBranch ? "// TODO: arrange to satisfy true branch" : "// TODO: arrange to satisfy false branch");
            }
        }

        // Fallback: use whole condition as the hint if short
        return (condition.Length <= 40 ? condition : null, null);
    }

    // ── Code generation ──────────────────────────────────────────────────────────

    private static string RenderTestClass(
        string className,
        string methodName,
        string ns,
        string framework,
        List<ParameterSyntax> interfaceParams,
        List<PathDrivenTestCase> testCases,
        bool needsAsync)
    {
        var fw = framework.ToLowerInvariant();
        var testAttr  = fw switch { "nunit" => "[Test]", "mstest" => "[TestMethod]", _ => "[Fact]" };
        var classAttr = fw switch { "nunit" => "[TestFixture]", "mstest" => "[TestClass]", _ => "" };
        var setupAttr = fw switch { "nunit" => "[SetUp]", "mstest" => "[TestInitialize]", _ => "" };
        var setupName = fw switch { "nunit" => "SetUp", "mstest" => "Initialize", _ => $"{className}_{methodName}_PathTests" };

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Moq;");
        if (fw == "nunit")  sb.AppendLine("using NUnit.Framework;");
        if (fw == "xunit")  sb.AppendLine("using Xunit;");
        if (fw == "mstest") sb.AppendLine("using Microsoft.VisualStudio.TestTools.UnitTesting;");
        sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.Tests");
        sb.AppendLine("{");
        if (!string.IsNullOrEmpty(classAttr)) sb.AppendLine($"    {classAttr}");
        sb.AppendLine($"    public class {className}_{methodName}_PathTests");
        sb.AppendLine("    {");

        foreach (var p in interfaceParams)
        {
            var typeName = p.Type!.ToString().TrimEnd('?');
            sb.AppendLine($"        private Mock<{typeName}> {GetMockFieldName(typeName)} = null!;");
        }
        sb.AppendLine($"        private {className} _sut = null!;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(setupAttr)) sb.AppendLine($"        {setupAttr}");
        sb.AppendLine(fw == "xunit"
            ? $"        public {className}_{methodName}_PathTests()"
            : $"        public void {setupName}()");
        sb.AppendLine("        {");
        foreach (var p in interfaceParams)
        {
            var typeName = p.Type!.ToString().TrimEnd('?');
            sb.AppendLine($"            {GetMockFieldName(typeName)} = new Mock<{typeName}>();");
        }
        var mockArgs = string.Join(", ",
            interfaceParams.Select(p => $"{GetMockFieldName(p.Type!.ToString().TrimEnd('?'))}.Object"));
        sb.AppendLine($"            _sut = new {className}({mockArgs});");
        sb.AppendLine("        }");

        foreach (var tc in testCases)
        {
            sb.AppendLine();
            sb.AppendLine($"        // Scenario: {tc.ScenarioDescription}");
            sb.AppendLine($"        // Expected: {tc.ExpectedOutcome}");
            if (tc.Notes != null)
                sb.AppendLine($"        // Note: {tc.Notes}");
            sb.AppendLine($"        {testAttr}");
            sb.AppendLine(needsAsync
                ? $"        public async Task {tc.TestMethodName}()"
                : $"        public void {tc.TestMethodName}()");
            sb.AppendLine("        {");
            sb.AppendLine("            // Arrange");
            if (!string.IsNullOrWhiteSpace(tc.ArrangeCode))
                sb.AppendLine(tc.ArrangeCode);
            sb.AppendLine();
            sb.AppendLine("            // Act");
            sb.AppendLine(tc.ActCode);
            sb.AppendLine();
            sb.AppendLine("            // Assert");
            sb.AppendLine(tc.AssertCode);
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string InferHappyValue(string typeName, string paramName) =>
        typeName.TrimEnd('?') switch
        {
            "string"                         => $"\"{paramName}Value\"",
            "int" or "long" or "short"       => "1",
            "double" or "float" or "decimal" => "1.0",
            "bool"                           => "true",
            "Guid"                           => "Guid.NewGuid()",
            "DateTime"                       => "DateTime.UtcNow",
            "DateTimeOffset"                 => "DateTimeOffset.UtcNow",
            "CancellationToken"              => "CancellationToken.None",
            var t when t.StartsWith("List<") => $"new {t}()",
            var t when t.StartsWith("IEnumerable<") ||
                       t.StartsWith("IReadOnlyList<") =>
                $"Array.Empty<{ExtractGenericArg(t)}>()",
            var t => $"new {t.TrimEnd('?')}() // TODO: provide valid instance"
        };

    private static string ExtractGenericArg(string typeName)
    {
        var start = typeName.IndexOf('<') + 1;
        var end   = typeName.LastIndexOf('>');
        return start > 0 && end > start ? typeName[start..end] : "object";
    }

    private static string GetMockFieldName(string typeName)
    {
        var baseName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        var withoutI = baseName.Length > 1 && char.IsUpper(baseName[1]) ? baseName[1..] : baseName;
        return $"_mock{withoutI}";
    }

    private static string GetFieldRef(string typeName)
    {
        var baseName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        var withoutI = baseName.Length > 1 && char.IsUpper(baseName[1]) ? baseName[1..] : baseName;
        return $"_{char.ToLowerInvariant(withoutI[0])}{withoutI[1..]}";
    }

    private static string SanitizeName(string input) =>
        new string(input.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())
            .TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    private static string TruncateCondition(string condition) =>
        condition.Length > 50 ? condition[..47] + "..." : condition;
}
