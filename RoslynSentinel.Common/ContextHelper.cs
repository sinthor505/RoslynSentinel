using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// When a snippet could be ambiguous, provide lineBefore and/or lineAfter for disambiguation.
/// </summary>
public static class ContextHelper
{
    /// <summary>
    /// Non-throwing variant that returns every candidate match instead of resolving to one.
    /// Empty list = not found. Single-item list = unambiguous (equivalent to FindSnippetPosition's
    /// success case). Multi-item list = ambiguous; caller decides what to do, including building
    /// its own hint from the candidates.
    /// </summary>
    public static List<int> FindAllSnippetMatches(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            return new List<int>();
        }

        var source = sourceText.ToString();
        var allMatches = new List<int>();
        int idx = 0;
        while ((idx = source.IndexOf(contextSnippet, idx, StringComparison.Ordinal)) >= 0)
        {
            allMatches.Add(idx);
            idx++;
        }

        if (allMatches.Count == 0 && contextSnippet.Contains('\n'))
        {
            // A multi-line snippet failed the exact ordinal search — the likely cause is a
            // line-ending mismatch (e.g. the caller composed the snippet with \n while the file
            // on disk is \r\n, or vice versa), not a genuine content difference. Retry treating
            // any \r\n/\r/\n in the snippet as interchangeable with whatever the source actually
            // uses, without loosening any other whitespace — a caller building a multi-statement
            // selection out of literal source lines should not need to know the file's line-ending
            // convention.
            var pattern = string.Join(@"\r?\n",
                contextSnippet.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
                    .Select(System.Text.RegularExpressions.Regex.Escape));
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, pattern))
            {
                allMatches.Add(m.Index);
            }
        }

        if (allMatches.Count == 0)
        {
            // Fallback: try matching with collapsed whitespace. This handles a single-line
            // snippet whose indentation doesn't match the source (common when an AI caller
            // retypes a line from memory instead of copying it verbatim character-for-character —
            // models reliably reproduce tokens but not incidental indentation).
            var snippetNorm = System.Text.RegularExpressions.Regex.Replace(contextSnippet.Trim(), @"\s+", " ");
            var lines = sourceText.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var lineText = lines[i].ToString();
                var lineNorm = System.Text.RegularExpressions.Regex.Replace(lineText.Trim(), @"\s+", " ");
                var normIndex = lineNorm.IndexOf(snippetNorm, StringComparison.OrdinalIgnoreCase);
                if (normIndex >= 0)
                {
                    // Map the match offset within the whitespace-collapsed line back to the
                    // corresponding offset in the real (pre-collapse) line text, so callers that
                    // need in-line precision (e.g. ExtractLocalVariableAsync locating an
                    // ExpressionSyntax's SpanStart) land on the actual match, not just "somewhere
                    // in the right line" — the line-start position was fine for member/type
                    // resolution (FindNode/AncestorsAndSelf walk up to the enclosing declaration
                    // regardless of exact column) but wrong for expression-level lookups.
                    var realOffset = MapNormalizedOffsetToRaw(lineText, normIndex);
                    allMatches.Add(lines[i].Start + realOffset);
                }
            }
        }

        if (allMatches.Count == 0 && contextSnippet.Contains('\n'))
        {
            // Fallback: same whitespace-collapse tolerance as above, but for a snippet spanning
            // multiple statements/lines. The single-line fallback can never match this shape —
            // it only ever tests one source line against the whole (newline-collapsed) snippet.
            // Instead, slide a window of N consecutive source lines (N = the snippet's own line
            // count), collapse whitespace runs on both sides identically, and compare — this
            // preserves line-by-line structure (so it won't match reordered statements) while
            // being indifferent to indentation depth, which carries no compiler meaning in C#.
            //
            // A caller-supplied snippet very commonly ends (and sometimes starts) with a blank
            // line — e.g. "return foo;\n}\n" from copying a whole statement plus its closing brace
            // with a trailing newline. Split('\n') on that produces a trailing empty-string
            // element, which would otherwise inflate windowSize by one and force the window to
            // swallow one real, unrelated source line that was never meant to be part of the
            // match. Trim blank leading/trailing lines before computing windowSize so the window
            // reflects only the snippet's actual content lines.
            var normalizedSnippet = contextSnippet.Replace("\r\n", "\n").Replace("\r", "\n");
            var snippetLineTexts = normalizedSnippet.Split('\n')
                .SkipWhile(string.IsNullOrWhiteSpace)
                .Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()
                .ToArray();
            if (snippetLineTexts.Length == 0)
            {
                snippetLineTexts = normalizedSnippet.Split('\n');
            }
            var snippetWindowNorm = System.Text.RegularExpressions.Regex.Replace(
                string.Join("\n", snippetLineTexts.Select(l => l.Trim())).Trim(), @"\s+", " ");
            var lines = sourceText.Lines;
            int windowSize = snippetLineTexts.Length;

            for (int i = 0; i + windowSize <= lines.Count; i++)
            {
                var windowText = string.Join(
                    "\n", Enumerable.Range(i, windowSize).Select(j => lines[j].ToString().Trim()));
                var windowNorm = System.Text.RegularExpressions.Regex.Replace(windowText, @"\s+", " ");
                if (windowNorm.Equals(snippetWindowNorm, StringComparison.OrdinalIgnoreCase))
                {
                    allMatches.Add(lines[i].Start);
                }
            }
        }

        if (allMatches.Count == 0)
        {
            return new List<int>();
        }

        // If lineBefore/lineAfter are supplied, filter all matches (including single matches) against them
        if (lineBefore != null || lineAfter != null)
        {
            var lbTrimmed = lineBefore?.Trim();
            var laTrimmed = lineAfter?.Trim();

            var filtered = allMatches.Where(offset =>
            {
                var linePos = sourceText.Lines.GetLinePosition(offset);
                var lineIndex = linePos.Line;

                if (lbTrimmed != null)
                {
                    if (lineIndex == 0)
                    {
                        return false;
                    }

                    var prevLine = sourceText.Lines[lineIndex - 1].ToString().Trim();
                    if (!MatchLine(prevLine, lbTrimmed))
                    {
                        return false;
                    }
                }
                if (laTrimmed != null)
                {
                    if (lineIndex >= sourceText.Lines.Count - 1)
                    {
                        return false;
                    }

                    var nextLine = sourceText.Lines[lineIndex + 1].ToString().Trim();
                    if (!MatchLine(nextLine, laTrimmed))
                    {
                        return false;
                    }
                }
                return true;
            }).ToList();

            return filtered;
        }

        return allMatches;
    }

    /// <summary>
    /// Finds the unique character offset of contextSnippet within sourceText.
    /// Optionally, provide lineBefore/lineAfter (verbatim text from adjacent lines) to disambiguate.
    /// Throws InvalidOperationException if not found or still ambiguous after disambiguation.
    /// </summary>
    public static int FindSnippetPosition(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            throw new InvalidOperationException("contextSnippet must not be empty.");
        }

        var matches = FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"contextSnippet not found: \"{contextSnippet.Trim()}\"");
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        return (lbTrimmed, laTrimmed) switch
        {
            (null, null) => throw new InvalidOperationException(
                $"contextSnippet is ambiguous ({matches.Count} matches): \"{contextSnippet.Trim()}\". " +
                "Provide lineBefore and/or lineAfter (verbatim text from the lines immediately above/below) to disambiguate."),
            _ => throw new InvalidOperationException(
                $"contextSnippet is still ambiguous ({matches.Count} matches remain): \"{contextSnippet.Trim()}\". " +
                "Provide more specific lineBefore and/or lineAfter content.")
        };
    }

    /// <summary>String overload — delegates to SourceText for consistent line handling.</summary>
    public static int FindSnippetPosition(
        string fullSource, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindSnippetPosition(SourceText.From(fullSource), contextSnippet, lineBefore, lineAfter);

    /// <summary>
    /// Non-throwing variant of <see cref="FindSnippetPosition(SourceText, string, string?, string?)"/>.
    /// Returns <c>-1</c> and sets <paramref name="error"/> to the diagnostic message when the snippet
    /// cannot be found or is ambiguous. Use this when the caller wants to fall back or surface the
    /// error as a return value rather than propagate an exception.
    /// </summary>
    public static int TryFindSnippetPosition(
        SourceText sourceText, string contextSnippet, out string? error,
        string? lineBefore = null, string? lineAfter = null)
    {
        try
        {
            error = null;
            return FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    /// <summary>String overload of <see cref="TryFindSnippetPosition(SourceText, string, out string?, string?, string?)"/>.</summary>
    public static int TryFindSnippetPosition(
        string fullSource, string contextSnippet, out string? error,
        string? lineBefore = null, string? lineAfter = null)
        => TryFindSnippetPosition(SourceText.From(fullSource), contextSnippet, out error, lineBefore, lineAfter);

    /// <summary>
    /// Maps a character offset within a whitespace-collapsed, trimmed line (every run of
    /// whitespace replaced by a single space) back to the corresponding offset in the original,
    /// untrimmed line text. Used to recover a precise match position after
    /// <see cref="FindAllSnippetMatches"/>'s whitespace-tolerant fallback locates a snippet inside
    /// the collapsed form.
    /// </summary>
    private static int MapNormalizedOffsetToRaw(string rawLine, int normalizedOffset)
    {
        var leadingWhitespace = rawLine.Length - rawLine.TrimStart().Length;
        int rawIndex = leadingWhitespace;
        int normIndex = 0;

        while (rawIndex < rawLine.Length && normIndex < normalizedOffset)
        {
            if (char.IsWhiteSpace(rawLine[rawIndex]))
            {
                // A whole run of raw whitespace collapses to a single normalized space — consume
                // the entire run in one step so rawIndex lands on the next real character, not
                // partway through the run.
                normIndex++;
                while (rawIndex < rawLine.Length && char.IsWhiteSpace(rawLine[rawIndex]))
                {
                    rawIndex++;
                }
            }
            else
            {
                normIndex++;
                rawIndex++;
            }
        }

        return rawIndex;
    }

    /// <summary>
    /// Checks if a source line contains the pattern. Falls back to a quote-normalized comparison
    /// to handle AI-provided snippets where `\"` wasn't unescaped (e.g., from JSON context).
    /// </summary>
    private static bool MatchLine(string sourceLine, string pattern)
    {
        if (sourceLine.Contains(pattern, StringComparison.Ordinal))
        {
            return true;
        }
        // Normalize JSON escape sequences and retry
        var normalized = pattern
            .Replace("\\\"", "\"")
            .Replace("\\'", "'")
            .Replace("\\\\", "\\");
        return normalized != pattern && sourceLine.Contains(normalized, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prepends a <c>// Added by &lt;toolName&gt;</c> leading-trivia comment to a freshly synthesized
    /// member declaration, on its own line above any trivia the member already carries (e.g. a
    /// blank-line separator). Intended for tools that insert a brand-new member — a constructor,
    /// method, property, or field — so the addition is easy to spot in a diff or code review
    /// without cross-referencing which MCP tool call produced it. Not for tools that edit an
    /// existing member in place (e.g. AddSummaryComment, ChangeAccessibility) — only for genuinely
    /// new members.
    /// </summary>
    public static T WithAddedByComment<T>(this T member, string toolName) where T : MemberDeclarationSyntax
    {
        var comment = SyntaxFactory.Comment($"// Added by {toolName}");
        var newLeadingTrivia = member.GetLeadingTrivia()
            .Insert(0, comment)
            .Insert(1, SyntaxFactory.CarriageReturnLineFeed);
        return (T)member.WithLeadingTrivia(newLeadingTrivia);
    }

    /// <summary>
    /// After <see cref="FindSnippetPosition"/> returns a position, that position may land on a
    /// modifier keyword (e.g., "public") rather than the declared identifier.
    /// This helper scans the snippet span for identifier tokens and returns the position of the
    /// <em>last</em> one (the declared name, not the return type).
    /// Required before calling <c>SymbolFinder.FindSymbolAtPositionAsync</c>, which returns null
    /// when the cursor is on a non-identifier token.
    /// </summary>
    public static int AdvanceToLastIdentifier(SyntaxNode root, int snippetStart, int snippetLength)
    {
        var startToken = root.FindToken(snippetStart);
        if (startToken.IsKind(SyntaxKind.IdentifierToken))
        {
            return snippetStart;
        }

        var snippetEnd = snippetStart + snippetLength;
        var ident = root.DescendantTokens()
            .Where(t => t.SpanStart >= snippetStart && t.SpanStart < snippetEnd &&
                        t.IsKind(SyntaxKind.IdentifierToken))
            .Select(t => (SyntaxToken?)t)
            .LastOrDefault();

        return ident.HasValue ? ident.Value.SpanStart : snippetStart;
    }

    /// <summary>
    /// Finds the SyntaxNode at the position identified by contextSnippet.
    /// </summary>
    public static SyntaxNode FindNodeAtSnippet(
        SyntaxNode root, SourceText text, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        return root.FindNode(new TextSpan(pos, contextSnippet.Length));
    }

    /// <summary>
    /// Gets the ISymbol at the contextSnippet's position.
    /// Walks up ancestors to find the nearest declaration, falling back to reference resolution.
    /// </summary>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root == null || model == null)
        {
            return null;
        }

        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        var node = root.FindNode(new TextSpan(pos, 0));

        return node.AncestorsAndSelf()
                   .Select(n => model.GetDeclaredSymbol(n, cancellationToken))
                   .FirstOrDefault(s => s != null)
               ?? model.GetSymbolInfo(node, cancellationToken).Symbol;
    }

    /// <summary>
    /// C# reserved keywords that cannot be used as variable names (in non-verbatim form).
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while"
    };

    /// <summary>
    /// Generates a unique variable name within the given scope (method body, class, etc.)
    /// that doesn't conflict with existing variables or parameters.
    /// 
    /// Examples:
    /// - If baseName="temp" and "temp" is available, returns "temp"
    /// - If "temp" exists, tries "temp1", "temp2", etc. until finding a free name
    /// - If baseName is a reserved keyword (e.g., "class"), appends "1": "class1"
    /// - Returns deterministic, camelCase-safe names for use in local variable extraction
    /// </summary>
    /// <param name="scope">The syntax node representing the scope (method body, class, block, etc.)</param>
    /// <param name="baseName">Base name to use (e.g., "temp", "value", "result"). Will be converted to camelCase.</param>
    /// <returns>A unique variable name safe to use in the given scope.</returns>
    public static string GetUniqueVariableName(SyntaxNode scope, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("baseName must not be empty", nameof(baseName));
        }

        // Convert to camelCase: first letter lowercase, rest as-is after first char
        var camelCaseName = char.ToLowerInvariant(baseName[0]) + baseName.Substring(1);

        // Collect all identifiers in the scope (variables, parameters, fields, etc.)
        var existingNames = new HashSet<string>(StringComparer.Ordinal);

        // Add all declared variables in this scope
        var descendants = scope.DescendantNodes();

        // Local variables and parameters
        foreach (var varDecl in descendants.OfType<VariableDeclaratorSyntax>())
        {
            if (varDecl.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Parameters (in method declarations, delegates, etc.)
        foreach (var param in descendants.OfType<ParameterSyntax>())
        {
            if (param.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Local functions and type parameters
        foreach (var localFunc in descendants.OfType<LocalFunctionStatementSyntax>())
        {
            if (localFunc.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Check if base name is reserved or already exists
        if (ReservedKeywords.Contains(camelCaseName) || existingNames.Contains(camelCaseName))
        {
            // Try appending numeric suffixes: name1, name2, name3, ...
            for (int i = 1; i <= 10000; i++)
            {
                var candidate = camelCaseName + i;
                if (!existingNames.Contains(candidate) && !ReservedKeywords.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Fallback: this should never happen in practice, but provide a safe default
            return string.Concat(camelCaseName, "_", Guid.NewGuid().ToString("N").AsSpan(0, 8));
        }

        return camelCaseName;
    }

    /// <summary>
    /// Generates standard C# XML documentation comments for a given symbol.
    /// Returns a string containing properly formatted XML doc tags (///).
    /// Handles methods, properties, constructors, indexers, types, and fields.
    /// </summary>
    /// <param name="symbol">The Roslyn symbol to generate documentation for</param>
    /// <returns>XML documentation string with ///, &lt;summary&gt;, &lt;param&gt;, &lt;returns&gt; tags</returns>
    /// <remarks>
    /// Generates standard-level documentation containing:
    /// - &lt;summary&gt; with placeholder description
    /// - &lt;param&gt; tags for each parameter (methods only)
    /// - &lt;returns&gt; tag (for non-void methods)
    /// 
    /// Example output for a method "GetUser(int id, bool active)":
    /// /// &lt;summary&gt;
    /// /// Gets or retrieves the user.
    /// /// &lt;/summary&gt;
    /// /// &lt;param name="id"&gt;The unique identifier for the user.&lt;/param&gt;
    /// /// &lt;param name="active"&gt;A value indicating whether to filter by active status.&lt;/param&gt;
    /// /// &lt;returns&gt;The requested user object.&lt;/returns&gt;
    /// </remarks>
    public static string GenerateXmlDocumentation(ISymbol symbol)
    {
        if (symbol == null)
        {
            throw new ArgumentNullException(nameof(symbol), "Symbol cannot be null");
        }

        var sb = new System.Text.StringBuilder();

        // Generate documentation based on symbol type
        switch (symbol)
        {
            case IMethodSymbol method:
                GenerateMethodDocumentation(sb, method);
                break;

            case IPropertySymbol property:
                GeneratePropertyDocumentation(sb, property);
                break;

            case IFieldSymbol field:
                GenerateFieldDocumentation(sb, field);
                break;

            case ITypeSymbol type:
                GenerateTypeDocumentation(sb, type);
                break;

            case IEventSymbol @event:
                GenerateEventDocumentation(sb, @event);
                break;

            default:
                // Generic fallback for other symbol types
                sb.AppendLine("/// <summary>");
                sb.AppendLine($"/// {GetFriendlySymbolDescription(symbol.Kind)}.");
                sb.AppendLine("/// </summary>");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private static void GenerateMethodDocumentation(System.Text.StringBuilder sb, IMethodSymbol method)
    {
        // Generate summary based on method name and kind
        string summaryText = GenerateMethodSummary(method);

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// {summaryText}");
        sb.AppendLine("/// </summary>");

        // Generate param tags for each parameter
        foreach (var param in method.Parameters)
        {
            string paramDescription = GenerateParameterDescription(param);
            sb.AppendLine($"/// <param name=\"{param.Name}\">{paramDescription}</param>");
        }

        // Generate returns tag if method returns something (not void, not Task)
        if (!method.ReturnsVoid && !IsTaskType(method.ReturnType))
        {
            string returnDescription = GenerateReturnDescription(method);
            sb.AppendLine($"/// <returns>{returnDescription}</returns>");
        }
        else if (method.ReturnsVoid && method.MethodKind == MethodKind.Constructor)
        {
            // Constructors might have special handling
        }
    }

    private static void GeneratePropertyDocumentation(System.Text.StringBuilder sb, IPropertySymbol property)
    {
        sb.AppendLine("/// <summary>");

        if (property.GetMethod != null && property.SetMethod != null)
        {
            sb.AppendLine($"/// Gets or sets the {property.Name} value.");
        }
        else if (property.GetMethod != null)
        {
            sb.AppendLine($"/// Gets the {property.Name} value.");
        }
        else if (property.SetMethod != null)
        {
            sb.AppendLine($"/// Sets the {property.Name} value.");
        }
        else
        {
            sb.AppendLine($"/// Gets or sets the {property.Name}.");
        }

        sb.AppendLine("/// </summary>");

        // Add returns tag describing the return type
        string returnDescription = GetTypeDescription(property.Type);
        sb.AppendLine($"/// <value>{returnDescription}</value>");
    }

    private static void GenerateFieldDocumentation(System.Text.StringBuilder sb, IFieldSymbol field)
    {
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// The {MakeFriendlyName(field.Name)} field.");
        sb.AppendLine("/// </summary>");
    }

    private static void GenerateTypeDocumentation(System.Text.StringBuilder sb, ITypeSymbol type)
    {
        sb.AppendLine("/// <summary>");

        string typeKind = type.TypeKind.ToString().ToLowerInvariant();
        if (type.TypeKind == TypeKind.Class)
        {
            typeKind = "class";
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            typeKind = "structure";
        }
        else if (type.TypeKind == TypeKind.Interface)
        {
            typeKind = "interface";
        }
        else if (type.TypeKind == TypeKind.Enum)
        {
            typeKind = "enumeration";
        }

        sb.AppendLine($"/// {type.Name} {typeKind}.");
        sb.AppendLine("/// </summary>");
    }

    private static void GenerateEventDocumentation(System.Text.StringBuilder sb, IEventSymbol @event)
    {
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Occurs when {MakeFriendlyName(@event.Name)}.");
        sb.AppendLine("/// </summary>");
    }

    private static string GenerateMethodSummary(IMethodSymbol method)
    {
        // Special handling for constructors
        if (method.MethodKind == MethodKind.Constructor)
        {
            return $"Initializes a new instance of the {method.ContainingType?.Name} {method.ContainingType?.TypeKind.ToString().ToLowerInvariant()}.";
        }

        // Parse method name to generate meaningful description
        string methodName = method.Name;
        string action = "";

        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            action = "Gets or retrieves";
        }
        else if (methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
        {
            action = "Sets";
        }
        else if (methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase))
        {
            action = "Adds";
        }
        else if (methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase))
        {
            action = "Removes";
        }
        else if (methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
        {
            action = "Deletes";
        }
        else if (methodName.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
        {
            action = "Creates";
        }
        else if (methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else if (methodName.StartsWith("Has", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else if (methodName.StartsWith("Can", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else
        {
            action = "Performs";
        }

        string subject = MakeFriendlyName(methodName.Substring(action == "Performs" ? 0 : GetPrefixLength(methodName)));
        return $"{action} {subject}.";
    }

    private static string GenerateParameterDescription(IParameterSymbol param)
    {
        string typeName = param.Type.Name;
        string paramName = MakeFriendlyName(param.Name);

        // Generate description based on parameter name and type
        if (param.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("identifier", StringComparison.OrdinalIgnoreCase))
        {
            return "The unique identifier.";
        }

        if (param.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return "The name.";
        }

        if (param.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
        {
            return $"The {typeName.ToLowerInvariant()} value.";
        }

        if (param.Name.Equals("count", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("size", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            return $"The {param.Name.ToLowerInvariant()} of the collection.";
        }

        if (param.Name.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return "The zero-based index.";
        }

        if (param.Type.Name == "Boolean" || param.Type.Name == "bool")
        {
            return $"A value indicating whether to {MakeFriendlyName(param.Name)}.";
        }

        if (param.Type.TypeKind == TypeKind.Enum)
        {
            return $"The {typeName} value.";
        }

        // Generic fallback
        return $"The {paramName} parameter.";
    }

    private static string GenerateReturnDescription(IMethodSymbol method)
    {
        string typeName = method.ReturnType.Name;

        // Special case for Task<T>
        if (IsTaskType(method.ReturnType))
        {
            if (method.ReturnType is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
            {
                return $"A task representing the asynchronous operation.";
            }
            return "A task representing the asynchronous operation.";
        }

        if (typeName == "Boolean" || typeName == "bool")
        {
            return "A value indicating the result of the operation.";
        }

        if (typeName == "String" || typeName == "string")
        {
            return "The resulting string value.";
        }

        if (typeName == "Int32" || typeName == "int")
        {
            return "The numeric result.";
        }

        if (method.ReturnType.TypeKind == TypeKind.Enum)
        {
            return $"The {typeName} value.";
        }

        // Generic fallback
        return $"The {typeName} result.";
    }

    private static string GetTypeDescription(ITypeSymbol type)
    {
        string typeName = type.Name;

        if (typeName == "String" || typeName == "string")
        {
            return "A string value.";
        }

        if (typeName == "Boolean" || typeName == "bool")
        {
            return "A boolean value.";
        }

        if (typeName == "Int32" || typeName == "int")
        {
            return "An integer value.";
        }

        return $"A {type.Name} value.";
    }

    private static bool IsTaskType(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        var name = type.Name;
        return name == "Task" || name == "Task`1" || (type.ToString()?.StartsWith("System.Threading.Tasks.Task") ?? false);
    }

    private static string MakeFriendlyName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        // Convert camelCase/PascalCase to friendly name
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                result.Append(' ');
            }

            result.Append(char.ToLower(c));
        }
        return result.ToString();
    }

    private static int GetPrefixLength(string methodName)
    {
        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Can", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Has", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        return 0;
    }

    private static string GetFriendlySymbolDescription(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Method => "Provides method functionality",
            SymbolKind.Property => "Provides property access",
            SymbolKind.Field => "Provides field data",
            SymbolKind.NamedType => "Provides type definition",
            SymbolKind.Event => "Occurs when a specific condition is met",
            _ => "Provides functionality"
        };
    }
}
