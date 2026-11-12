using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The one place MoveMember (and its dry-run preview) turns the caller's memberNames into class member declarations,
/// so the real move and the preview cannot disagree.
///
/// Each memberNames entry is either a BARE NAME or a SIGNATURE. An entry is a signature exactly when it contains '('.
///  - A bare name matches by identifier: a method (every overload), a property, a field variable or a nested type.
///    A bare name that matches more than one method with different signatures is refused as ambiguous
///    (ToolAmbiguousMatchException), because it would silently move the whole overload group.
///  - A signature selects one method: <c>Save(int)</c>. Listing every overload's signature moves the whole group.
///
/// Signature rule (see <see cref="SignatureOf"/>): the method identifier, its type-parameter list when it has one
/// (<c>Save&lt;T&gt;(T)</c>), then the parameters in order, each as its modifiers in source order (ref, out, in, params,
/// this) followed by its type text, with parameter names and default values omitted. Types compare AS WRITTEN: nullable
/// annotations (<c>string?</c>), qualified versus short names (<c>System.Int32</c> versus <c>int</c>) and generic
/// arguments are not resolved, so <c>Save(string?)</c> does not select <c>Save(string)</c>. ref, out, in and params are part
/// of the match. Whitespace is normalized the same way for the caller's text and the declaration text before an ordinal
/// comparison: runs collapse to one space and whitespace next to any of <c>&lt; &gt; , [ ] ? . ( )</c> is removed, and
/// parameters print joined by ", ". <c>Save()</c> selects the parameterless overload.
///
/// The printed form (in the ambiguity message and the "found" list of the not-found message) round-trips: passing
/// exactly a printed signature selects exactly that method.
///
/// Partial methods: a definition part and its implementation part print the same signature and are selected together.
/// </summary>
public static class MemberSelection
{
    private const string TightCharacters = "<>,[]?.()";

    /// <summary>True when the memberNames entry is a signature (contains '('), false when it is a bare name.</summary>
    public static bool IsSignatureEntry(string entry) => entry.Contains('(');

    /// <summary>
    /// Collapses whitespace runs to one space and removes whitespace adjacent to any of <c>&lt; &gt; , [ ] ? . ( )</c>;
    /// the result is trimmed. Idempotent. Applied to both the caller's text and declaration text before comparing.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && !TightCharacters.Contains(c) && !TightCharacters.Contains(builder[builder.Length - 1]))
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The canonical printed signature of a method: identifier, type-parameter list, "(", parameters joined by ", ", ")".
    /// Each parameter is its modifiers in source order and its type text as written; names and defaults are omitted.
    /// </summary>
    public static string SignatureOf(MethodDeclarationSyntax method)
    {
        var parameters = new List<string>();
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var modifiers = string.Join(" ", parameter.Modifiers.Select(m => m.Text));
            var type = parameter.Type?.ToString() ?? string.Empty;
            parameters.Add(Normalize(modifiers.Length == 0 ? type : modifiers + " " + type));
        }

        var name = Normalize(method.Identifier.Text + (method.TypeParameterList?.ToString() ?? string.Empty));
        return name + "(" + string.Join(", ", parameters) + ")";
    }

    /// <summary>
    /// Normalizes a caller-supplied signature entry into the printed form: the text before the first '(' is the name
    /// (with type parameters), the text between the first '(' and the last ')' is split on top-level commas (tracking
    /// &lt;&gt;, () and [] depth) and each piece normalized, then re-joined with ", ". Malformed text (no closing ')' or
    /// trailing text after it) is returned normalized as-is and never equals a printed signature.
    /// </summary>
    public static string NormalizeSignatureEntry(string entry)
    {
        var open = entry.IndexOf('(');
        var close = entry.LastIndexOf(')');
        if (open < 0 || close < open || !string.IsNullOrWhiteSpace(entry.Substring(close + 1)))
        {
            return Normalize(entry);
        }

        var inner = entry.Substring(open + 1, close - open - 1);
        var pieces = string.IsNullOrWhiteSpace(inner) ? new List<string>() : SplitTopLevel(inner).Select(Normalize).ToList();
        return Normalize(entry.Substring(0, open)) + "(" + string.Join(", ", pieces) + ")";
    }

    private static List<string> SplitTopLevel(string text)
    {
        var pieces = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                pieces.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        pieces.Add(text.Substring(start));
        return pieces;
    }

    /// <summary>
    /// The bare names one class member declares: a method, property or nested type by identifier, a field by each of its
    /// variables. Other member kinds declare none for selection purposes.
    /// </summary>
    public static IEnumerable<string> DeclaredNames(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => new[] { method.Identifier.Text },
        PropertyDeclarationSyntax property => new[] { property.Identifier.Text },
        FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text),
        BaseTypeDeclarationSyntax nestedType => new[] { nestedType.Identifier.Text },
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// What a caller can pass in memberNames for these class members, in declaration order and without repeats: each method
    /// as its printed signature (copyable as a signature entry), every other selectable member as its bare name.
    /// </summary>
    public static IEnumerable<string> DescribeAvailable(IEnumerable<MemberDeclarationSyntax> classMembers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in classMembers)
        {
            if (member is MethodDeclarationSyntax method)
            {
                var signature = SignatureOf(method);
                if (seen.Add(signature))
                {
                    yield return signature;
                }

                continue;
            }

            foreach (var name in DeclaredNames(member))
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>
    /// The bare-name entries (no '(') in memberNames that match more than one method of different signature, each with
    /// those methods' printed signatures in declaration order. A definition and implementation part of one partial method
    /// share a signature and count once. Empty when no requested bare name is an overloaded method; ExtractMembers uses the
    /// same answer to refuse an overloaded bare name.
    /// </summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Signatures)> FindOverloadedNames(IEnumerable<MemberDeclarationSyntax> classMembers, string[] memberNames)
    {
        var members = classMembers.ToList();
        var overloaded = new List<(string Name, IReadOnlyList<string> Signatures)>();
        foreach (var name in memberNames.Where(n => !IsSignatureEntry(n)).Distinct(StringComparer.Ordinal))
        {
            var signatures = new List<string>();
            foreach (var member in members)
            {
                if (member is MethodDeclarationSyntax method && string.Equals(method.Identifier.Text, name, StringComparison.Ordinal))
                {
                    var signature = SignatureOf(method);
                    if (!signatures.Contains(signature, StringComparer.Ordinal))
                    {
                        signatures.Add(signature);
                    }
                }
            }

            if (signatures.Count > 1)
            {
                overloaded.Add((name, signatures));
            }
        }

        return overloaded;
    }

    // A signature entry (signature != null) selects methods whose printed signature equals it; a bare name selects by identifier.
    private static bool Matches(MemberDeclarationSyntax member, string entry, string? signature)
    {
        if (signature != null)
        {
            return member is MethodDeclarationSyntax method && string.Equals(SignatureOf(method), signature, StringComparison.Ordinal);
        }

        foreach (var name in DeclaredNames(member))
        {
            if (string.Equals(name, entry, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The error wording follows the calling tool: toolName is the tool the retry hint names, verb is its action ("move"/"extract").
    private static ToolAmbiguousMatchException BuildAmbiguous(IReadOnlyList<(string Name, IReadOnlyList<string> Signatures)> overloaded, string[] memberNames, string className, string toolName, string verb)
    {
        var descriptions = overloaded.Select(o => $"Method '{o.Name}' has {o.Signatures.Count} overloads in class '{className}': {string.Join("; ", o.Signatures)}.");
        var firstSignatureByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, signatures) in overloaded)
        {
            firstSignatureByName[name] = signatures[0];
        }

        // The retry names the first overload of each ambiguous name and keeps every other requested entry as given.
        var retryNames = memberNames.Select(n => firstSignatureByName.TryGetValue(n, out var first) ? first : n).Distinct(StringComparer.Ordinal);
        var retryArray = QuoteArray(retryNames);
        var retry = ToolCallHint.Next(toolName, string.Empty, ("memberNames", retryArray));
        var example = firstSignatureByName[overloaded[0].Name];
        return new ToolAmbiguousMatchException($"{string.Join(" ", descriptions)} A bare name selects every overload, so it is refused and nothing was {PastTense(verb)}. To {verb} one overload pass its signature in memberNames, for example \"{example}\"; listing every signature {verb}s the whole group. {retry}");
    }

    private static string PastTense(string verb) => verb.EndsWith('e') ? verb + "d" : verb + "ed";

    // Quotes each name as a string literal and joins them into a bracketed array for a retry hint.
    private static string QuoteArray(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Select(n => "\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    // The name part of a member entry or printed signature: the text before '(' and before any type-parameter list.
    private static string NameOf(string text)
    {
        var end = text.IndexOfAny(new[] { '(', '<' });
        return (end < 0 ? text : text.Substring(0, end)).Trim();
    }

    // Available members whose name is similar to a missing entry come first (nearest first); the rest keep their order.
    private static List<string> SimilarFirst(IEnumerable<string> available, IReadOnlyList<string> missing)
    {
        var list = available.ToList();
        var similar = missing
            .SelectMany(entry => NameCandidates.Rank(NameOf(entry), list, item => NameOf(item), int.MaxValue))
            .GroupBy(r => r.Item, StringComparer.Ordinal)
            .OrderBy(g => g.Min(r => r.Distance))
            .Select(g => g.Key)
            .ToList();
        return similar.Concat(list.Where(item => !similar.Contains(item, StringComparer.Ordinal))).ToList();
    }

    // Not-found guidance: a 'Did you mean' per missing entry plus one prefilled retry; with no similar name for any
    // missing entry, the no-match fact (and where the member does not exist) instead.
    private static string BuildNotFoundHints(string[] memberNames, IReadOnlyList<string> missing, List<MemberDeclarationSyntax> members, string className, string toolName)
    {
        var names = members.SelectMany(m => DeclaredNames(m)).Distinct(StringComparer.Ordinal).ToList();
        var topByEntry = new Dictionary<string, string>(StringComparer.Ordinal);
        var didYouMean = new StringBuilder();
        foreach (var entry in missing)
        {
            var ranked = NameCandidates.Rank(NameOf(entry), names, name => name);
            if (ranked.Count > 0)
            {
                topByEntry[entry] = ranked[0].Item;
                didYouMean.Append($" Did you mean '{ranked[0].Item}' for '{entry}'?");
            }
        }

        if (topByEntry.Count == 0)
        {
            return $" No member of class '{className}' has a similar name to [{string.Join(", ", missing)}]. A requested member that is not in the found list (and the class is not partial) does not exist in '{className}'.";
        }

        var retryNames = memberNames.Select(n => topByEntry.TryGetValue(n, out var top) ? top : n).Distinct(StringComparer.Ordinal);
        var retry = ToolCallHint.Next(toolName, string.Empty, ("memberNames", QuoteArray(retryNames)));
        return didYouMean + " " + retry;
    }

    /// <summary>
    /// Selects the class members named by memberNames, in declaration order (methods, properties, field declarations
    /// holding a requested variable, nested types). Every entry is validated before anything is returned, so a caller that
    /// writes only after this returns writes nothing on a refusal: bare names matching several overloads throw
    /// ToolAmbiguousMatchException (all ambiguous names in one error); an entry matching nothing throws ToolNotFoundException.
    /// toolName and verb only word the errors (the retry hint's tool and the action); MoveMember is the default,
    /// ExtractMembers passes ("ExtractMembers", "extract") and its own method/property subset of the class members.
    /// </summary>
    public static List<MemberDeclarationSyntax> SelectMembers(IEnumerable<MemberDeclarationSyntax> classMembers, string[] memberNames, string className, string toolName = "MoveMember", string verb = "move")
    {
        var members = classMembers.ToList();
        var overloaded = FindOverloadedNames(members, memberNames);
        if (overloaded.Count > 0)
        {
            throw BuildAmbiguous(overloaded, memberNames, className, toolName, verb);
        }

        var selected = new HashSet<MemberDeclarationSyntax>();
        var missing = new List<string>();
        foreach (var entry in memberNames.Distinct(StringComparer.Ordinal))
        {
            var signature = IsSignatureEntry(entry) ? NormalizeSignatureEntry(entry) : null;
            var matched = false;
            foreach (var member in members)
            {
                if (Matches(member, entry, signature))
                {
                    selected.Add(member);
                    matched = true;
                }
            }

            if (!matched)
            {
                missing.Add(entry);
            }
        }

        var result = members.Where(selected.Contains).ToList();
        if (result.Count == 0)
        {
            // TODO no filePath reaches SelectMembers, so the no-candidate case cannot prefill GetFileOutline(filePath)
            throw new ToolNotFoundException($"None of the requested member(s) [{string.Join(", ", memberNames)}] were found in class '{className}' (found: [{string.Join(", ", SimilarFirst(DescribeAvailable(members), missing))}]).{BuildNotFoundHints(memberNames, missing, members, className, toolName)}");
        }

        // A requested entry that matches no member in this file is otherwise dropped silently while the rest moves (e.g. the
        // member lives in another part of a partial class). Refuse before any write. The found list shows method signatures
        // so the caller can copy one.
        if (missing.Count > 0)
        {
            // TODO no filePath reaches SelectMembers, so the no-candidate case cannot prefill GetFileOutline(filePath)
            throw new ToolNotFoundException($"Requested member(s) [{string.Join(", ", missing)}] were not found in class '{className}' in this file (found: [{string.Join(", ", SimilarFirst(DescribeAvailable(members), missing))}]). Nothing was {PastTense(verb)}. If the class is partial, the member may be declared in another part; pass that file's path.{BuildNotFoundHints(memberNames, missing, members, className, toolName)}");
        }

        return result;
    }
}
