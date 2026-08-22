using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Basic;

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
    string UpdatedContent,
    bool WrittenToDisk = false);

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
    private static readonly char[] anyOf = new[] { '{', '}' };

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
        FilePath filePath, string fieldName, string? overridePropertyName = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null)
        {
            return MsAugmentResult.Fail($"File not found: {filePath}");
        }

        var root = await doc.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return MsAugmentResult.Fail("Could not parse syntax tree.");
        }

        var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
        if (field == null)
        {
            return MsAugmentResult.Fail($"Field '{fieldName}' not found in {filePath}.");
        }

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
        {
            return MsAugmentResult.Fail($"Computed names collide ('{backingName}'). Provide an overridePropertyName.");
        }

        bool isReadOnly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        bool isStatic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var fieldType = field.Declaration.Type;
        var varDecl = field.Declaration.Variables.First(v => v.Identifier.Text == fieldName);
        var initializer = varDecl.Initializer;

        // ── Step 1: rename all usages of fieldName → backingName in the tree
        //    (excluding the field declaration itself)
        var trackedRoot = root.TrackNodes(field);
        var usages = trackedRoot.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == fieldName
                      && !(id.Parent is VariableDeclaratorSyntax vd && vd.Identifier.Text == fieldName))
            .ToList();

        var renamedRoot = usages.Count != 0
            ? trackedRoot.ReplaceNodes(usages,
                (old, _) => SyntaxFactory.IdentifierName(backingName).WithTriviaFrom(old))
            : trackedRoot;

        // ── Step 2: replace the field declaration with backing field + property ──
        var currentField = renamedRoot.GetCurrentNode(field)!;

        // New private backing field
        var backingModifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (isStatic)
        {
            backingModifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        if (isReadOnly)
        {
            backingModifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        var newVarDeclarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(backingName));
        if (initializer != null)
        {
            newVarDeclarator = newVarDeclarator.WithInitializer(initializer);
        }

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
        if (isStatic)
        {
            propModifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

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
        FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null)
        {
            return new SwitchConversionAnalysis(false, 0, [], $"File not found: {filePath}");
        }

        var root = await doc.GetSyntaxRootAsync(cancellationToken);
        var text = await doc.GetTextAsync(cancellationToken);
        if (root == null)
        {
            return new SwitchConversionAnalysis(false, 0, [], "Could not parse syntax tree.");
        }

        SwitchStatementSyntax? sw = null;
        try
        {
            var pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
            sw = node.AncestorsAndSelf().OfType<SwitchStatementSyntax>().FirstOrDefault();
        }
        catch (ToolException ex)
        {
            return new SwitchConversionAnalysis(false, 0, [], ex.Message);
        }

        if (sw == null)
        {
            return new SwitchConversionAnalysis(false, 0, [], "No switch statement found at contextSnippet location.");
        }

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
        bool safe = multiAssignCases.Count == 0;
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
        FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (!analysis.IsSafeToConvert)
        {
            return MsAugmentResult.Fail(analysis.BlockingReason ?? "Switch cannot be safely converted.");
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault()!;
        var root = (await doc.GetSyntaxRootAsync(cancellationToken))!;
        var text = await doc.GetTextAsync(cancellationToken);

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
                if (targetVariable == null)
                {
                    targetVariable = lhs;
                }
                else if (targetVariable != lhs)
                {
                    return MsAugmentResult.Fail($"Cases assign to different variables ('{targetVariable}' vs '{lhs}'). Cannot build a single switch expression.");
                }
            }
            else if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax)
            {
                // valid return case
            }
            else if (stmts.Count == 1 && stmts[0] is ThrowStatementSyntax)
            {
                isReturnSwitch = false;
            }
            else if (stmts.Count == 0) { /* empty case — fall-through */ }
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
                {
                    armExpr = asgn.Right;
                }
                else if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax ret && ret.Expression != null)
                {
                    armExpr = ret.Expression;
                }
                else if (stmts.Count == 1 && stmts[0] is ThrowStatementSyntax thr && thr.Expression != null)
                {
                    armExpr = SyntaxFactory.ThrowExpression(thr.Expression);
                }
                else
                {
                    continue; // empty/fall-through — skip
                }

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
        FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null)
        {
            return MsAugmentResult.Fail($"File not found: {filePath}");
        }

        var root = await doc.GetSyntaxRootAsync(cancellationToken);
        var text = await doc.GetTextAsync(cancellationToken);
        var model = await doc.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
        {
            return MsAugmentResult.Fail("Could not load document.");
        }

        // Find the string.Format invocation
        int pos;
        try { pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter); }
        catch (ToolException ex) { return MsAugmentResult.Fail(ex.Message); }

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
        {
            return MsAugmentResult.Fail("No string.Format() call found at contextSnippet location.");
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1)
        {
            return MsAugmentResult.Fail("string.Format() has no arguments.");
        }

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
            var constValue = model.GetConstantValue(args[0].Expression, cancellationToken);
            if (constValue.HasValue && constValue.Value is string sv)
            {
                formatStr = sv;
            }
        }

        if (formatStr == null)
        {
            return MsAugmentResult.Fail(
                "Could not resolve format string to a compile-time constant. " +
                "Only string literals and constant fields/properties are supported.");
        }

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
                int next = formatStr.IndexOfAny(anyOf, i);
                string chunk = next < 0 ? formatStr.Substring(i) : formatStr.Substring(i, next - i);
                if (!string.IsNullOrEmpty(chunk))
                {
                    contents.Add(MakeText(EscapeForInterpolated(chunk)));
                }

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
        FilePath filePath, bool writeToFile = true, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null)
        {
            throw new InvalidOperationException($"File not found: {filePath}");
        }

        var root = (await doc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax)!;
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
        var updatedContent = newRoot.NormalizeWhitespace().ToFullString();

        if (writeToFile)
        {
            await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { [filePath] = updatedContent },
                cancellationToken: cancellationToken);
        }

        return new UsingsCleanupResult(originalCount, removedDuplicates, updatedContent, WrittenToDisk: writeToFile);
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

    /// <summary>
    /// Renders a type for use in a freshly synthesized parameter/return signature, ignoring any
    /// nullable annotation that came from flow-state analysis rather than the variable's actual
    /// declared nullability. <c>ILocalSymbol.Type</c> obtained via <c>SemanticModel.AnalyzeDataFlow</c>
    /// can carry <see cref="NullableAnnotation.Annotated"/> purely because the compiler's flow
    /// analysis is conservative at the region boundary (e.g. after a loop or multiple branches) —
    /// not because the variable was ever actually assignable to null. A generated parameter that
    /// blindly copies that annotation ends up typed <c>Foo?</c> for a variable that's really always
    /// non-null (e.g. <c>var sb = new StringBuilder();</c>, never reassigned), and the generated
    /// method body then dereferences it without a null check — producing a live CS8602 warning
    /// that didn't exist before extraction. Only a type explicitly declared/initialized as nullable
    /// (value types' <c>Nullable&lt;T&gt;</c>, or a reference type whose declared type is itself
    /// nullable) should keep the <c>?</c> here; this strips a flow-state-only annotation.
    /// </summary>
    private static string DisplayTypeForExtractedSignature(ITypeSymbol type)
    {
        var normalized = type.NullableAnnotation == NullableAnnotation.Annotated
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;
        return normalized.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

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
        FilePath filePath, bool preview = true, CancellationToken cancellationToken = default)
    {
        string source;
        try { source = await File.ReadAllTextAsync(filePath, cancellationToken); }
        catch (Exception ex) { return MsAugmentResult.Fail($"Could not read file '{filePath}': {ex.Message}"); }

        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        // Use an AdhocWorkspace for formatting options — no project or solution needed
        using var workspace = new AdhocWorkspace();
        var formattedRoot = Formatter.Format(root, workspace, cancellationToken: cancellationToken);
        var formatted = formattedRoot.ToFullString();

        if (!preview)
        {
            var currentSolution = _workspaceManager.CurrentSolution;
            if (currentSolution != null && currentSolution.GetDocumentIdsWithFilePath(filePath).Any())
            {
                // Solution is loaded — route through the shared chokepoint so this write gets
                // drift detection, pre-image capture, and workspace resync like every other tool.
                var result = await _workspaceManager.ApplyProposedChangesAsync(
                    new Dictionary<FilePath, string> { [filePath] = formatted },
                    cancellationToken: cancellationToken);
                if (!result.Success)
                {
                    return MsAugmentResult.Fail($"Could not write file '{filePath}': {result.Summary}");
                }
            }
            else
            {
                // No solution loaded — the chokepoint requires CurrentSolution, so fall back to a
                // direct write. Same fallback the previous code silently relied on via its try/catch.
                try { await File.WriteAllTextAsync(filePath, formatted, cancellationToken); }
                catch (Exception ex) { return MsAugmentResult.Fail($"Could not write file '{filePath}': {ex.Message}"); }
            }
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
        FilePath filePath, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        string source;
        try { source = await File.ReadAllTextAsync(filePath, cancellationToken); }
        catch (Exception ex)
        {
            return new ForeachLinqAnalysis(false, "", 0,
                $"Could not read file: {ex.Message}", "Cannot analyze.");
        }

        var sourceText = SourceText.From(source);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        int pos;
        try { pos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
        catch (ToolException ex)
        {
            return new ForeachLinqAnalysis(false, "", 0, ex.Message, "Cannot analyze.");
        }

        var node = root.FindNode(new TextSpan(pos, contextSnippet.Length));
        var forEach = node.AncestorsAndSelf().OfType<ForEachStatementSyntax>().FirstOrDefault();

        if (forEach == null)
        {
            return new ForeachLinqAnalysis(false, "", 0,
                "No foreach statement found at contextSnippet location.", "Cannot analyze.");
        }

        // Find List.Add() calls in the foreach body to determine the collection variable
        var addCalls = forEach.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax mae
                          && mae.Name.Identifier.Text == "Add")
            .ToList();

        if (addCalls.Count == 0)
        {
            return new ForeachLinqAnalysis(false, "", 0,
                "No .Add() calls found in foreach body — not a LINQ Select/ToList() candidate.",
                "Manual analysis required — this foreach may not be convertible to LINQ.");
        }

        // All Add() calls must target the same collection
        var collectionNames = addCalls
            .Select(c => ((MemberAccessExpressionSyntax)c.Expression).Expression.ToString())
            .Distinct()
            .ToList();

        if (collectionNames.Count > 1)
        {
            return new ForeachLinqAnalysis(false, collectionNames[0], 0,
                $"Multiple collections targeted by Add(): {string.Join(", ", collectionNames)}. " +
                "Cannot determine a single conversion target.",
                "Manual conversion required.");
        }

        var collectionName = collectionNames[0];

        // Find the containing block to inspect statements between declaration and foreach
        if (forEach.Parent is not BlockSyntax containingBlock)
        {
            return new ForeachLinqAnalysis(true, collectionName, 0, null,
                "Use standard convert_foreach_linq tool (no containing block to analyze further).");
        }

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
        {
            return new ForeachLinqAnalysis(true, collectionName, 0, null,
                $"Collection '{collectionName}' is not a local variable in this block. " +
                "Use standard convert_foreach_linq tool if the preceding code is safe.");
        }

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
        FilePath filePath, CancellationToken cancellationToken = default)
    {
        // Solution must be loaded — this tool requires semantic analysis
        var currentSolution = _workspaceManager.CurrentSolution;
        if (currentSolution == null)
        {
            return new AddUsingsPreview(
                SolutionRequired: true,
                UsingsToAdd: [],
                Warning: "No solution is loaded. Load a solution first using load_solution. " +
                         "(The standard add_missing_usings tool requires a solution too.)",
                UpdatedContent: "");
        }

        var doc = currentSolution.GetDocumentIdsWithFilePath(filePath)
            .Select(currentSolution.GetDocument)
            .FirstOrDefault();

        if (doc == null)
        {
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: $"File '{filePath}' is not part of the loaded solution.",
                UpdatedContent: "");
        }

        var root = await doc.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var model = await doc.GetSemanticModelAsync(cancellationToken);

        if (root == null || model == null)
        {
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: "Could not obtain syntax root or semantic model.",
                UpdatedContent: "");
        }

        // Find CS0246 ("type not found") and CS0103 ("name not found") diagnostics
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id is "CS0246" or "CS0103")
            .ToList();

        if (diagnostics.Count == 0)
        {
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: null,
                UpdatedContent: root.ToFullString());
        }

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
            {
                unresolvedNames.Add(simpleName.Identifier.Text);
            }
        }

        // Search all projects in the solution for types matching each unresolved name
        var namespacesToAdd = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var typeName in unresolvedNames)
        {
            bool found = false;
            foreach (var proj in currentSolution.Projects)
            {
                var compilation = await proj.GetCompilationAsync(cancellationToken);
                if (compilation == null)
                {
                    continue;
                }

                var types = compilation.GetSymbolsWithName(typeName, SymbolFilter.Type, cancellationToken);
                foreach (var type in types)
                {
                    var ns = type.ContainingNamespace?.ToDisplayString();
                    if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                    {
                        namespacesToAdd.Add(ns);
                        found = true;
                    }
                }
                if (found)
                {
                    break;
                }
            }
            if (!found)
            {
                warnings.Add($"Could not locate type '{typeName}' in the solution.");
            }
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

        if (newNamespaces.Count == 0)
        {
            return new AddUsingsPreview(
                SolutionRequired: false,
                UsingsToAdd: [],
                Warning: warnings.Count != 0
                    ? string.Join("; ", warnings)
                    : "All required namespaces are already imported.",
                UpdatedContent: root.ToFullString());
        }

        // Build and insert new using directives without touching the file
        var newUsingNodes = newNamespaces
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" " + ns))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .ToArray();

        var newRoot = root.AddUsings(newUsingNodes);

        return new AddUsingsPreview(
            SolutionRequired: false,
            UsingsToAdd: newNamespaces,
            Warning: warnings.Count != 0 ? string.Join("; ", warnings) : null,
            UpdatedContent: newRoot.ToFullString());
    }

    // ── 10. ExtractConstantSafe ───────────────────────────────────────────────
    // MS Bug: roslyn-extract_constant gives a cryptic error like
    // "Column 99 is beyond end of line" when coordinates don't match ExtractConstantSafeAsync,
    // because the tool takes raw 1-based line/column offsets and provides no
    // human-readable error messages when they are wrong.
    // Fix: uses contextSnippet (not line/col) to find the literal, validates it
    // before ExtractConstantSafeAsync, and gives human-readable errors.

    /// <summary>
    /// Extracts a literal expression to a named constant, using <c>contextSnippet</c>
    /// to locate the literal instead of fragile line/column coordinates.
    /// Fixes the standard <c>extract_constant</c> tool's cryptic "Column 99 is beyond
    /// end of line" error. Replaces ALL identical literals in the file.
    /// </summary>
    public async Task<MsAugmentResult> ExtractConstantSafeAsync(
        FilePath filePath, string contextSnippet, string constantName,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(constantName))
        {
            return MsAugmentResult.Fail($"'{constantName}' is not a valid C# identifier.");
        }

        string source;
        try { source = await File.ReadAllTextAsync(filePath, cancellationToken); }
        catch (Exception ex) { return MsAugmentResult.Fail($"Could not read file '{filePath}': {ex.Message}"); }

        var sourceText = SourceText.From(source);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        int pos;
        try { pos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
        catch (ToolException ex) { return MsAugmentResult.Fail(ex.Message); }

        // Find the nearest literal expression at or near the snippet position
        var node = root.FindNode(new TextSpan(pos, contextSnippet.Length));
        var literal = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault()
                   ?? node.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault();

        if (literal == null)
        {
            return MsAugmentResult.Fail(
                "No literal expression found at contextSnippet location. " +
                "Provide a contextSnippet that directly contains or is adjacent to the literal.");
        }

        if (!literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            !literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
            !literal.IsKind(SyntaxKind.CharacterLiteralExpression) &&
            !literal.IsKind(SyntaxKind.TrueLiteralExpression) &&
            !literal.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            return MsAugmentResult.Fail(
                $"Unsupported literal kind: {literal.Kind()}. " +
                "Only string, numeric, char, and bool literals can be extracted to constants.");
        }

        // Find the containing type — needed to place the constant declaration
        var containingType = literal.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            return MsAugmentResult.Fail("No containing type found — cannot place the constant declaration.");
        }

        // Determine the C# type keyword for the constant
        var typeKeyword = literal.Kind() switch
        {
            SyntaxKind.StringLiteralExpression => "string",
            SyntaxKind.NumericLiteralExpression => DetermineNumericType(literal.Token),
            SyntaxKind.CharacterLiteralExpression => "char",
            _ => "bool"  // true/false
        };

        // Replace ALL identical literals in the file with the constant name
        var literalValueText = literal.Token.ValueText;
        var literalKind = literal.Kind();

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
        {
            return MsAugmentResult.Fail("Could not locate containing type after literal replacement.");
        }

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
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed))
            .WithAddedByComment("ExtractConstantSafe");

        var updatedType = newContainingType.WithMembers(
            newContainingType.Members.Insert(0, constDecl));

        var finalRoot = replacedRoot.ReplaceNode(newContainingType, updatedType);
        return MsAugmentResult.Ok(finalRoot.NormalizeWhitespace().ToFullString());
    }

    private static string DetermineNumericType(SyntaxToken token)
    {
        var text = token.Text.ToLowerInvariant();
        if (text.EndsWith('m'))
        {
            return "decimal";
        }

        if (text.EndsWith('d'))
        {
            return "double";
        }

        if (text.EndsWith('f'))
        {
            return "float";
        }

        if (text.EndsWith('l'))
        {
            return "long";
        }

        if (text.Contains('.'))
        {
            return "double";
        }

        return "int";
    }

    // ── 11. GenerateToStringSafe ──────────────────────────────────────────────
    // MS Bug: generate_tostring produces $"TypeName { Prop1 = {Prop1} }" where the
    // outer literal braces are NOT escaped, causing CS8086 (invalid interpolation hole).
    // Fix: emit {{ and }} for literal braces surrounding the member list.

    /// <summary>
    /// Generates a <c>ToString()</c> override for a type using a properly-escaped
    /// interpolated string. Fixes the standard <c>generate_tostring</c> bug where
    /// literal <c>{</c> in the format section is left unescaped, causing CS8086.
    /// </summary>
    /// <param name="filePath">Absolute path to the .cs file.</param>
    /// <param name="typeName">Name of the type to add ToString() to.</param>
    /// <param name="members">Optional explicit list of property/field names to include.
    ///     If null/empty, all public instance properties and fields are used.</param>
    public async Task<MsAugmentResult> GenerateToStringSafeAsync(
        FilePath filePath, string typeName, IList<string>? members = null,
        CancellationToken cancellationToken = default)
    {
        // Read source: prefer workspace (always in sync, supports testability)
        // then fall back to disk for files not loaded in the solution.
        string source;
        var currentSolution = _workspaceManager.CurrentSolution;
        var wsDoc = currentSolution?.GetDocumentIdsWithFilePath(filePath)
            .Select(currentSolution.GetDocument)
            .FirstOrDefault();

        if (wsDoc != null)
        {
            var wsText = await wsDoc.GetTextAsync(cancellationToken);
            source = wsText.ToString();
        }
        else
        {
            try { source = await File.ReadAllTextAsync(filePath, cancellationToken); }
            catch (Exception ex) { return MsAugmentResult.Fail($"Could not read '{filePath}': {ex.Message}"); }
        }

        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        var typeDecl = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);

        if (typeDecl == null)
        {
            return MsAugmentResult.Fail($"Type '{typeName}' not found in {filePath}.");
        }

        if (typeDecl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "ToString" && !m.ParameterList.Parameters.Any()))
        {
            return MsAugmentResult.Fail(
                $"'{typeName}' already has a ToString() override. Remove it first.");
        }

        // Collect members to include in the ToString output
        var SelectedMembers = new List<string>();
        if (members != null && members.Count > 0)
        {
            SelectedMembers.AddRange(members);
        }
        else
        {
            SelectedMembers.AddRange(
                typeDecl.Members.OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                             && !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                             && (p.AccessorList?.Accessors.Any(
                                     a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true
                                 || p.ExpressionBody != null))
                    .Select(p => p.Identifier.Text));

            SelectedMembers.AddRange(
                typeDecl.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                             && !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)));
        }

        if (SelectedMembers.Count == 0)
        {
            return MsAugmentResult.Fail(
                $"No public properties or fields found on '{typeName}'. Specify members explicitly.");
        }

        // Build the interpolated string: $"TypeName {{ Prop1 = {Prop1}, Prop2 = {Prop2} }}"
        // Using {{ and }} to produce literal braces in C# interpolated strings (no CS8086).
        // NOTE: do NOT use a C# interpolated string here — $"{typeName} {{ " would evaluate
        // {{ to a single { at runtime, defeating the escape. Use concatenation instead.
        var contents = new List<InterpolatedStringContentSyntax>
        {
            MakeText(typeName + " {{ ")
        };

        for (int i = 0; i < SelectedMembers.Count; i++)
        {
            if (i > 0)
            {
                contents.Add(MakeText(", "));
            }

            contents.Add(MakeText($"{SelectedMembers[i]} = "));
            contents.Add(SyntaxFactory.Interpolation(
                SyntaxFactory.IdentifierName(SelectedMembers[i])));
        }

        contents.Add(MakeText(" }}"));

        var interpolated = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents),
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken));

        var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)))
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(interpolated))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAddedByComment("Generate(kind: generate_to_string_safe)");

        var newTypeDecl = typeDecl.AddMembers(method);
        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        return MsAugmentResult.Ok(newRoot.NormalizeWhitespace().ToFullString());
    }

    // ── 12. ExtractMethodSafe ─────────────────────────────────────────────────
    // MS Bug: extract_method generates `private void MethodName(...)` when the
    // Selected block ends with `return <expression>`, producing a compile error
    // because the extracted method has the wrong (void) return type.
    // Fix: use SemanticModel.GetTypeInfo() on the return expression, and
    // DataFlowAnalysis.DataFlowsIn to determine the correct parameter list.
    //
    // ── Known issue / fix history — read this before touching anything below ──
    // This tool has repeatedly shipped "technically succeeded, semantically wrong"
    // bugs: it returns Success=true, compiles clean, and passes existing tests,
    // while quietly producing the wrong extraction. Every case so far was found by
    // a live agent run, not by the existing test suite, because the sample/test
    // code never exercised the exact resulting shape. Treat a green build/test run
    // as necessary, not sufficient, when changing this method — re-derive expected
    // output by hand for any new contextSnippet shape you touch.
    //
    // 1. (fixed) Nullable over-annotation on synthesized parameters/return types.
    //    Roslyn's DataFlowAnalysis reports DataFlowsIn/DataFlowsOut symbols with a
    //    flow-state-conservative NullableAnnotation (e.g. `StringBuilder?` for a
    //    local that is unconditionally constructed and never reassigned). Copying
    //    that annotation verbatim into the generated signature produced a live
    //    CS8602 warning that did not exist before extraction. Fixed via
    //    DisplayTypeForExtractedSignature(), which strips NullableAnnotation.Annotated
    //    before calling ToDisplayString(). If a new nullable-looking parameter shows
    //    up in generated output, check whether it's flow-state noise vs. a genuine
    //    (correctly) nullable declared type before "fixing" it again.
    //
    // 2. (fixed) Single-statement selection INSIDE a loop body, sibling statement(s)
    //    silently dropped. contextSnippet matching only one statement inside a loop
    //    whose body has other statements (e.g. `runningTotal += x;` inside
    //    `foreach { runningTotal += x; totalUnits += y; }`) used to extract just
    //    that one statement into a method called once per iteration, silently
    //    dropping the sibling statement and the loop itself from the extraction.
    //    Guarded below: when stmtsInSelection.Count == 1 and block.Parent is a loop
    //    construct with block.Statements.Count > 1, AnalyzeDataFlow on the single
    //    statement checks DataFlowsIn for a local not declared by the loop itself;
    //    if found, refuse rather than guess.
    //
    // 3. (fixed) Single-statement selection BEFORE a loop in the same block —
    //    same ambiguity, different shape, and the guard from #2 did NOT catch it.
    //    contextSnippet matching only an accumulator's *initializer* (e.g.
    //    `decimal runningTotal = 0m;`), with the loop that actually accumulates
    //    into it appearing later in the *enclosing method body* block (not inside
    //    the loop), was extracted alone. The result: a method that always returns
    //    the initializer's value (e.g. `return 0m;`), with the foreach/its other
    //    local (`totalUnits`) silently stranded in the caller. #2's guard never
    //    fired because it only checks `block.Parent is <LoopType>` — here
    //    block.Parent is the method declaration, not a loop. Guarded below by
    //    scanning forward from the matched statement's index, within the SAME
    //    block, for a later loop statement; if found, AnalyzeDataFlow on both the
    //    matched statement (VariablesDeclared ∪ WrittenInside) and the loop
    //    (DataFlowsIn ∪ ReadInside ∪ WrittenInside) checks for a shared name.
    //    ⚠ This still only looks one block deep and only forward in the same
    //    block's direct statement list — a variant where the loop is nested inside
    //    an intervening `if`/`using`/nested block rather than a direct sibling
    //    statement would NOT be caught by either guard. If you find that shape
    //    failing silently, it's the same bug class, not a new one — generalize the
    //    forward-scan to walk into intervening non-loop block-containing
    //    statements' descendants, not just block.Statements directly.
    //
    // General lesson from all three: this tool's failure mode is never a thrown
    // exception or a compile error — it's a *plausible-looking, compiling, silently
    // incomplete* extraction. When adding new heuristics/guards, prefer refusing
    // with a specific, actionable error (naming the ambiguity and what a wider
    // contextSnippet should cover) over guessing a narrower scope than the caller
    // likely intended. When diagnosing a NEW report of "ExtractMethodSafe did
    // something weird," reproduce with a real dotnet build first — GetDiagnostics'
    // error *count* is not enough, and neither is the tool's own Success flag.

    /// <summary>
    /// Extract a block of statements into a new private method using semantic
    /// analysis to determine the correct return type. Fixes the standard
    /// <c>extract_method</c> bug where Selections ending with <c>return expr</c>
    /// produce <c>void</c> return type instead of the expression's actual type.
    /// </summary>
    /// <param name="filePath">Absolute path to the .cs file (must be in loaded solution).</param>
    /// <param name="newMethodName">Valid C# identifier for the new method.</param>
    /// <param name="contextSnippet">
    /// A short unique code snippet identifying the Selection.
    /// When <paramref name="extractEntireBody"/> is <c>true</c>, treated as the name of
    /// the source method whose entire body is extracted.
    /// </param>
    /// <param name="lineBefore">Optional line immediately before the snippet for disambiguation.</param>
    /// <param name="lineAfter">Optional line immediately after the snippet for disambiguation.</param>
    /// <param name="extractEntireBody">
    /// When <c>true</c>, <paramref name="contextSnippet"/> is interpreted as a method name
    /// and the entire block body of that method is used as the Selection.
    /// <paramref name="lineBefore"/> and <paramref name="lineAfter"/> are ignored.
    /// Expression-bodied methods are not supported in this mode.
    /// </param>
    public async Task<MsAugmentResult> ExtractMethodSafeAsync(
        FilePath filePath, string newMethodName, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        bool extractEntireBody = false,
        CancellationToken cancellationToken = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(newMethodName))
        {
            return MsAugmentResult.Fail($"'{newMethodName}' is not a valid C# identifier.");
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var doc = solution.GetDocumentIdsWithFilePath(filePath)
            .Select(solution.GetDocument)
            .FirstOrDefault();
        if (doc == null)
        {
            return MsAugmentResult.Fail($"File not found in solution: {filePath}");
        }

        var root = await doc.GetSyntaxRootAsync(cancellationToken);
        var model = await doc.GetSemanticModelAsync(cancellationToken);
        var sourceText = await doc.GetTextAsync(cancellationToken);
        if (root == null || model == null)
        {
            return MsAugmentResult.Fail("Could not load semantic model.");
        }

        BlockSyntax? block;
        List<StatementSyntax> stmtsInSelection;

        if (extractEntireBody)
        {
            var candidates = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == contextSnippet)
                .ToList();

            if (candidates.Count == 0)
                return MsAugmentResult.Fail($"No method named '{contextSnippet}' found in {filePath}.");
            if (candidates.Count > 1)
                return MsAugmentResult.Fail(
                    $"'{contextSnippet}' is overloaded ({candidates.Count} declarations). " +
                    "Use contextSnippet + lineBefore/lineAfter to identify the correct overload.");

            var sourceMethod = candidates[0];
            if (sourceMethod.Body == null)
                return MsAugmentResult.Fail(
                    $"'{contextSnippet}' has an expression body (=> ...) which is not supported " +
                    "with extractEntireBody. Use the contextSnippet path instead.");

            block = sourceMethod.Body;
            stmtsInSelection = block.Statements.ToList();

            if (stmtsInSelection.Count == 0)
                return MsAugmentResult.Fail($"'{contextSnippet}' has an empty body; nothing to extract.");
        }
        else
        {
            int pos;
            try { pos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
            catch (ToolException ex) { return MsAugmentResult.Fail(ex.Message); }

            var SelectionSpan = new TextSpan(pos, contextSnippet.Length);
            var node = root.FindNode(SelectionSpan);

            // Walk up to the nearest block so we can work with statement-level items
            block = node.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
            if (block == null)
            {
                return MsAugmentResult.Fail(
                    "Selection must be inside a method body (no enclosing block found).");
            }

            // Find statements that overlap the Selection
            stmtsInSelection = block.Statements
                .Where(s => SelectionSpan.Contains(s.Span) || SelectionSpan.OverlapsWith(s.Span))
                .ToList();

            if (stmtsInSelection.Count == 0)
            {
                // Fall back to the single statement that contains the Selection
                var single = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                if (single != null && block.Statements.Contains(single))
                {
                    stmtsInSelection.Add(single);
                }
                else
                {
                    return MsAugmentResult.Fail(
                        "No statements found at the contextSnippet location. Try a broader snippet.");
                }
            }

            // A selection that resolves to exactly one statement, inside a loop body that has
            // sibling statements left out of the selection, is a red flag when that statement
            // depends on state declared outside the loop — e.g. an accumulator pattern like
            // `runningTotal += x` inside `foreach { runningTotal += x; totalUnits += y; }`.
            // Extracting just that one statement produces a method called once per iteration
            // that silently drops the sibling statement and the loop itself from the extraction —
            // exactly the kind of "technically did something, semantically wrong" result a caller
            // has no way to detect from a success response. Refuse and point at the ambiguity
            // instead of guessing a narrower scope than the caller likely intended (confirmed
            // regression: ContosoOrders BuildOrderSummary/ComputeTotals).
            if (stmtsInSelection.Count == 1
                && block.Statements.Count > 1
                && block.Parent is ForEachStatementSyntax or ForStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax)
            {
                var single = stmtsInSelection[0];
                var loopLocalNames = new HashSet<string>(
                    block.Parent switch
                    {
                        ForEachStatementSyntax fe => [fe.Identifier.Text],
                        ForStatementSyntax f => f.Declaration?.Variables.Select(v => v.Identifier.Text) ?? [],
                        _ => []
                    });

                var singleDf = model.AnalyzeDataFlow(single);
                bool touchesOuterState = singleDf?.Succeeded == true &&
                    singleDf.DataFlowsIn.Any(s => s.Kind == SymbolKind.Local && !loopLocalNames.Contains(s.Name));

                if (touchesOuterState)
                {
                    var siblingPreview = string.Join(" / ",
                        block.Statements.Where(s => s != single)
                                        .Select(s => s.ToString().Trim())
                                        .Where(t => t.Length > 0)
                                        .Take(3));
                    return MsAugmentResult.Fail(
                        $"contextSnippet matched only one statement inside a loop body that has other " +
                        $"statement(s) ({siblingPreview}) and uses state declared outside the loop. " +
                        "Extracting just this statement would run the new method once per iteration and " +
                        "silently leave the sibling statement(s) behind. If you meant to extract the whole " +
                        "loop (and anything around it), widen contextSnippet to cover that whole span " +
                        "verbatim — e.g. from the first line before the loop through the loop's closing " +
                        "brace. If you really do mean just this one statement, use lineBefore/lineAfter " +
                        "naming its immediate neighbors to confirm the narrower selection.");
                }
            }

            // Same ambiguity, different shape: a single matched statement that sits BEFORE a loop
            // later in the same block (not inside it — possibly with other non-loop statements
            // in between, e.g. another unrelated declaration), where the statement declares/assigns
            // a local that the loop then reads or mutates. Extracting just this statement strands
            // the loop and produces a method whose only job — initializing state the loop depends
            // on — looks complete but silently detaches from the accumulation that follows
            // (confirmed regression: ContosoOrders BuildOrderSummary/ComputeTotals, where
            // `decimal runningTotal = 0m;` alone was matched and extracted, leaving the `foreach`
            // that actually accumulates into `runningTotal` — two statements later in the same
            // block — behind in the caller).
            if (stmtsInSelection.Count == 1
                && block.Statements.Count > 1
                && block.Parent is not (ForEachStatementSyntax or ForStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax))
            {
                var single = stmtsInSelection[0];
                var singleIndex = block.Statements.IndexOf(single);
                var laterLoop = singleIndex >= 0
                    ? block.Statements.Skip(singleIndex + 1)
                        .FirstOrDefault(s => s is ForEachStatementSyntax or ForStatementSyntax
                            or WhileStatementSyntax or DoStatementSyntax)
                    : null;

                if (laterLoop != null)
                {
                    var singleDf = model.AnalyzeDataFlow(single);
                    var declaredOrWrittenBySingle = singleDf?.Succeeded == true
                        ? new HashSet<string>(
                            singleDf.VariablesDeclared.Select(s => s.Name)
                                .Concat(singleDf.WrittenInside.Select(s => s.Name)))
                        : [];

                    var loopDf = model.AnalyzeDataFlow(laterLoop);
                    bool loopConsumesIt = loopDf?.Succeeded == true &&
                        (loopDf.DataFlowsIn.Any(s => declaredOrWrittenBySingle.Contains(s.Name))
                            || loopDf.ReadInside.Any(s => declaredOrWrittenBySingle.Contains(s.Name))
                            || loopDf.WrittenInside.Any(s => declaredOrWrittenBySingle.Contains(s.Name)));

                    if (loopConsumesIt)
                    {
                        return MsAugmentResult.Fail(
                            $"contextSnippet matched only one statement (\"{single.ToString().Trim()}\") " +
                            "in a block that later contains a loop reading or mutating the same variable " +
                            "it declares/assigns. Extracting just this statement would strand the loop, " +
                            "leaving a method that looks complete but no longer connects to the " +
                            "accumulation that follows. If you meant to extract the initialization " +
                            "together with the loop (and anything else around it), widen contextSnippet " +
                            "to cover that whole span verbatim. If you really do mean just this one " +
                            "statement, use lineBefore/lineAfter naming its immediate neighbors to " +
                            "confirm the narrower selection.");
                    }
                }
            }
        }

        var firstStmt = stmtsInSelection.First();
        var lastStmt = stmtsInSelection.Last();

        // Determine the return type of the extracted method
        string returnTypeStr = "void";
        bool returnsValue = false;

        if (lastStmt is ReturnStatementSyntax retStmt && retStmt.Expression != null)
        {
            var typeInfo = model.GetTypeInfo(retStmt.Expression, cancellationToken);
            if (typeInfo.Type != null
             && typeInfo.Type.SpecialType != SpecialType.System_Void
             && typeInfo.Type.TypeKind != TypeKind.Error)
            {
                returnTypeStr = typeInfo.Type.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat);
                returnsValue = true;
            }
        }

        // Find variables that flow into the Selection — these become parameters. Locals that
        // ALSO flow out (mutated inside the Selection, then read afterward) need their final
        // value reported back to the caller too — tracked in outVarNames and handled below.
        var parameters = new List<(string Name, string TypeStr)>();
        var outVarNames = new HashSet<string>();
        // Locals declared FRESH inside the Selection (so they never appear in DataFlowsIn —
        // nothing flows in, there's no pre-existing value) but read afterward. These must also
        // be reported back to the caller, but as an additional return value only — NOT as a
        // parameter, since there's nothing for the caller to pass in.
        var declaredOutVars = new List<(string Name, string TypeStr)>();
        try
        {
            var df = model.AnalyzeDataFlow(firstStmt, lastStmt);
            if (df?.Succeeded == true)
            {
                var flowsOut = new HashSet<ISymbol>(df.DataFlowsOut, SymbolEqualityComparer.Default);
                var flowsIn = new HashSet<ISymbol>(df.DataFlowsIn, SymbolEqualityComparer.Default);

                foreach (var sym in df.DataFlowsIn)
                {
                    if (sym.Kind == SymbolKind.Local && sym is ILocalSymbol local)
                    {
                        parameters.Add((local.Name, DisplayTypeForExtractedSignature(local.Type)));
                        if (flowsOut.Contains(local))
                        {
                            outVarNames.Add(local.Name);
                        }
                    }
                    else if (sym.Kind == SymbolKind.Parameter && sym is IParameterSymbol param && !param.IsThis)
                    {
                        // IsThis excludes the implicit 'this' — DataFlowsIn reports it whenever
                        // the Selection touches an instance member (e.g. a field), but it is
                        // already implicitly available to a non-static extracted method and is
                        // not valid syntax as an explicit parameter.
                        parameters.Add((param.Name, DisplayTypeForExtractedSignature(param.Type)));
                        if (flowsOut.Contains(param))
                        {
                            outVarNames.Add(param.Name);
                        }
                    }
                }

                foreach (var sym in df.DataFlowsOut)
                {
                    if (sym.Kind == SymbolKind.Local && sym is ILocalSymbol local && !flowsIn.Contains(local))
                    {
                        declaredOutVars.Add((local.Name, DisplayTypeForExtractedSignature(local.Type)));
                    }
                }
            }
        }
        catch { /* best-effort — proceed without parameters if analysis fails */ }

        var outVars = parameters.Where(p => outVarNames.Contains(p.Name)).Concat(declaredOutVars).ToList();

        // No explicit return statement in the Selection, but pre-existing locals are mutated
        // and read afterward — return their final value(s) so the caller sees the update.
        // Parameters are passed by value; without this the extraction would silently drop it.
        bool returnsOutVars = false;
        if (!returnsValue && outVars.Count > 0)
        {
            returnsOutVars = true;
            returnTypeStr = outVars.Count == 1
                ? outVars[0].TypeStr
                : $"({string.Join(", ", outVars.Select(v => $"{v.TypeStr} {v.Name}"))})";
        }

        // Preserve static-ness from the containing method
        var containingMethod = block.Ancestors()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault();
        bool isStatic = containingMethod?
            .Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) == true;

        // Determine the containing type BEFORE any tree modifications
        var containingType = block.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (containingType == null)
        {
            return MsAugmentResult.Fail(
                "No containing type found — cannot place extracted method.");
        }

        // Build the extracted method source text
        var sb = new StringBuilder();
        sb.Append("private ");
        if (isStatic)
        {
            sb.Append("static ");
        }

        sb.Append($"{returnTypeStr} {newMethodName}(");
        sb.Append(string.Join(", ", parameters.Select(p => $"{p.TypeStr} {p.Name}")));
        sb.AppendLine(")");
        sb.AppendLine("{");
        foreach (var stmt in stmtsInSelection)
        {
            sb.AppendLine($"    {stmt.WithoutLeadingTrivia().ToFullString().TrimEnd()}");
        }

        if (returnsOutVars)
        {
            var returnExpr = outVars.Count == 1
                ? outVars[0].Name
                : $"({string.Join(", ", outVars.Select(v => v.Name))})";
            sb.AppendLine($"    return {returnExpr};");
        }

        sb.Append('}');

        var newMethodDecl = SyntaxFactory.ParseMemberDeclaration(sb.ToString());
        if (newMethodDecl == null)
        {
            return MsAugmentResult.Fail("Failed to parse the generated method declaration.");
        }

        // Build the call-site replacement statement
        var argsStr = string.Join(", ", parameters.Select(p => p.Name));
        string callText;
        if (returnsValue)
        {
            callText = $"return {newMethodName}({argsStr});";
        }
        else if (returnsOutVars)
        {
            // outVars declared fresh inside the Selection (declaredOutVars) have no counterpart
            // in the caller's scope — the call site must declare them (`var name`), not assign to
            // them. outVars that already existed before the Selection (flowed in AND out) must be
            // assigned, not re-declared. Per-element `var` correctly expresses a mix of both in a
            // single deconstruction (e.g. `(existing, var fresh) = Method();`).
            var declaredOutNames = new HashSet<string>(declaredOutVars.Select(v => v.Name));
            var targetExpr = outVars.Count == 1
                ? (declaredOutNames.Contains(outVars[0].Name) ? $"var {outVars[0].Name}" : outVars[0].Name)
                : $"({string.Join(", ", outVars.Select(v => declaredOutNames.Contains(v.Name) ? $"var {v.Name}" : v.Name))})";
            callText = $"{targetExpr} = {newMethodName}({argsStr});";
        }
        else
        {
            callText = $"{newMethodName}({argsStr});";
        }

        var callStmt = SyntaxFactory.ParseStatement(callText)
            .WithLeadingTrivia(firstStmt.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed));

        // Replace the Selected statements in the block with the call
        var stmtList = block.Statements.ToList();
        int firstIdx = stmtList.IndexOf(firstStmt);
        if (firstIdx < 0)
        {
            return MsAugmentResult.Fail("Could not locate first statement in block (internal error).");
        }

        var newStmts = new List<StatementSyntax>();
        newStmts.AddRange(stmtList.Take(firstIdx));
        newStmts.Add(callStmt);
        newStmts.AddRange(stmtList.Skip(firstIdx + stmtsInSelection.Count));

        var newBlock = block.WithStatements(SyntaxFactory.List(newStmts));
        var newRoot1 = root.ReplaceNode(block, newBlock);

        // Locate the containing type in the modified tree (by name) and add the new method
        var newContainingType = newRoot1.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == containingType.Identifier.Text);
        if (newContainingType == null)
        {
            return MsAugmentResult.Fail("Could not re-locate containing type after transformation.");
        }

        var methodWithTrivia = newMethodDecl
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed, SyntaxFactory.LineFeed))
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.LineFeed))
            .WithAddedByComment("ExtractMethodSafe");

        var updatedType = newContainingType.AddMembers(methodWithTrivia);
        var finalRoot = newRoot1.ReplaceNode(newContainingType, updatedType);

        return MsAugmentResult.Ok(finalRoot.NormalizeWhitespace().ToFullString());
    }

    public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync()
    {
        Solution? currentSolution;
        try { currentSolution = _workspaceManager.CurrentSolution; }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: false,
                HasLoadedSolution: false,
                LoadedSolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: [$"Workspace exception: {ex.Message}"],
                Summary: $"Workspace is NOT operational: {ex.Message}"));
        }

        var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();

        if (currentSolution is null)
        {
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: true,
                HasLoadedSolution: false,
                LoadedSolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: loadErrors,
                Summary: "Workspace is operational. No solution is currently loaded. Call load_solution to load a .sln or .csproj file."));
        }

        var projectCount = currentSolution.ProjectIds.Count;
        var documentCount = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath = currentSolution.FilePath ?? _workspaceManager.SolutionPath;

        return Task.FromResult(new WorkspaceHealthReport(
            IsOperational: true,
            HasLoadedSolution: true,
            LoadedSolutionPath: solutionPath,
            ProjectCount: projectCount,
            DocumentCount: documentCount,
            LoadErrors: loadErrors,
            Summary: $"Workspace operational. {projectCount} project(s) loaded, {documentCount} document(s). " +
                     (loadErrors.Count > 0
                         ? $"{loadErrors.Count} load warning(s) recorded (non-fatal)."
                         : "No load errors.")));
    }
}
