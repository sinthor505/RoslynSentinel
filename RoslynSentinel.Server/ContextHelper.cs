using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Server;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// When a snippet could be ambiguous, provide lineBefore and/or lineAfter for disambiguation.
/// </summary>
public static class ContextHelper
{
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

        var source = sourceText.ToString();
        var allMatches = new List<int>();
        int idx = 0;
        while ((idx = source.IndexOf(contextSnippet, idx, StringComparison.Ordinal)) >= 0)
        {
            allMatches.Add(idx);
            idx++;
        }

        if (allMatches.Count == 0)
        {
            // Fallback: try matching with collapsed whitespace
            var snippetNorm = System.Text.RegularExpressions.Regex.Replace(contextSnippet.Trim(), @"\s+", " ");
            var lines = sourceText.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var lineNorm = System.Text.RegularExpressions.Regex.Replace(lines[i].ToString().Trim(), @"\s+", " ");
                if (lineNorm.Contains(snippetNorm, StringComparison.OrdinalIgnoreCase))
                {
                    allMatches.Add(lines[i].Start);
                }
            }
        }

        if (allMatches.Count == 0)
        {
            throw new InvalidOperationException($"contextSnippet not found: \"{contextSnippet.Trim()}\"");
        }

        if (allMatches.Count == 1)
        {
            return allMatches[0];
        }

        // Multiple matches — try to disambiguate with surrounding lines
        if (lineBefore == null && lineAfter == null)
        {
            throw new InvalidOperationException(
                $"contextSnippet is ambiguous ({allMatches.Count} matches): \"{contextSnippet.Trim()}\". " +
                "Provide lineBefore and/or lineAfter (verbatim text from the lines immediately above/below) to disambiguate.");
        }

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

        return filtered.Count switch
        {
            0 => throw new InvalidOperationException(
                $"contextSnippet \"{contextSnippet.Trim()}\" found {allMatches.Count} time(s) " +
                $"but none match the provided context. " +
                (lbTrimmed != null ? $"lineBefore=\"{lbTrimmed}\" " : "") +
                (laTrimmed != null ? $"lineAfter=\"{laTrimmed}\"" : "") +
                " — check that the surrounding lines are exact."),
            1 => filtered[0],
            _ => throw new InvalidOperationException(
                $"contextSnippet is still ambiguous ({filtered.Count} of {allMatches.Count} matches remain): \"{contextSnippet.Trim()}\". " +
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
        CancellationToken ct = default)
    {
        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (root == null || model == null)
        {
            return null;
        }

        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        var node = root.FindNode(new TextSpan(pos, 0));

        return node.AncestorsAndSelf()
                   .Select(n => model.GetDeclaredSymbol(n, ct))
                   .FirstOrDefault(s => s != null)
               ?? model.GetSymbolInfo(node, ct).Symbol;
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
