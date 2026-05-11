using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SafetyIssue(string FilePath, int Line, int Column, string Type, string Description);

public class SecurityAndSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SecurityAndSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    // Numeric keyword aliases used as cast targets (C# syntax, not CLR names)
    private static readonly HashSet<string> NumericKeywords = new(StringComparer.Ordinal)
    {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "char", "nint", "nuint"
    };

    // CLR type names for numeric value types (used when checking source type via semantic model)
    private static readonly HashSet<string> NumericClrNames = new(StringComparer.Ordinal)
    {
        "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Single", "Double", "Decimal", "Char", "IntPtr", "UIntPtr"
    };

    public async Task<List<SafetyIssue>> FindUnsafeTypeCastsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SafetyIssue>();

        // Use semantic model to determine source types for accurate exclusion of safe numeric casts
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var issues = new List<SafetyIssue>();

        // Look for direct cast expressions like (Type)obj instead of 'as' or pattern matching.
        // A numeric cast (e.g., (int)myDouble) is safe only if the SOURCE type is also numeric.
        // Casting from object/reference to numeric (e.g., (int)untypedObject) IS unsafe and is flagged.
        var casts = root.DescendantNodes().OfType<CastExpressionSyntax>();
        foreach (var cast in casts)
        {
            var castTypeName = cast.Type.ToString().Trim();

            // Only skip the cast if BOTH target type and source type are numeric value types
            if (NumericKeywords.Contains(castTypeName) && semanticModel != null)
            {
                var sourceType = semanticModel.GetTypeInfo(cast.Expression, cancellationToken).Type;
                bool sourceIsNumeric = sourceType is { IsValueType: true } &&
                    (NumericClrNames.Contains(sourceType.Name) || NumericKeywords.Contains(sourceType.Name));
                if (sourceIsNumeric) continue; // e.g., (int)myDouble — safe narrowing/widening
            }

            var loc = cast.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1, "UnsafeCast",
                $"Direct cast to '{castTypeName}' detected. Consider using 'as' operator or pattern matching 'is' to avoid InvalidCastException."));
        }

        return issues;
    }

    public async Task<List<SafetyIssue>> DetectMissingNullChecksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SafetyIssue>();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var issues = new List<SafetyIssue>();

        // Strategy: find public methods and constructors that accept non-nullable reference-type
        // parameters, use those parameters in the body, but never null-guard them.
        // This catches the most common cause of NullReferenceException — unguarded public API entry points.
        var candidates = root.DescendantNodes()
            .Where(n => n is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
            .Cast<BaseMethodDeclarationSyntax>();

        foreach (var method in candidates)
        {
            // Only public entry points need guards; private callers are trusted by convention
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

            // Abstract/extern/partial methods have no body to analyse
            var body = method.Body;
            var exprBody = method.ExpressionBody?.Expression;
            if (body == null && exprBody == null) continue;

            foreach (var param in method.ParameterList.Parameters)
            {
                if (param.Type == null) continue;

                // Skip params arrays — they are never null (just empty)
                if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))) continue;

                // Skip explicitly nullable types (string?, IDisposable?) — nullable-by-contract
                if (param.Type is NullableTypeSyntax) continue;

                // Use the semantic model to confirm the type is a reference type
                var typeSymbol = semanticModel?.GetTypeInfo(param.Type, cancellationToken).Type;
                if (typeSymbol == null || !typeSymbol.IsReferenceType) continue;

                // Skip parameters that have a null default value (optional nullable parameters)
                if (param.Default?.Value is LiteralExpressionSyntax defLit &&
                    defLit.IsKind(SyntaxKind.NullLiteralExpression)) continue;

                var paramName = param.Identifier.Text;

                // Skip if the parameter is not actually used in the body/expression
                // (unused params can't cause a null dereference in this method)
                // Also exclude nameof(param) — that's not a real dereference.
                SyntaxNode bodyNode = body ?? (SyntaxNode)exprBody!;
                bool isUsed = bodyNode.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.Text == paramName && !IsNameofExpression(id));
                if (!isUsed) continue;

                // Check for any form of null guard in the method body (block body only)
                if (body != null && HasNullGuard(body, paramName)) continue;

                // For expression-bodied methods: null-conditional access on the parameter is a guard
                if (exprBody != null)
                {
                    bool hasNullConditional = exprBody.DescendantNodesAndSelf()
                        .OfType<ConditionalAccessExpressionSyntax>()
                        .Any(ca => ca.Expression is IdentifierNameSyntax id && id.Identifier.Text == paramName);
                    if (hasNullConditional) continue;
                }

                var loc = param.GetLocation().GetLineSpan().StartLinePosition;
                var methodName = method is MethodDeclarationSyntax md ? md.Identifier.Text : ".ctor";
                issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1,
                    "MissingNullCheck",
                    $"Parameter '{paramName}' of type '{typeSymbol.Name}' in '{methodName}' is used without a null guard. " +
                    $"Add ArgumentNullException.ThrowIfNull({paramName}) or a null-check at the top of the method."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Returns true if the body contains any form of null guard for <paramref name="paramName"/>:
    /// <list type="bullet">
    ///   <item><c>if (param == null)</c> / <c>if (param is null)</c> / <c>if (null == param)</c></item>
    ///   <item><c>if (param != null)</c> / <c>if (param is not null)</c> (non-null guards)</item>
    ///   <item><c>param ?? throw</c> / <c>param ??= …</c></item>
    ///   <item><c>ArgumentNullException.ThrowIfNull(param)</c> / ThrowIfNullOrEmpty / ThrowIfNullOrWhiteSpace</item>
    /// </list>
    /// </summary>
    private static bool HasNullGuard(BlockSyntax body, string paramName)
    {
        // Pattern 1: if-statement with null equality / is-null pattern
        foreach (var ifStmt in body.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ConditionMentionsNullCheckOf(ifStmt.Condition, paramName)) return true;
        }

        // Pattern 2: param ?? expr  (null-coalescing — param is checked for null and replaced)
        foreach (var coalesce in body.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!coalesce.IsKind(SyntaxKind.CoalesceExpression)) continue;
            if (coalesce.Left is IdentifierNameSyntax id && id.Identifier.Text == paramName) return true;
        }

        // Pattern 3: param ??= expr
        foreach (var coalesceAssign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!coalesceAssign.IsKind(SyntaxKind.CoalesceAssignmentExpression)) continue;
            if (coalesceAssign.Left is IdentifierNameSyntax id && id.Identifier.Text == paramName) return true;
        }

        // Pattern 4: ArgumentNullException.ThrowIfNull / ThrowIfNullOrEmpty / ThrowIfNullOrWhiteSpace
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax mae => mae.Name.Identifier.Text,
                IdentifierNameSyntax idn => idn.Identifier.Text,
                _ => string.Empty
            };

            if (methodName is "ThrowIfNull" or "ThrowIfNullOrEmpty" or "ThrowIfNullOrWhiteSpace")
            {
                var args = invocation.ArgumentList.Arguments;
                if (args.Count > 0 &&
                    args[0].Expression is IdentifierNameSyntax argId &&
                    argId.Identifier.Text == paramName)
                    return true;
            }
        }

        return false;
    }

    private static bool ConditionMentionsNullCheckOf(ExpressionSyntax condition, string paramName)
    {
        // Binary equality/inequality: param == null / param != null / null == param / null != param
        if (condition is BinaryExpressionSyntax bin &&
            (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression)))
        {
            bool leftIsParam = bin.Left is IdentifierNameSyntax l && l.Identifier.Text == paramName;
            bool rightIsParam = bin.Right is IdentifierNameSyntax r && r.Identifier.Text == paramName;
            bool leftIsNull = bin.Left is LiteralExpressionSyntax ll && ll.IsKind(SyntaxKind.NullLiteralExpression);
            bool rightIsNull = bin.Right is LiteralExpressionSyntax rl && rl.IsKind(SyntaxKind.NullLiteralExpression);
            if ((leftIsParam && rightIsNull) || (rightIsParam && leftIsNull)) return true;
        }

        // Pattern matching: param is null / param is not null
        if (condition is IsPatternExpressionSyntax isPat &&
            isPat.Expression is IdentifierNameSyntax isId &&
            isId.Identifier.Text == paramName)
            return true;

        return false;
    }

    private static bool IsNameofExpression(IdentifierNameSyntax id)
    {
        // Check if identifier appears inside nameof(...)
        return id.Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is IdentifierNameSyntax n && n.Identifier.Text == "nameof");
    }

    // ── NullDereferenceChain ──────────────────────────────────────────────

    /// <summary>
    /// Detects chained member access (a.b.c) where the intermediate step (a.b) is a reference
    /// type that could be null — flagging the chain as a potential NullReferenceException.
    /// Only reports when the intermediate is not accessed via null-conditional (?.) and has no
    /// visible null guard in the containing method.
    /// </summary>
    public async Task<List<SafetyIssue>> FindNullDereferenceChainAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return [];

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return [];

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null) return [];

        var issues = new List<SafetyIssue>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body == null && method.ExpressionBody == null) continue;

            // Collect all intermediate member-access expressions that are themselves accessed further
            // Pattern: X.Y.Z → flag X.Y if it could be null and isn't ?.-guarded
            foreach (var outerMa in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                // We want cases where outerMa.Expression is itself a MemberAccessExpressionSyntax
                if (outerMa.Expression is not MemberAccessExpressionSyntax innerMa) continue;

                // If the inner access uses null-conditional, it's already guarded
                if (outerMa.IsKind(SyntaxKind.SimpleMemberAccessExpression) &&
                    outerMa.Parent is ConditionalAccessExpressionSyntax) continue;

                // Check if inner expression is already a conditional access (x?.y.z is fine for x?.y)
                if (innerMa.Parent is ConditionalAccessExpressionSyntax) continue;

                // Use semantic model to verify the inner type is a nullable reference type
                var innerType = semanticModel.GetTypeInfo(innerMa, ct).Type;
                if (innerType == null || !innerType.IsReferenceType) continue;

                // Check that there's no null guard for this expression in the method
                var innerExprText = innerMa.ToString();
                var methodBody = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                bool isGuarded = methodBody!.DescendantNodes().OfType<IfStatementSyntax>()
                    .Any(ifStmt => ExpressionNullGuarded(ifStmt.Condition, innerExprText));

                // Also accept null-conditional access at this level (x?.y)
                bool isNullConditional = outerMa.Ancestors()
                    .OfType<ConditionalAccessExpressionSyntax>()
                    .Any(ca => ca.Expression.ToString() == innerMa.Expression.ToString());

                if (!isGuarded && !isNullConditional)
                {
                    var loc = outerMa.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1,
                        "NullDereferenceChain",
                        $"Chained access '{outerMa}' dereferences '{innerExprText}' without a null check. " +
                        $"If '{innerExprText}' can be null, use '?.' or add a null guard."));
                }
            }
        }

        return issues
            .DistinctBy(i => (i.Line, i.Column))
            .ToList();
    }

    private static bool ExpressionNullGuarded(ExpressionSyntax condition, string exprText)
    {
        if (condition is BinaryExpressionSyntax bin &&
            (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression)))
        {
            bool leftMatch = bin.Left.ToString() == exprText;
            bool rightMatch = bin.Right.ToString() == exprText;
            bool hasNull = bin.Left is LiteralExpressionSyntax ll && ll.IsKind(SyntaxKind.NullLiteralExpression)
                        || bin.Right is LiteralExpressionSyntax rl && rl.IsKind(SyntaxKind.NullLiteralExpression);
            if ((leftMatch || rightMatch) && hasNull) return true;
        }
        if (condition is IsPatternExpressionSyntax isPat && isPat.Expression.ToString() == exprText)
            return true;
        return false;
    }

    // ── ArithmeticOverflow ────────────────────────────────────────────────

    /// <summary>
    /// Detects integer arithmetic that references MaxValue/MinValue boundary constants
    /// without being wrapped in a checked block — potential silent overflow.
    /// </summary>
    public async Task<List<SafetyIssue>> FindArithmeticOverflowRisksAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return [];

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return [];

        var issues = new List<SafetyIssue>();

        foreach (var binExpr in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!binExpr.IsKind(SyntaxKind.AddExpression) &&
                !binExpr.IsKind(SyntaxKind.SubtractExpression) &&
                !binExpr.IsKind(SyntaxKind.MultiplyExpression)) continue;

            bool involvesMaxMin = binExpr.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Any(ma => ma.Name.Identifier.Text is "MaxValue" or "MinValue" &&
                           ma.Expression.ToString() is "int" or "long" or "uint" or "ulong" or
                                                       "short" or "byte" or "Int32" or "Int64");

            if (!involvesMaxMin) continue;

            // If wrapped in checked { } — intentionally throws on overflow
            bool isChecked = binExpr.Ancestors().OfType<CheckedStatementSyntax>().Any() ||
                             binExpr.Ancestors().OfType<CheckedExpressionSyntax>().Any();
            if (isChecked) continue;

            var loc = binExpr.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1,
                "ArithmeticOverflowRisk",
                $"Arithmetic involving a boundary constant ({binExpr}) may overflow silently. " +
                "Wrap in 'checked {{ }}' to throw on overflow, or use a wider type."));
        }

        return issues;
    }
}
