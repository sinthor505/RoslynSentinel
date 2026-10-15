using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Common;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// When a snippet could be ambiguous, provide lineBefore and/or lineAfter for disambiguation.
/// </summary>
public static class ContextHelper
{
    /// <summary>
    /// A snippet match's location AND real length in the source text. The length is not always
    /// <c>contextSnippet.Length</c> -> the whitespace-collapsing fallback paths in
    /// FindAllSnippetMatchesWithLength match content that is equivalent once
    /// whitespace is normalized, but whose raw span in the source can be a different length
    /// (different indentation, extra spaces, etc.). Any caller that removes/replaces a span of the
    /// real source text (as opposed to merely using <c>Start</c> as an entry point into a
    /// Roslyn node/token lookup) MUST use <c>Length</c>, not <c>contextSnippet.Length</c>.
    /// </summary>
    public readonly record struct SnippetMatch(int Start, int Length);

    /// <summary>
    /// Prepends a <c>// Added by <toolName></c> leading-trivia comment to a freshly synthesized
    /// member declaration, on its own line above any trivia the member already carries (e.g. a
    /// blank-line separator). Intended for tools that insert a brand-new member -> a constructor,
    /// method, property, or field -> so the addition is easy to spot in a diff or code review
    /// without cross-referencing which MCP tool call produced it. Not for tools that edit an
    /// existing member in place (e.g. AddSummaryComment, ChangeAccessibility) -> only for genuinely
    /// new members.
    /// </summary>
    public static T WithAddedByComment<T>(this T member, string toolName) where T : MemberDeclarationSyntax
    {
        var comment = SyntaxFactory.Comment($"// Added by {toolName} (expected - used for diagnostics)");
        var newLeadingTrivia = member.GetLeadingTrivia()
            .Insert(0, comment)
            .Insert(1, SyntaxFactory.CarriageReturnLineFeed);
        return (T)member.WithLeadingTrivia(newLeadingTrivia);
    }

    /// <summary>
    /// After FindSnippetPosition returns a position, that position may land on a
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
    /// <returns>XML documentation string with ///, <summary>, <param>, <returns> tags</returns>
    /// <remarks>
    /// Generates standard-level documentation containing:
    /// - <summary> with placeholder description
    /// - <param> tags for each parameter (methods only)
    /// - <returns> tag (for non-void methods)
    /// 
    /// Example output for a method "GetUser(int id, bool active)":
    /// /// <summary>
    /// /// Gets or retrieves the user.
    /// /// </summary>
    /// /// <param name="id">The unique identifier for the user.</param>
    /// /// <param name="active">A value indicating whether to filter by active status.</param>
    /// /// <returns>The requested user object.</returns>
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
