using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynSentinel.Server;

// ── Result Types ──────────────────────────────────────────────────────────────

public record MsAugmentResult(bool Success, string? Error, string? UpdatedContent)
{
    public static MsAugmentResult Fail(string error) => new(false, error, null);
    public static MsAugmentResult Ok(string content) => new(true, null, content);
}

public record SwitchCaseInfo(
    string CaseLabel,
    List<string> AssignedVariables,
    bool HasMultipleAssignments);

public record SwitchConversionAnalysis(
    bool IsSafeToConvert,
    int TotalCases,
    List<SwitchCaseInfo> Cases,
    string? BlockingReason);

public record UsingsCleanupResult(
    int OriginalCount,
    int RemovedDuplicates,
    string UpdatedContent);

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Augments / fixes known bugs in the standard Microsoft roslyn-mcp server tools.
/// Each method documents which MS tool it replaces and why.
/// </summary>
public class MsToolAugmentEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public MsToolAugmentEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    // ── 1. EncapsulateFieldSafe ───────────────────────────────────────────────
    // MS Bug: encapsulate_field generates `private int SuccessCount` + property
    // `get { return SuccessCount; }` — same name → infinite recursion / compile error.
    // Fix: back field is always renamed to `_camelCase`.

    /// <summary>
    /// Encapsulates a public field as a private backing field + public property.
    /// The backing field is always named <c>_camelCase</c> to avoid the self-referential
    /// property bug in the standard <c>encapsulate_field</c> tool.
    /// </summary>
    public async Task<MsAugmentResult> EncapsulateFieldSafeAsync(
        string filePath, string fieldName, string? overridePropertyName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null) return MsAugmentResult.Fail($"File not found: {filePath}");

        var root = await doc.GetSyntaxRootAsync(ct);
        if (root == null) return MsAugmentResult.Fail("Could not parse syntax tree.");

        var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
        if (field == null)
            return MsAugmentResult.Fail($"Field '{fieldName}' not found in {filePath}.");

        // ── Compute names ──
        string backingName, propertyName;
        if (fieldName.StartsWith("_") && fieldName.Length > 1)
        {
            backingName = fieldName;
            var stripped = fieldName.Substring(1);
            propertyName = overridePropertyName ?? (char.ToUpper(stripped[0]) + stripped.Substring(1));
        }
        else
        {
            backingName = "_" + char.ToLower(fieldName[0]) + (fieldName.Length > 1 ? fieldName.Substring(1) : "");
            propertyName = overridePropertyName ?? (char.ToUpper(fieldName[0]) + (fieldName.Length > 1 ? fieldName.Substring(1) : ""));
        }

        // Validate: property name must differ from backing field name
        if (backingName == propertyName)
            return MsAugmentResult.Fail($"Computed names collide ('{backingName}'). Provide an overridePropertyName.");

        bool isReadOnly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        bool isStatic   = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var  fieldType  = field.Declaration.Type;
        var  varDecl    = field.Declaration.Variables.First(v => v.Identifier.Text == fieldName);
        var  initializer = varDecl.Initializer;

        // ── Step 1: rename all usages of fieldName → backingName in the tree
        //    (excluding the field declaration itself)
        var trackedRoot = root.TrackNodes(field);
        var usages = trackedRoot.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == fieldName
                      && !(id.Parent is VariableDeclaratorSyntax vd && vd.Identifier.Text == fieldName))
            .ToList();

        var renamedRoot = usages.Any()
            ? trackedRoot.ReplaceNodes(usages,
                (old, _) => SyntaxFactory.IdentifierName(backingName).WithTriviaFrom(old))
            : trackedRoot;

        // ── Step 2: replace the field declaration with backing field + property ──
        var currentField = renamedRoot.GetCurrentNode(field)!;

        // New private backing field
        var backingModifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (isStatic)   backingModifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        if (isReadOnly) backingModifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var newVarDeclarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(backingName));
        if (initializer != null) newVarDeclarator = newVarDeclarator.WithInitializer(initializer);

        var backingField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(fieldType.WithTrailingTrivia(SyntaxFactory.Space))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(newVarDeclarator)))
            .WithModifiers(SyntaxFactory.TokenList(backingModifiers))
            .WithLeadingTrivia(currentField.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed));

        // New public property
        var getBody = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(backingName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var accessors = new List<AccessorDeclarationSyntax> { getBody };
        if (!isReadOnly)
        {
            var setBody = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(backingName),
                        SyntaxFactory.IdentifierName("value"))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            accessors.Add(setBody);
        }

        var propModifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space) };
        if (isStatic) propModifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var property = SyntaxFactory.PropertyDeclaration(fieldType.WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Identifier(propertyName))
            .WithModifiers(SyntaxFactory.TokenList(propModifiers))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed))
            .WithTrailingTrivia(currentField.GetTrailingTrivia());

        var finalRoot = renamedRoot.ReplaceNode(currentField, new SyntaxNode[] { backingField, property });
        return MsAugmentResult.Ok(finalRoot.NormalizeWhitespace().ToFullString());
    }

    // ── 2. AnalyzeSwitchForPatternConversion ─────────────────────────────────
    // MS Bug: convert_to_pattern_matching silently drops variable assignments when
    // a switch case assigns to multiple variables. This pre-flight returns a full
    // analysis so callers can decide whether to proceed.

    /// <summary>
    /// Analyzes a switch statement to determine whether it is safe for pattern-matching
    /// conversion. Use this before calling the standard <c>convert_to_pattern_matching</c>
    /// tool to avoid silent data loss.
    /// </summary>
    public async Task<SwitchConversionAnalysis> AnalyzeSwitchForPatternConversionAsync(
        string filePath, string contextSnippet, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null)
            return new SwitchConversionAnalysis(false, 0, [], $"File not found: {filePath}");

        var root = await doc.GetSyntaxRootAsync(ct);
        var text = await doc.GetTextAsync(ct);
        if (root == null)
            return new SwitchConversionAnalysis(false, 0, [], "Could not parse syntax tree.");

        SwitchStatementSyntax? sw = null;
        try
        {
            var pos = ContextHelper.FindSnippetPosition(text, contextSnippet);
            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
            sw = node.AncestorsAndSelf().OfType<SwitchStatementSyntax>().FirstOrDefault();
        }
        catch (InvalidOperationException ex)
        {
            return new SwitchConversionAnalysis(false, 0, [], ex.Message);
        }

        if (sw == null)
            return new SwitchConversionAnalysis(false, 0, [], "No switch statement found at contextSnippet location.");

        var caseInfos = new List<SwitchCaseInfo>();
        foreach (var section in sw.Sections)
        {
            var label = section.Labels.FirstOrDefault()?.ToString() ?? "default";
            var assignments = section.Statements
                .OfType<ExpressionStatementSyntax>()
                .Select(e => e.Expression)
                .OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left.ToString())
                .Distinct()
                .ToList();

            caseInfos.Add(new SwitchCaseInfo(label, assignments, assignments.Count > 1));
        }

        var multiAssignCases = caseInfos.Where(c => c.HasMultipleAssignments).ToList();
        bool safe = !multiAssignCases.Any();
        string? blockingReason = safe ? null
            : $"Cannot safely convert: {multiAssignCases.Count} case(s) assign to multiple variables. " +
              string.Join("; ", multiAssignCases.Select(c => $"'{c.CaseLabel}' assigns [{string.Join(", ", c.AssignedVariables)}]")) +
              ". The standard tool will silently drop all but the last assignment. Refactor to a single assignment or helper method first.";

        return new SwitchConversionAnalysis(safe, caseInfos.Count, caseInfos, blockingReason);
    }

    // ── 3. ConvertSwitchToPatternSafe ─────────────────────────────────────────
    // MS Bug: convert_to_pattern_matching drops all but the last assignment in
    // multi-assignment cases. This version rejects those with a clear error message
    // and only converts when the output will be correct.

    /// <summary>
    /// Converts a switch statement to a switch expression. Unlike the standard
    /// <c>convert_to_pattern_matching</c> tool, this version rejects switch statements
    /// where cases assign to multiple variables — preventing silent data loss.
    /// </summary>
    public async Task<MsAugmentResult> ConvertSwitchToPatternSafeAsync(
        string filePath, string contextSnippet, CancellationToken ct = default)
    {
        var analysis = await AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, ct);
        if (!analysis.IsSafeToConvert)
            return MsAugmentResult.Fail(analysis.BlockingReason ?? "Switch cannot be safely converted.");

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault()!;
        var root = (await doc.GetSyntaxRootAsync(ct))!;
        var text = await doc.GetTextAsync(ct);

        var pos = ContextHelper.FindSnippetPosition(text, contextSnippet);
        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
        var sw = node.AncestorsAndSelf().OfType<SwitchStatementSyntax>().First();

        // Determine the single assigned variable (or detect return/throw patterns)
        string? targetVariable = null;
        bool isReturnSwitch = true;

        foreach (var section in sw.Sections)
        {
            var stmts = section.Statements
                .Where(s => !s.IsKind(SyntaxKind.BreakStatement)).ToList();

            if (stmts.Count == 1 && stmts[0] is ExpressionStatementSyntax exprStmt
                && exprStmt.Expression is AssignmentExpressionSyntax assign)
            {
                isReturnSwitch = false;
                var lhs = assign.Left.ToString();
                if (targetVariable == null) targetVariable = lhs;
                else if (targetVariable != lhs)
                    return MsAugmentResult.Fail($"Cases assign to different variables ('{targetVariable}' vs '{lhs}'). Cannot build a single switch expression.");
            }
            else if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax)
            {
                // valid return case
            }
            else if (stmts.Count == 1 && stmts[0] is ThrowStatementSyntax)
            {
                isReturnSwitch = false;
            }
            else if (!stmts.Any()) { /* empty case — fall-through */ }
            else
            {
                return MsAugmentResult.Fail(
                    $"Case '{section.Labels.First()}' has a complex body that cannot be expressed as a switch arm. " +
                    "Refactor to a single expression, return, or throw first.");
            }
        }

        // ── Build the switch expression arms ──
        var arms = new List<SwitchExpressionArmSyntax>();
        foreach (var section in sw.Sections)
        {
            foreach (var label in section.Labels)
            {
                var stmts = section.Statements
                    .Where(s => !s.IsKind(SyntaxKind.BreakStatement)).ToList();

                ExpressionSyntax armExpr;
                if (stmts.Count == 1 && stmts[0] is ExpressionStatementSyntax es
                    && es.Expression is AssignmentExpressionSyntax asgn)
                    armExpr = asgn.Right;
                else if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax ret && ret.Expression != null)
                    armExpr = ret.Expression;
                else if (stmts.Count == 1 && stmts[0] is ThrowStatementSyntax thr && thr.Expression != null)
                    armExpr = SyntaxFactory.ThrowExpression(thr.Expression);
                else
                    continue; // empty/fall-through — skip

                PatternSyntax pattern = label is DefaultSwitchLabelSyntax
                    ? SyntaxFactory.DiscardPattern()
                    : SyntaxFactory.ConstantPattern(((CaseSwitchLabelSyntax)label).Value);

                arms.Add(SyntaxFactory.SwitchExpressionArm(pattern, armExpr));
            }
        }

        var switchExpr = SyntaxFactory.SwitchExpression(
            sw.Expression.WithoutTrivia(),
            SyntaxFactory.SeparatedList(arms));

        SyntaxNode replacement;
        if (isReturnSwitch)
        {
            replacement = SyntaxFactory.ReturnStatement(switchExpr)
                .WithLeadingTrivia(sw.GetLeadingTrivia())
                .WithTrailingTrivia(sw.GetTrailingTrivia());
        }
        else if (targetVariable != null)
        {
            var assignStmt = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(targetVariable),
                    switchExpr));
            replacement = assignStmt
                .WithLeadingTrivia(sw.GetLeadingTrivia())
                .WithTrailingTrivia(sw.GetTrailingTrivia());
        }
        else
        {
            return MsAugmentResult.Fail("Could not determine replacement form (return/assignment). Manual conversion required.");
        }

        var newRoot = root.ReplaceNode(sw, replacement);
        return MsAugmentResult.Ok(newRoot.NormalizeWhitespace().ToFullString());
    }

    // ── 4. ConvertStringFormatToInterpolatedSmart ─────────────────────────────
    // MS Limitation: convert_to_interpolated_string requires the first argument of
    // string.Format() to be a string literal. It fails with "First argument must be
    // a string literal" when the format is a named constant. This version resolves
    // the constant via the semantic model before converting.

    /// <summary>
    /// Converts a <c>string.Format()</c> call to an interpolated string. Unlike the
    /// standard tool, this works when the format argument is a named constant
    /// (e.g., <c>string.Format(MyConst, arg1, arg2)</c>).
    /// </summary>
    public async Task<MsAugmentResult> ConvertStringFormatToInterpolatedSmartAsync(
        string filePath, string contextSnippet, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null) return MsAugmentResult.Fail($"File not found: {filePath}");

        var root = await doc.GetSyntaxRootAsync(ct);
        var text = await doc.GetTextAsync(ct);
        var model = await doc.GetSemanticModelAsync(ct);
        if (root == null || model == null) return MsAugmentResult.Fail("Could not load document.");

        // Find the string.Format invocation
        int pos;
        try { pos = ContextHelper.FindSnippetPosition(text, contextSnippet); }
        catch (InvalidOperationException ex) { return MsAugmentResult.Fail(ex.Message); }

        var invocation = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length))
            .AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(inv =>
            {
                var name = inv.Expression switch
                {
                    MemberAccessExpressionSyntax mae => mae.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                return name == "Format";
            });

        if (invocation == null)
            return MsAugmentResult.Fail("No string.Format() call found at contextSnippet location.");

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1)
            return MsAugmentResult.Fail("string.Format() has no arguments.");

        // ── Resolve the format string ──
        string? formatStr = null;

        if (args[0].Expression is LiteralExpressionSyntax lit &&
            lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            formatStr = lit.Token.ValueText;
        }
        else
        {
            // Try to resolve via semantic constant value
            var constValue = model.GetConstantValue(args[0].Expression, ct);
            if (constValue.HasValue && constValue.Value is string sv)
                formatStr = sv;
        }

        if (formatStr == null)
            return MsAugmentResult.Fail(
                "Could not resolve format string to a compile-time constant. " +
                "Only string literals and constant fields/properties are supported.");

        // ── Build the interpolated string ──
        var formatArgs = args.Skip(1).Select(a => a.Expression).ToList();
        var contents = new List<InterpolatedStringContentSyntax>();

        int i = 0;
        while (i < formatStr.Length)
        {
            if (formatStr[i] == '{' && i + 1 < formatStr.Length)
            {
                if (formatStr[i + 1] == '{') { contents.Add(MakeText("{{")); i += 2; continue; }
                int close = formatStr.IndexOf('}', i + 1);
                if (close < 0) { contents.Add(MakeText(formatStr.Substring(i))); break; }
                var spec = formatStr.Substring(i + 1, close - i - 1);
                var colonIdx = spec.IndexOf(':');
                string idxStr = colonIdx >= 0 ? spec.Substring(0, colonIdx) : spec;
                string? fmtSpec = colonIdx >= 0 ? spec.Substring(colonIdx) : null;

                if (int.TryParse(idxStr, out int argIdx) && argIdx < formatArgs.Count)
                {
                    var argExpr = formatArgs[argIdx];
                    var formatted = fmtSpec != null
                        ? SyntaxFactory.Interpolation(argExpr, null,
                            SyntaxFactory.InterpolationFormatClause(
                                SyntaxFactory.Token(SyntaxKind.ColonToken),
                                SyntaxFactory.Token(
                                    SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                                    fmtSpec.TrimStart(':'), fmtSpec.TrimStart(':'),
                                    SyntaxTriviaList.Empty)))
                        : SyntaxFactory.Interpolation(argExpr);
                    contents.Add(formatted);
                }
                else
                {
                    contents.Add(MakeText("{" + spec + "}"));
                }
                i = close + 1;
            }
            else if (formatStr[i] == '}' && i + 1 < formatStr.Length && formatStr[i + 1] == '}')
            {
                contents.Add(MakeText("}}")); i += 2;
            }
            else
            {
                int next = formatStr.IndexOfAny(new[] { '{', '}' }, i);
                string chunk = next < 0 ? formatStr.Substring(i) : formatStr.Substring(i, next - i);
                if (!string.IsNullOrEmpty(chunk)) contents.Add(MakeText(EscapeForInterpolated(chunk)));
                i = next < 0 ? formatStr.Length : next;
            }
        }

        var interpolated = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents),
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken))
            .WithTriviaFrom(invocation);

        var newRoot = root.ReplaceNode(invocation, interpolated);
        return MsAugmentResult.Ok(newRoot.NormalizeWhitespace().ToFullString());
    }

    // ── 5. SortAndDeduplicateUsings ───────────────────────────────────────────
    // MS Limitation: sort_usings only sorts; it does not remove duplicates.
    // remove_unused_usings removes unused but won't help if both copies are used.
    // This combines sort + dedup in one pass.

    /// <summary>
    /// Sorts <c>using</c> directives alphabetically (System.* first) AND removes
    /// duplicates in a single operation. Fixes the gap between <c>sort_usings</c>
    /// (no dedup) and <c>remove_unused_usings</c> (won't remove a duplicate that
    /// is technically "used").
    /// </summary>
    public async Task<UsingsCleanupResult> SortAndDeduplicateUsingsAsync(
        string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null) throw new InvalidOperationException($"File not found: {filePath}");

        var root = (await doc.GetSyntaxRootAsync(ct) as CompilationUnitSyntax)!;
        var original = root.Usings;
        int originalCount = original.Count;

        // Deduplicate: preserve first occurrence keyed on normalized name
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = original
            .Where(u => seen.Add(u.Name?.ToString() ?? u.ToString()))
            .ToList();

        int removedDuplicates = originalCount - deduped.Count;

        // Sort: System.* first (then sub-namespaces alphabetically), then everything else
        var sorted = deduped
            .OrderBy(u => !IsSystemUsing(u))     // System.* first
            .ThenBy(u => u.Name?.ToString() ?? u.ToString(), StringComparer.OrdinalIgnoreCase);

        var newUsings = SyntaxFactory.List(sorted.Select((u, idx) =>
        {
            // Clean up trivia — each using on its own line, no extra blanks
            var clean = u.WithLeadingTrivia(SyntaxFactory.TriviaList())
                         .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed));
            return idx == 0
                ? clean.WithLeadingTrivia(root.Usings.First().GetLeadingTrivia())
                : clean;
        }));

        var newRoot = root.WithUsings(newUsings);
        return new UsingsCleanupResult(originalCount, removedDuplicates, newRoot.NormalizeWhitespace().ToFullString());
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    private static bool IsSystemUsing(UsingDirectiveSyntax u)
    {
        var name = u.Name?.ToString() ?? "";
        return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
    }

    private static InterpolatedStringTextSyntax MakeText(string raw) =>
        SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(
                SyntaxTriviaList.Empty,
                SyntaxKind.InterpolatedStringTextToken,
                raw, raw,
                SyntaxTriviaList.Empty));

    private static string EscapeForInterpolated(string s) =>
        s.Replace("{", "{{").Replace("}", "}}");
}
