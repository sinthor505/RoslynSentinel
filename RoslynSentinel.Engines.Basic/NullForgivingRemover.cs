using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Plans the <c>removeNullForgiving</c> operation of SemanticFindReplace: deletes the postfix null-forgiving operator
/// (<c>!</c>) from every expression whose operand binds to one property or field whose declared type is not nullable, so
/// the operator is redundant.
/// </summary>
/// <remarks>
/// The edit removes only the <c>!</c> operator token (not its trivia), so nested forms such as <c>x.P!.Y!</c> and
/// <c>a!!</c> never produce overlapping edits and surrounding text and line endings stay untouched. A member whose type is
/// nullable-annotated (or an unconstrained type parameter, or marked [MaybeNull]) is refused: there the <c>!</c> is not redundant.
/// </remarks>
internal static class NullForgivingRemover
{
    private static string SymbolHint => $"Pass a property or field id such as 'P:Ns.Type.Name' or 'F:Ns.Type.name' (from {ToolCallHint.Call("Search", ("mode", "symbol"))} or {ToolCallHint.Call("Search", ("mode", "property"))}); its declared type must not be nullable.";

    /// <summary>Plans the removal. Zero matching sites is a valid outcome (empty Sites and Changes, no Error).</summary>
    /// <param name="solution">Solution to walk.</param>
    /// <param name="symbolDocCommentId">docCommentId of the property or field whose null-forgiving uses are removed.</param>
    /// <param name="projectFilter">Optional project names (comma or semicolon separated); null or empty means all projects.</param>
    /// <param name="fileFilter">Optional glob ('*', '**', '?') on the solution-relative path with '/' separators; a glob without '/' matches the bare file name. Null or empty means all files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sites (Role NullForgiving), a per-file full new text dictionary, and an error for a refused operation.</returns>
    public static async Task<SemanticReplaceOutcome> PlanAsync(Solution solution, string symbolDocCommentId, string? projectFilter, string? fileFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbolDocCommentId))
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol cannot be empty. {SymbolHint}");
        }

        var (targetSymbol, _) = await ResolveAsync(solution, symbolDocCommentId, ct);
        if (targetSymbol is null)
        {
            return Fail(ToolErrorCode.NotFound, $"symbol: no symbol found for docCommentId '{symbolDocCommentId}' in any project of the loaded solution. {SymbolHint}");
        }

        var memberType = targetSymbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };
        if (memberType is null)
        {
            return Fail(ToolErrorCode.InvalidArgument, $"symbol: '{symbolDocCommentId}' resolves to a {targetSymbol.Kind.ToString().ToLowerInvariant()}, not a property or field. {SymbolHint}");
        }

        var refusal = GetRedundancyRefusal(targetSymbol, memberType);
        if (refusal is not null)
        {
            return Fail(ToolErrorCode.TargetIneligible, $"symbol: '{targetSymbol.ToDisplayString()}' {refusal} Nothing was changed. Pass a member whose declared type is non-nullable, or remove the '!' by hand where it really is redundant.");
        }

        var targetId = targetSymbol.OriginalDefinition.GetDocumentationCommentId() ?? symbolDocCommentId;

        var filter = ParseProjectFilter(projectFilter);
        if (filter is not null)
        {
            var projectNames = solution.Projects.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unknown = filter.Where(n => !projectNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
            {
                return Fail(ToolErrorCode.InvalidArgument,
                    $"projectFilter: no project named {string.Join(", ", unknown.Select(n => $"'{n}'"))}. Pass project names separated by commas, for example '{projectNames.FirstOrDefault() ?? "MyProject"}'. Available: {string.Join(", ", projectNames.Take(40))}.");
            }
        }

        Regex? fileRegex = null;
        var matchBareName = false;
        if (!string.IsNullOrWhiteSpace(fileFilter))
        {
            var glob = fileFilter.Trim().Replace('\\', '/');
            matchBareName = !glob.Contains('/');
            try
            {
                fileRegex = BuildGlobRegex(glob);
            }
            catch (ArgumentException)
            {
                return Fail(ToolErrorCode.InvalidArgument, $"fileFilter: '{fileFilter}' is not a usable glob. Use '*', '**' and '?' on the solution-relative path, for example 'RoslynSentinel.Tests.Basic/Foo*.cs' or 'Foo.cs'.");
            }
        }

        var solutionDirectory = string.IsNullOrEmpty(solution.FilePath) ? null : Path.GetDirectoryName(solution.FilePath);

        var sites = new List<SemanticReplaceSite>();
        var editsByFile = new Dictionary<string, List<TextSpan>>(StringComparer.Ordinal);
        var textByFile = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        var seen = new HashSet<(string Path, int Start)>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (filter is not null && !filter.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            // The project cannot reference the member unless it can see it.
            var targetHere = DocumentationCommentId.GetFirstSymbolForDeclarationId(targetId, compilation);
            if (targetHere is null)
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (document.FilePath is null)
                {
                    continue;
                }

                if (fileRegex is not null && !fileRegex.IsMatch(matchBareName ? Path.GetFileName(document.FilePath) : ToSolutionRelativePath(document.FilePath, solutionDirectory)))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(ct);
                if (root is null)
                {
                    continue;
                }

                var suppressions = root.DescendantNodes()
                    .OfType<PostfixUnaryExpressionSyntax>()
                    .Where(n => n.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                    .ToList();
                if (suppressions.Count == 0)
                {
                    continue;
                }

                var model = await document.GetSemanticModelAsync(ct);
                if (model is null)
                {
                    continue;
                }

                var text = await document.GetTextAsync(ct);
                foreach (var node in suppressions)
                {
                    var bound = GetBoundMember(model, node.Operand, ct);
                    if (bound is null || !SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, targetHere.OriginalDefinition))
                    {
                        continue;
                    }

                    var operatorSpan = node.OperatorToken.Span;
                    if (!seen.Add((document.FilePath, operatorSpan.Start)))
                    {
                        continue;
                    }

                    var line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
                    var before = node.ToString();
                    var after = before.Remove(operatorSpan.Start - node.SpanStart, operatorSpan.Length);
                    sites.Add(new SemanticReplaceSite(document.FilePath, line, SemanticReplaceRole.NullForgiving, before, after, null));
                    if (!editsByFile.TryGetValue(document.FilePath, out var fileEdits))
                    {
                        fileEdits = [];
                        editsByFile[document.FilePath] = fileEdits;
                        textByFile[document.FilePath] = text;
                    }

                    fileEdits.Add(operatorSpan);
                }
            }
        }

        var changes = new Dictionary<FilePathWrapper, string>();
        foreach (var (path, fileEdits) in editsByFile)
        {
            var result = textByFile[path].ToString();
            foreach (var span in fileEdits.OrderByDescending(s => s.Start))
            {
                result = result.Remove(span.Start, span.Length);
            }

            changes[path] = result;
        }

        sites = sites.OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.Line).ToList();
        return new SemanticReplaceOutcome(sites, changes, null);
    }

    private static SemanticReplaceOutcome Fail(string code, string message) =>
    new(new List<SemanticReplaceSite>(), new Dictionary<FilePathWrapper, string>(), new ResultError(code, $"SemanticFindReplace removeNullForgiving: {message}"));

    // TODO ParseProjectFilter and ResolveAsync duplicate the private helpers in ConstructionReplacer; share one copy.
    private static async Task<(ISymbol? Symbol, Compilation? Compilation)> ResolveAsync(Solution solution, string docCommentId, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docCommentId, compilation);
            if (symbol is not null)
            {
                return (symbol, compilation);
            }
        }

        return (null, null);
    }

    private static List<string>? ParseProjectFilter(string? projectFilter)
    {
        if (string.IsNullOrWhiteSpace(projectFilter))
        {
            return null;
        }

        var names = projectFilter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return names.Count == 0 ? null : names;
    }

    /// <summary>Translates a glob ('*' within a segment, '**' across segments, '?' one non-separator character) into an anchored, case-insensitive regex.</summary>
    private static Regex BuildGlobRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        // '**/' also matches zero directories.
                        i++;
                        pattern.Append("(?:.*/)?");
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ToSolutionRelativePath(string filePath, string? solutionDirectory)
    {
        if (solutionDirectory is not null)
        {
            var relative = Path.GetRelativePath(solutionDirectory, filePath);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }

        return filePath.Replace('\\', '/');
    }

    /// <summary>
    /// Binds the member an operand of '!' ends in: strips parentheses and nested '!', follows a conditional access to its
    /// WhenNotNull part, and binds the name of an identifier, member access or member binding (<c>x?.P</c>).
    /// </summary>
    private static ISymbol? GetBoundMember(SemanticModel model, ExpressionSyntax operand, CancellationToken ct)
    {
        var current = operand;
        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    break;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    current = postfix.Operand;
                    break;
                case ConditionalAccessExpressionSyntax conditional:
                    current = conditional.WhenNotNull;
                    break;
                case IdentifierNameSyntax identifier:
                    return model.GetSymbolInfo(identifier, ct).Symbol;
                case MemberAccessExpressionSyntax memberAccess:
                    return model.GetSymbolInfo(memberAccess.Name, ct).Symbol;
                case MemberBindingExpressionSyntax memberBinding:
                    return model.GetSymbolInfo(memberBinding.Name, ct).Symbol;
                default:
                    return null;
            }
        }
    }

    /// <summary>Returns why the operator would not be redundant on this member, or null when its type is non-nullable.</summary>
    private static string? GetRedundancyRefusal(ISymbol member, ITypeSymbol type)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return $"has the nullable type '{type.ToDisplayString()}', so a '!' on it is not redundant.";
        }

        if (type is ITypeParameterSymbol typeParameter
            && !typeParameter.HasNotNullConstraint
            && !typeParameter.HasValueTypeConstraint
            && !(typeParameter.HasReferenceTypeConstraint && typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.NotAnnotated))
        {
            return $"has the type parameter type '{type.ToDisplayString()}', which is not constrained to be non-nullable, so a '!' on it is not redundant.";
        }

        if (member.GetAttributes().Any(a => a.AttributeClass?.Name is "MaybeNullAttribute" or "MaybeNullWhenAttribute"))
        {
            return "is marked [MaybeNull], so a '!' on it is not redundant.";
        }

        return null;
    }
}
