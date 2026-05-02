using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.IO;
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

// ── New result types for MS bug-fix augmented tools (Tools 6–10) ─────────────

/// <summary>
/// Safety analysis result for <see cref="MsToolAugmentEngine.AnalyzeForeachForLinqConversionAsync"/>.
/// Reports whether the standard convert_foreach_linq tool is safe to use.
/// </summary>
public record ForeachLinqAnalysis(
    bool IsSafeToConvert,
    string CollectionVariableName,
    int StatementsBeforeForeach,  // # statements between collection decl and foreach that reference the collection
    string? BlockingReason,
    string Recommendation);

/// <summary>
/// Workspace health report from <see cref="MsToolAugmentEngine.GetWorkspaceHealthAsync"/>.
/// Fixes false-negative from standard roslyn-diagnose.
/// </summary>
public record WorkspaceHealthReport(
    bool IsOperational,
    bool HasLoadedSolution,
    string? SolutionPath,
    int ProjectCount,
    int DocumentCount,
    List<string> LoadErrors,
    string Summary);

/// <summary>
/// Preview result from <see cref="MsToolAugmentEngine.PreviewAddMissingUsingsAsync"/>.
/// The standard add_missing_usings tool ignores preview:true and applies changes anyway.
/// </summary>
public record AddUsingsPreview(
    bool SolutionRequired,
    List<string> UsingsToAdd,
    string? Warning,
    string UpdatedContent);  // full file content with usings added (preview only)

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
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
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
            var pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
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
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        var analysis = await AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, lineBefore, lineAfter, ct);
        if (!analysis.IsSafeToConvert)
            return MsAugmentResult.Fail(analysis.BlockingReason ?? "Switch cannot be safely converted.");

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault()!;
        var root = (await doc.GetSyntaxRootAsync(ct))!;
        var text = await doc.GetTextAsync(ct);

        var pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
        var sw = node.AncestorsAndSelf().OfType<SwitchStatementSyntax>().First();
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
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
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
        try { pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter); }
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

    // ── 6. FormatDocumentSafe ─────────────────────────────────────────────────
    // MS Bug: roslyn-format_document always applies changes immediately.
    // There is NO preview mode — preview: true is not even a parameter in the MS tool.
    // Fix: Use Roslyn's Formatter.Format() to compute formatted content without writing.
    // preview=true (default) returns formatted content WITHOUT writing to disk.
    // preview=false writes to disk AND updates the in-memory workspace.

    /// <summary>
    /// Formats a C# file using Roslyn's built-in formatter, with true preview support.
    /// Unlike the standard <c>format_document</c> tool, <c>preview=true</c> (the default)
    /// returns the formatted content WITHOUT modifying the file on disk.
    /// </summary>
    public async Task<MsAugmentResult> FormatDocumentSafeAsync(
        string filePath, bool preview = true, CancellationToken ct = default)
    {
        string source;
        try { source = await File.ReadAllTextAsync(filePath, ct); }
        catch (Exception ex) { return MsAugmentResult.Fail($"Could not read file '{filePath}': {ex.Message}"); }

        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        // Use an AdhocWorkspace for formatting options — no project or solution needed
        using var workspace = new AdhocWorkspace();
        var formattedRoot = Formatter.Format(root, workspace, cancellationToken: ct);
        var formatted = formattedRoot.ToFullString();

        if (!preview)
        {
            try { await File.WriteAllTextAsync(filePath, formatted, ct); }
            catch (Exception ex) { return MsAugmentResult.Fail($"Could not write file '{filePath}': {ex.Message}"); }

            // If a solution is loaded, keep the workspace in sync
            try
            {
                var currentSolution = _workspaceManager.CurrentSolution;
                if (currentSolution != null && currentSolution.GetDocumentIdsWithFilePath(filePath).Any())
                    await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<string, string> { [filePath] = formatted });
            }
            catch { /* workspace might not be loaded — safe to ignore */ }
        }

        return MsAugmentResult.Ok(formatted);
    }

    // ── 7. AnalyzeForeachForLinqConversion ────────────────────────────────────
    // MS Bug: roslyn-convert_foreach_linq silently overwrites the collection variable
    // with `new List<T>()`, discarding any elements added before the foreach.
    // Example: results.Add("header"); foreach (...) { results.Add(...); }
    //          → standard tool drops "header" entirely, silently producing wrong code.
    // Fix: Pre-flight analysis that detects unsafe modifications before the foreach.

    /// <summary>
    /// Pre-flight safety analysis for the standard <c>convert_foreach_linq</c> tool.
    /// Detects the case where the collection is modified before the foreach — which the
    /// standard tool silently destroys by re-initializing the collection variable.
    /// </summary>
    public async Task<ForeachLinqAnalysis> AnalyzeForeachForLinqConversionAsync(
        string filePath, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken ct = default)
    {
        string source;
        try { source = await File.ReadAllTextAsync(filePath, ct); }
        catch (Exception ex)
        {
            return new ForeachLinqAnalysis(false, "", 0,
                $"Could not read file: {ex.Message}", "Cannot analyze.");
        }

        var sourceText = SourceText.From(source);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        int pos;
        try { pos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
        catch (InvalidOperationException ex)
        {
            return new ForeachLinqAnalysis(false, "", 0, ex.Message, "Cannot analyze.");
        }

        var node = root.FindNode(new TextSpan(pos, contextSnippet.Length));
        var forEach = node.AncestorsAndSelf().OfType<ForEachStatementSyntax>().FirstOrDefault();

        if (forEach == null)
            return new ForeachLinqAnalysis(false, "", 0,
                "No foreach statement found at contextSnippet location.", "Cannot analyze.");

        // Find List.Add() calls in the foreach body to determine the collection variable
        var addCalls = forEach.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax mae
                          && mae.Name.Identifier.Text == "Add")
            .ToList();

        if (!addCalls.Any())
            return new ForeachLinqAnalysis(false, "", 0,
                "No .Add() calls found in foreach body — not a LINQ Select/ToList() candidate.",
                "Manual analysis required — this foreach may not be convertible to LINQ.");

        // All Add() calls must target the same collection
        var collectionNames = addCalls
            .Select(c => ((MemberAccessExpressionSyntax)c.Expression).Expression.ToString())
            .Distinct()
            .ToList();

        if (collectionNames.Count > 1)
            return new ForeachLinqAnalysis(false, collectionNames[0], 0,
                $"Multiple collections targeted by Add(): {string.Join(", ", collectionNames)}. " +
                "Cannot determine a single conversion target.",
                "Manual conversion required.");

        var collectionName = collectionNames[0];

        // Find the containing block to inspect statements between declaration and foreach
        if (forEach.Parent is not BlockSyntax containingBlock)
            return new ForeachLinqAnalysis(true, collectionName, 0, null,
                "Use standard convert_foreach_linq tool (no containing block to analyze further).");

        var stmtList = containingBlock.Statements.ToList();
        var foreachIndex = stmtList.IndexOf(forEach);

        // Locate the collection's declaration in the same block
        int declIndex = -1;
        for (int i = 0; i < foreachIndex; i++)
        {
            if (stmtList[i] is LocalDeclarationStatementSyntax localDecl &&
                localDecl.Declaration.Variables.Any(v => v.Identifier.Text == collectionName))
            {
                declIndex = i;
                break;
            }
        }

        if (declIndex < 0)
            return new ForeachLinqAnalysis(true, collectionName, 0, null,
                "Collection is not a local variable in this block. " +
                "Use standard convert_foreach_linq tool if the preceding code is safe.");

        // Count statements between declaration and foreach that reference the collection
        var statementsBetween = stmtList
            .Skip(declIndex + 1)
            .Take(foreachIndex - declIndex - 1)
            .Where(s => s.ToString().Contains(collectionName, StringComparison.Ordinal))
            .ToList();

        if (statementsBetween.Count > 0)
        {
            var examples = statementsBetween
                .Select(s => s.ToString().Trim())
                .Take(3)
                .ToList();
            return new ForeachLinqAnalysis(
                IsSafeToConvert: false,
                CollectionVariableName: collectionName,
                StatementsBeforeForeach: statementsBetween.Count,
                BlockingReason:
                    $"Collection '{collectionName}' is modified {statementsBetween.Count} time(s) before the foreach: " +
                    string.Join("; ", examples) +
                    ". The standard convert_foreach_linq tool would silently discard these modifications " +
                    "by re-initializing the variable with 'new List<T>()'.",
                Recommendation: "Manual conversion required — preserve pre-foreach modifications.");
        }

        return new ForeachLinqAnalysis(
            IsSafeToConvert: true,
            CollectionVariableName: collectionName,
            StatementsBeforeForeach: 0,
            BlockingReason: null,
            Recommendation: "Use standard convert_foreach_linq tool — collection has no modifications before the foreach.");
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────
    // MS Bug: roslyn-diagnose reports healthy: false even when 86/86 projects load
    // successfully, because the health check tests MSBuild path existence rather
    // than actual workspace state. A fully operational workspace with a loaded
    // solution is falsely reported as unhealthy.
    // Fix: targeted health check that reads actual workspace state.

    /// <summary>
    /// Returns a targeted workspace health report based on actual solution state.
    /// Fixes the false-negative in the standard <c>diagnose</c> tool, which reports
    /// <c>healthy: false</c> even when all projects load successfully.
    /// </summary>
    public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync(CancellationToken ct = default)
    {
        // Use CurrentSolution (sync, no throw) rather than GetBranchedSolutionAsync
        // to distinguish "no solution loaded" from "workspace error"
        Solution? currentSolution;
        try { currentSolution = _workspaceManager.CurrentSolution; }
        catch (Exception ex)
        {
            // Workspace itself threw — genuinely non-operational
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: false,
                HasLoadedSolution: false,
                SolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: [$"Workspace exception: {ex.Message}"],
                Summary: $"Workspace is NOT operational: {ex.Message}"));
        }

        var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();

        if (currentSolution == null)
        {
            // No solution is loaded — but the workspace itself is operational
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: true,
                HasLoadedSolution: false,
                SolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: loadErrors,
                Summary: "Workspace is operational. No solution is currently loaded. " +
                         "Call load_solution to load a .sln or .csproj file."));
        }

        var projectCount   = currentSolution.ProjectIds.Count;
        var documentCount  = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath   = currentSolution.FilePath ?? _workspaceManager.SolutionPath;

        return Task.FromResult(new WorkspaceHealthReport(
            IsOperational: true,
            HasLoadedSolution: true,
            SolutionPath: solutionPath,
            ProjectCount: projectCount,
            DocumentCount: documentCount,
            LoadErrors: loadErrors,
            Summary: $"Workspace operational. {projectCount} project(s) loaded, " +
                     $"{documentCount} document(s). " +
                     (loadErrors.Count > 0
                         ? $"{loadErrors.Count} load warning(s) recorded (non-fatal)."
                         : "No load errors.")));
    }

    // ── 9. PreviewAddMissingUsings ────────────────────────────────────────────
    // MS Bug: roslyn-add_missing_usings with preview:true silently APPLIES changes
    // to the file on disk anyway — preview is completely ignored. This was confirmed
    // in testing: the file IS modified even when preview:true is specified.
    // Fix: compute what usings WOULD be added using diagnostics + semantic search,
    // without ever touching the file.

    /// <summary>
    /// Computes which <c>using</c> directives would be added without modifying the file.
    /// Fixes the standard <c>add_missing_usings</c> tool's bug where <c>preview:true</c>
    /// is silently ignored and the file is modified on disk.
    /// </summary>
    public async Task<AddUsingsPreview> PreviewAddMissingUsingsAsync(
        string filePath, CancellationToken ct = default)
    {
        // Solution must be loaded — this tool requires semantic analysis
        var currentSolution = _workspaceManager.CurrentSolution;
        if (currentSolution == null)
            return new AddUsingsPreview(
                SolutionRequired: true,
                UsingsToAdd: [],
                Warning: "No solution is loaded. Load a solution first using load_solution. " +
                         "(The standard add_missing_usings tool requires a solution too.)",
                UpdatedContent: "");

        var doc = currentSolution.GetDocumentIdsWithFilePath(filePath)
            .Select(currentSolution.GetDocument)
            .FirstOrDefault();

        if (doc == null)
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: $"File '{filePath}' is not part of the loaded solution.",
                UpdatedContent: "");

        var root = await doc.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var model = await doc.GetSemanticModelAsync(ct);

        if (root == null || model == null)
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: "Could not obtain syntax root or semantic model.",
                UpdatedContent: "");

        // Find CS0246 ("type not found") and CS0103 ("name not found") diagnostics
        var diagnostics = model.GetDiagnostics(cancellationToken: ct)
            .Where(d => d.Id is "CS0246" or "CS0103")
            .ToList();

        if (!diagnostics.Any())
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: null,
                UpdatedContent: root.ToFullString());

        // Extract unresolved identifier names from diagnostics
        var unresolvedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diag in diagnostics)
        {
            var span = diag.Location.SourceSpan;
            var node = root.FindNode(span);
            var simpleName = node.AncestorsAndSelf()
                .OfType<SimpleNameSyntax>()
                .FirstOrDefault();
            if (simpleName != null)
                unresolvedNames.Add(simpleName.Identifier.Text);
        }

        // Search all projects in the solution for types matching each unresolved name
        var namespacesToAdd = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var typeName in unresolvedNames)
        {
            bool found = false;
            foreach (var proj in currentSolution.Projects)
            {
                var compilation = await proj.GetCompilationAsync(ct);
                if (compilation == null) continue;

                var types = compilation.GetSymbolsWithName(typeName, SymbolFilter.Type, ct);
                foreach (var type in types)
                {
                    var ns = type.ContainingNamespace?.ToDisplayString();
                    if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                    {
                        namespacesToAdd.Add(ns);
                        found = true;
                    }
                }
                if (found) break;
            }
            if (!found)
                warnings.Add($"Could not locate type '{typeName}' in the solution.");
        }

        // Filter out namespaces already present in the file
        var existingUsings = root.Usings
            .Select(u => u.Name?.ToString())
            .Where(n => n != null)
            .ToHashSet(StringComparer.Ordinal)!;

        var newNamespaces = namespacesToAdd
            .Where(ns => !existingUsings.Contains(ns))
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!newNamespaces.Any())
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: warnings.Any()
                    ? string.Join("; ", warnings)
                    : "All required namespaces are already imported.",
                UpdatedContent: root.ToFullString());

        // Build and insert new using directives without touching the file
        var newUsingNodes = newNamespaces
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" " + ns))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .ToArray();

        var newRoot = root.AddUsings(newUsingNodes);

        return new AddUsingsPreview(
            SolutionRequired: false,
            UsingsToAdd: newNamespaces,
            Warning: warnings.Any() ? string.Join("; ", warnings) : null,
            UpdatedContent: newRoot.ToFullString());
    }

    // ── 10. ExtractConstantSafe ───────────────────────────────────────────────
    // MS Bug: roslyn-extract_constant gives a cryptic error like
    // "Column 99 is beyond end of line" when coordinates don't match exactly,
    // because the tool takes raw 1-based line/column offsets and provides no
    // human-readable error messages when they are wrong.
    // Fix: uses contextSnippet (not line/col) to find the literal, validates it
    // before extraction, and gives human-readable errors.

    /// <summary>
    /// Extracts a literal expression to a named constant, using <c>contextSnippet</c>
    /// to locate the literal instead of fragile line/column coordinates.
    /// Fixes the standard <c>extract_constant</c> tool's cryptic "Column 99 is beyond
    /// end of line" error. Replaces ALL identical literals in the file.
    /// </summary>
    public async Task<MsAugmentResult> ExtractConstantSafeAsync(
        string filePath, string contextSnippet, string constantName,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken ct = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(constantName))
            return MsAugmentResult.Fail($"'{constantName}' is not a valid C# identifier.");

        string source;
        try { source = await File.ReadAllTextAsync(filePath, ct); }
        catch (Exception ex) { return MsAugmentResult.Fail($"Could not read file '{filePath}': {ex.Message}"); }

        var sourceText = SourceText.From(source);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        int pos;
        try { pos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
        catch (InvalidOperationException ex) { return MsAugmentResult.Fail(ex.Message); }

        // Find the nearest literal expression at or near the snippet position
        var node = root.FindNode(new TextSpan(pos, contextSnippet.Length));
        var literal = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault()
                   ?? node.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault();

        if (literal == null)
            return MsAugmentResult.Fail(
                "No literal expression found at contextSnippet location. " +
                "Provide a contextSnippet that directly contains or is adjacent to the literal.");

        if (!literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            !literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
            !literal.IsKind(SyntaxKind.CharacterLiteralExpression) &&
            !literal.IsKind(SyntaxKind.TrueLiteralExpression) &&
            !literal.IsKind(SyntaxKind.FalseLiteralExpression))
            return MsAugmentResult.Fail(
                $"Unsupported literal kind: {literal.Kind()}. " +
                "Only string, numeric, char, and bool literals can be extracted to constants.");

        // Find the containing type — needed to place the constant declaration
        var containingType = literal.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return MsAugmentResult.Fail("No containing type found — cannot place the constant declaration.");

        // Determine the C# type keyword for the constant
        var typeKeyword = literal.Kind() switch
        {
            SyntaxKind.StringLiteralExpression    => "string",
            SyntaxKind.NumericLiteralExpression   => DetermineNumericType(literal.Token),
            SyntaxKind.CharacterLiteralExpression => "char",
            _                                     => "bool"  // true/false
        };

        // Replace ALL identical literals in the file with the constant name
        var literalValueText = literal.Token.ValueText;
        var literalKind      = literal.Kind();

        var allMatching = root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.Kind() == literalKind && l.Token.ValueText == literalValueText)
            .ToList();

        var constantRef = SyntaxFactory.IdentifierName(constantName);
        var replacedRoot = root.ReplaceNodes(allMatching,
            (old, _) => (SyntaxNode)constantRef.WithTriviaFrom(old));

        // Locate the containing type in the rewritten tree and prepend the constant
        var newContainingType = replacedRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == containingType.Identifier.Text);

        if (newContainingType == null)
            return MsAugmentResult.Fail("Could not locate containing type after literal replacement.");

        // Build: private const <type> <name> = <value>;
        var constDecl = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(typeKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(constantName))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        literal.WithoutTrivia())))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.ConstKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed))
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed));

        var updatedType = newContainingType.WithMembers(
            newContainingType.Members.Insert(0, constDecl));

        var finalRoot = replacedRoot.ReplaceNode(newContainingType, updatedType);
        return MsAugmentResult.Ok(finalRoot.NormalizeWhitespace().ToFullString());
    }

    private static string DetermineNumericType(SyntaxToken token)
    {
        var text = token.Text.ToLowerInvariant();
        if (text.EndsWith('m')) return "decimal";
        if (text.EndsWith('d')) return "double";
        if (text.EndsWith('f')) return "float";
        if (text.EndsWith('l')) return "long";
        if (text.Contains('.'))  return "double";
        return "int";
    }
}
