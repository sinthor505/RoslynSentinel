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
    /// Position a match is attributed to for the withinMember/matchLine selectors: its first
    /// non-whitespace character, so a match that begins with a newline or indentation is placed on
    /// the line (and in the member) a human sees it on.
    /// </summary>
    private static int FirstNonWhitespacePosition(SourceText sourceText, SnippetMatch match)
    {
        var last = match.Start + match.Length - 1;
        var position = match.Start;
        while (position < last && char.IsWhiteSpace(sourceText[position]))
        {
            position++;
        }

        return position;
    }

    /// <summary>
    /// Keeps only matches whose first non-whitespace line equals <paramref name="matchLine"/> (1-based)
    /// and/or whose enclosing member name equals <paramref name="withinMember"/> (ordinal). Both
    /// together are an intersection. The caller guarantees <paramref name="memberNameAt"/> is set
    /// whenever <paramref name="withinMember"/> is.
    /// </summary>
    private static List<SnippetMatch> FilterBySelectors(
        SourceText sourceText, List<SnippetMatch> matches, string? withinMember, int? matchLine,
        Func<int, string?>? memberNameAt)
    {
        return matches.Where(match =>
        {
            var position = FirstNonWhitespacePosition(sourceText, match);
            if (matchLine != null && sourceText.Lines.GetLinePosition(position).Line + 1 != matchLine)
            {
                return false;
            }

            return withinMember == null
                || string.Equals(memberNameAt!(position), withinMember, StringComparison.Ordinal);
        }).ToList();
    }

    /// <summary>
    /// The text matched, but no match satisfied withinMember/matchLine. Lists the current matches
    /// (re-anchored on their first non-whitespace character, the same position the selectors use)
    /// so a stale matchLine or member name can be corrected from the message alone.
    /// </summary>
    private static ToolException BuildSelectorMismatch(
        SourceText sourceText, string contextSnippet, List<SnippetMatch> textMatches,
        string? withinMember, int? matchLine, Func<int, string?>? memberNameAt, string label)
    {
        var supplied = new List<string>();
        if (matchLine != null)
        {
            supplied.Add($"matchLine={matchLine}");
        }
        if (withinMember != null)
        {
            supplied.Add($"withinMember=\"{withinMember}\"");
        }

        var listing = DescribeAmbiguousCandidates(
            sourceText, textMatches, label, memberNameAt, ContextErrorBuilder.MaxStillAmbiguousCandidatesListed,
            selectorsSupported: true);

        var diagnosis =
            $"{label} matches {textMatches.Count} place(s) exactly, but none satisfied the selector(s) you supplied ({string.Join(", ", supplied)}). " +
            "Current matches:\n" + listing + "\n" +
            "matchLine is the 1-based line of the match's first non-whitespace character and withinMember is the name of its enclosing member. Fix or drop withinMember/matchLine.";
        return ContextErrorBuilder.Build(
            SnippetMatchOutcome.SelectorMismatch, contextSnippet, sourceText,
            diagnosis: diagnosis, snippetLabel: label);
    }

    /// <summary>
    /// The text matched, but the lineBefore/lineAfter supplied filtered out every match. Keeps the
    /// original "N place(s) ... filtered out all of them" body and lists the current matches with
    /// copyable matchLine/withinMember values so the caller can switch to a selector.
    /// </summary>
    private static ToolException BuildAdjacentLineMismatch(
        SourceText sourceText, string contextSnippet, List<SnippetMatch> textMatches,
        bool afterSelectors, Func<int, string?>? memberNameAt, string label)
    {
        var listing = DescribeAmbiguousCandidates(
            sourceText, textMatches, label, memberNameAt, ContextErrorBuilder.MaxStillAmbiguousCandidatesListed,
            selectorsSupported: true);
        var afterSelectorsText = afterSelectors ? " (after the withinMember/matchLine selector)" : string.Empty;
        var diagnosis =
            $"{label} matches {textMatches.Count} place(s) exactly{afterSelectorsText}, but the lineBefore/lineAfter you supplied filtered out all of them. " +
            "lineBefore must equal the line before the match's first line and lineAfter the line after the match's last line (compared trimmed). " +
            "Fix or drop lineBefore/lineAfter, or pass matchLine (and withinMember where listed) copied from the current matches:\n" + listing;
        return ContextErrorBuilder.Build(
            SnippetMatchOutcome.AdjacentLineMismatch, contextSnippet, sourceText,
            diagnosis: diagnosis, snippetLabel: label);
    }

    /// <summary>
    /// Splits a (possibly CRLF/CR/LF-mixed) multi-line snippet into its own content lines, with
    /// leading/trailing blank lines trimmed off. Shared by the multi-line window fallback in
    /// FindAllSnippetMatchesWithLength and DiagnoseNoMatch -> a
    /// caller-supplied snippet very commonly starts/ends with a blank line (e.g. copying a whole
    /// statement plus its closing brace with a trailing newline), which would otherwise inflate
    /// line counts with content that was never meant to be matched.
    /// </summary>
    internal static string[] SplitSnippetIntoTrimmedContentLines(string contextSnippet)
    {
        var normalized = contextSnippet.Replace("\r\n", "\n").Replace("\r", "\n");
        var lineTexts = normalized.Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()
            .ToArray();
        return lineTexts.Length == 0 ? normalized.Split('\n') : lineTexts;
    }

    /// <summary>
    /// Cheap nearest-neighbor heuristic for DiagnoseNoMatch: shared leading-character
    /// count minus a length-difference penalty. Deliberately NOT full Levenshtein distance (see the
    /// plan's "Rejected: full sliding-window Levenshtein" section) -> this is O(min(len)) per
    /// candidate line rather than O(len1 x len2), which matters when it runs once per filtered
    /// snippet line against every source line in the file. Higher is more similar; can be negative.
    /// </summary>
    private static int LineSimilarityScore(string trimmedSnippetLine, string trimmedSourceLine)
    {
        int sharedPrefix = 0;
        int maxPrefix = Math.Min(trimmedSnippetLine.Length, trimmedSourceLine.Length);
        while (sharedPrefix < maxPrefix && trimmedSnippetLine[sharedPrefix] == trimmedSourceLine[sharedPrefix])
        {
            sharedPrefix++;
        }
        int lengthDiff = Math.Abs(trimmedSnippetLine.Length - trimmedSourceLine.Length);
        return sharedPrefix - lengthDiff;
    }

    private const int MaxHitsPerSnippetLine = 5;

    private const int MaxAllMatchedDiagnosisChars = 2000;

    private const int BoilerplateHitThreshold = 8;

    /// <summary>
    /// Character-level pointer to where a snippet line first diverges from its closest source
    /// line, so a one-character slip is visible without comparing two long strings by eye.
    /// </summary>
    private static string DescribeFirstDifference(string yours, string file)
    {
        int k = 0;
        int max = Math.Min(yours.Length, file.Length);
        while (k < max && yours[k] == file[k])
        {
            k++;
        }

        static string Excerpt(string s, int from) => from >= s.Length
            ? "(end of line)"
            : "\"" + (s.Length - from > 20 ? s.Substring(from, 20) + "..." : s.Substring(from)) + "\"";

        return $"first difference at char {k + 1} (1-based): yours {Excerpt(yours, k)} vs file {Excerpt(file, k)}";
    }

    /// <summary>
    /// Collapses runs of whitespace to a single space, for the whitespace-only-hit fallback in
    /// GatherNoMatchEvidence. Case is left untouched - the fallback comparison stays
    /// Ordinal, not OrdinalIgnoreCase, so a case-only difference is never folded into this bucket.
    /// </summary>
    private static string CollapseWhitespace(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    /// <summary>
    /// Describes each candidate's real, verbatim adjacent line(s) so an ambiguous-match error can
    /// let the caller copy one back as lineBefore/lineAfter instead of constructing one from
    /// memory. Also flags true ties (identical before AND after) since those can't be
    /// disambiguated this way at all. When <paramref name="memberNameAt"/> returns a name for a
    /// match start offset, the match line also names its enclosing member. At most
    /// <paramref name="maxListed"/> matches are listed, followed by an "... and N more" line.
    /// When <paramref name="selectorsSupported"/> is set (only the exact ReplaceSnippet matcher, which
    /// has withinMember/matchLine), each match is attributed to its first non-whitespace character (the
    /// position the selectors compare against) and also lists a ready-to-copy matchLine=N plus
    /// withinMember="X" when X is the enclosing member of exactly one of <paramref name="allMatches"/>;
    /// the tie advice then points at those selectors. Left false, the output is unchanged.
    /// </summary>
    internal static string DescribeAmbiguousCandidates(
        SourceText sourceText, List<SnippetMatch> allMatches, string snippetLabel = "contextSnippet",
        Func<int, string?>? memberNameAt = null, int maxListed = int.MaxValue, bool selectorsSupported = false)
    {
        int AnchorOf(SnippetMatch match) => selectorsSupported ? FirstNonWhitespacePosition(sourceText, match) : match.Start;
        string? MemberOf(SnippetMatch match) => memberNameAt?.Invoke(AnchorOf(match));

        // A member name only separates a match when no other match is in a member of that name.
        var uniqueMembers = selectorsSupported && memberNameAt != null
            ? allMatches.Select(MemberOf).Where(name => !string.IsNullOrEmpty(name))
                .GroupBy(name => name!, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var descriptions = new List<(string Before, string After)>();
        var lines = new List<string>();
        var listedCount = Math.Min(allMatches.Count, maxListed);
        for (int i = 0; i < listedCount; i++)
        {
            var firstLineIndex = sourceText.Lines.GetLinePosition(allMatches[i].Start).Line;
            var lastLineIndex = sourceText.Lines.GetLinePosition(allMatches[i].Start + allMatches[i].Length - 1).Line;
            var before = firstLineIndex > 0 ? sourceText.Lines[firstLineIndex - 1].ToString().Trim() : "(start of file)";
            var after = lastLineIndex < sourceText.Lines.Count - 1
                ? sourceText.Lines[lastLineIndex + 1].ToString().Trim()
                : "(end of file)";
            descriptions.Add((before, after));
            var memberName = MemberOf(allMatches[i]);
            var inMember = string.IsNullOrEmpty(memberName) ? string.Empty : $", in {memberName}";
            var startLineIndex = selectorsSupported ? sourceText.Lines.GetLinePosition(AnchorOf(allMatches[i])).Line : firstLineIndex;
            var lineSpan = selectorsSupported && startLineIndex == lastLineIndex
                ? $"line {startLineIndex + 1}"
                : $"lines {startLineIndex + 1}-{lastLineIndex + 1}";
            var selectors = string.Empty;
            if (selectorsSupported)
            {
                var member = !string.IsNullOrEmpty(memberName) && uniqueMembers.Contains(memberName) ? $"withinMember=\"{memberName}\" " : string.Empty;
                selectors = $"{member}matchLine={startLineIndex + 1} ";
            }

            lines.Add($"Match {i + 1} ({lineSpan}{inMember}): {selectors}lineBefore=\"{before}\" lineAfter=\"{after}\"");
        }

        if (listedCount < allMatches.Count)
        {
            lines.Add($"... and {allMatches.Count - listedCount} more");
        }

        var tieGroups = descriptions.Select((d, i) => (d, i)).GroupBy(x => x.d).Where(g => g.Count() > 1);
        foreach (var tie in tieGroups)
        {
            var matchNumbers = string.Join(" and ", tie.Select(x => x.i + 1));
            lines.Add(selectorsSupported
                ? $"Matches {matchNumbers} have identical surrounding lines, so lineBefore/lineAfter cannot tell them apart. " +
                  $"Pass matchLine (and withinMember where listed) copied from the list above, or widen {snippetLabel}."
                : $"Matches {matchNumbers} have identical surrounding lines and cannot be " +
                  $"disambiguated by lineBefore/lineAfter alone; use a different tool or a longer {snippetLabel}.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// lineBefore/lineAfter disambiguate a match against exactly one real adjacent source line ->
    /// a value containing an embedded newline can never satisfy that comparison and would
    /// otherwise silently eliminate every candidate, surfacing as a misleading "not found"
    /// instead of naming the actual problem.
    /// </summary>
    private static void ThrowIfMultiLine(SourceText sourceText, string parameterName, string? value, bool isAfter)
    {
        if (value != null && value.Contains('\n'))
        {
            throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.InvalidDisambiguator, contextSnippet: value, sourceText,
                parameterName: parameterName, invalidValue: value, isAfter: isAfter);
        }
    }

    /// <summary>
    /// Shared lineBefore/lineAfter filter for FindAllSnippetMatchesWithLength and
    /// FindAllExactSnippetMatches -> previously duplicated between the two.
    /// </summary>
    private static List<SnippetMatch> FilterByAdjacentLines(
        SourceText sourceText, List<SnippetMatch> allMatches, string? lineBefore, string? lineAfter)
    {
        if (lineBefore == null && lineAfter == null)
        {
            return allMatches;
        }

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        return allMatches.Where(match => FilterByAdjacentLinesPredicate(
            sourceText, match, lbTrimmed, laTrimmed)).ToList();
    }

    private static bool FilterByAdjacentLinesPredicate(
        SourceText sourceText, SnippetMatch match, string? lbTrimmed, string? laTrimmed)
    {
        var firstLineIndex = sourceText.Lines.GetLinePosition(match.Start).Line;
        var lastLineIndex = sourceText.Lines.GetLinePosition(match.Start + match.Length - 1).Line;

        if (lbTrimmed != null)
        {
            if (firstLineIndex == 0)
            {
                return false;
            }

            var prevLine = sourceText.Lines[firstLineIndex - 1].ToString().Trim();
            if (!MatchLine(prevLine, lbTrimmed))
            {
                return false;
            }
        }
        if (laTrimmed != null)
        {
            if (lastLineIndex >= sourceText.Lines.Count - 1)
            {
                return false;
            }

            var nextLine = sourceText.Lines[lastLineIndex + 1].ToString().Trim();
            if (!MatchLine(nextLine, laTrimmed))
            {
                return false;
            }
        }
        return true;
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
