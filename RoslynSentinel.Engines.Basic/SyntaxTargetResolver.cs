using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Resolves a name plus a context snippet to one declaration, syntax-only and stateless.
/// </summary>
public static class SyntaxTargetResolver
{
    /// <summary>
    /// Reduces a type name to the bare identifier Roslyn's <c>Identifier.Text</c> exposes, by
    /// stripping a trailing type-argument list (<c>Foo<T></c>, <c>Foo<TKey, TValue></c>)
    /// or backtick arity (<c>Foo`1</c>).
    /// </summary>
    /// <remarks>
    /// Needed because <c>Identifier.Text</c> is already arity-stripped, so comparing it against a
    /// caller's raw string rejected the type's own declared spelling: run 20260910-013550-398 had
    /// <c>containerName: "EngineResultWrapper<T>"</c> fail and the bare
    /// <c>"EngineResultWrapper"</c> succeed on the next turn. Third recorded instance
    /// (cf. project_qwen36_35b_smoketest_and_member_containername_gap). Applied to both sides of
    /// the comparison so all three spellings resolve identically.
    /// </remarks>
    public static string NormalizeTypeName(string typeName)
    {
        var name = typeName.Trim();

        var backtick = name.IndexOf('`');
        if (backtick > 0)
        {
            return name[..backtick];
        }

        // Only a trailing argument list is stripped -> an angle bracket anywhere else isn't arity
        // (a caller passing a whole declaration line, say), and truncating there would silently
        // resolve to the wrong type rather than reporting a miss.
        var open = name.IndexOf('<');
        return open > 0 && name.EndsWith('>') ? name[..open].TrimEnd() : name;
    }

    /// <summary>
    /// Disambiguation helper: a type's own name and its constructor's name are identical, so when
    /// both appear in the candidate set, prefer the constructor over the enclosing type declaration
    /// -- mirrors the inline check previously duplicated in ResolveMemberByNameOrSnippet and
    /// ResolveMemberOrEnumMemberByNameOrSnippet.
    /// </summary>
    public static List<SyntaxNodeCandidate> PreferConstructorOverType(List<SyntaxNodeCandidate> candidates)
    {
        if (candidates.Count > 1 && candidates.Any(c => c.Kind == CandidateKind.Constructor))
        {
            var narrowed = candidates.Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)).ToList();
            if (narrowed.Count > 0)
            {
                return narrowed;
            }
        }

        return candidates;
    }

    /// <summary>
    /// Disambiguation helper: when an interface member and a same-named implementer member both
    /// match, prefer the implementer -- mirrors the inline check previously duplicated across the
    /// same two resolvers as PreferConstructorOverType.
    /// </summary>
    public static List<SyntaxNodeCandidate> PreferNonInterfaceMember(List<SyntaxNodeCandidate> candidates)
    {
        var interfaceMembers = candidates.Where(c => c.Node.Parent is InterfaceDeclarationSyntax).ToList();
        var nonInterfaceMembers = candidates.Where(c => c.Node.Parent is not InterfaceDeclarationSyntax).ToList();
        if (interfaceMembers.Count > 0 && nonInterfaceMembers.Count > 0)
        {
            return nonInterfaceMembers;
        }

        return candidates;
    }

    /// <summary>
    /// Disambiguation helper: narrows candidates to those declared inside a given containing type,
    /// when doing so doesn't drop the set to empty (a caller-supplied hint that doesn't match reality
    /// should not zero out otherwise-valid candidates).
    /// </summary>
    public static List<SyntaxNodeCandidate> FilterByContainingType(List<SyntaxNodeCandidate> candidates, string? containingTypeName)
    {
        if (containingTypeName == null || candidates.Count <= 1)
        {
            return candidates;
        }

        var narrowed = candidates.Where(c => c.ContainingTypeName == containingTypeName).ToList();
        return narrowed.Count > 0 ? narrowed : candidates;
    }

    /// <summary>
    /// Builds the message for a container that could not be found, listing the type names the file
    /// actually declares.
    /// </summary>
    /// <remarks>
    /// Replaces three identical <c>"// Container not found."</c> literals, which said nothing
    /// actionable and -> being prefixed with <c>//</c> -> read as commented-out code rather than an
    /// error. Listing the available names is the single most useful thing to return here: the
    /// caller's next move is always to pick one, and it also reveals a wrong-file mistake
    /// immediately. Generic spellings resolve, so a listed name can be given back verbatim; see
    /// <see cref="SyntaxTargetResolver.NormalizeTypeName"/>.
    /// </remarks>
    public static string BuildContainerNotFoundMessage(SyntaxNode root, string requestedName)
    {
        var declared = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (declared.Count == 0)
        {
            return $"No type named '{requestedName}' was found - this file declares no types at all. " +
                   "Check the filePath.";
        }

        var shown = declared.Take(10).ToList();
        var suffix = declared.Count > shown.Count ? $" (+{declared.Count - shown.Count} more)" : "";
        return $"No type named '{requestedName}' was found in this file. Types declared here: " +
               $"{string.Join(", ", shown)}{suffix}. Pass one of those as containerName - a type " +
               "argument list is optional, so both 'Foo' and 'Foo<T>' resolve.";
    }

    /// <summary>
    /// The one formatter for hint messages over a list of <see cref="SyntaxNodeCandidate"/> records,
    /// for every member, type and enum-member lookup. Reads the precomputed
    /// <see cref="SyntaxNodeCandidate.StartLine"/> and <see cref="SyntaxNodeCandidate.Preview"/> rather
    /// than re-deriving them from the syntax node. This is the formatter for all hintBuilder callbacks
    /// passed to <see cref="ResolveBySnippetOrThrow"/>.
    /// </summary>
    /// <remarks>
    /// Wording is deliberately identical to the two node-based formatters this replaced: each preview is
    /// cut to 50 characters here even though <see cref="SyntaxNodeCandidate.Preview"/> allows 80, because
    /// 50 is what agents have always been shown. Do not widen it without checking the hint-wording tests.
    /// </remarks>
    public static string BuildHintForCandidates(List<SyntaxNodeCandidate> candidates, List<int> matches, string failureMode)
    {
        if (candidates.Count == 0)
        {
            return $"contextSnippet {failureMode}: no candidates found.";
        }

        var previews = candidates.Take(3).Select(c =>
        {
            var text = c.Preview.Length > 50 ? c.Preview.Substring(0, 47) + "..." : c.Preview;
            return $"line {c.StartLine} `{text}`";
        });
        var count = candidates.Count;
        var suffix = count > 3 ? $" (+{count - 3} more)" : "";
        return $"contextSnippet {failureMode} ({count} candidates): {string.Join(", ", previews)}{suffix}. " + "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
    }

    public static string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ClassDeclarationSyntax c => c.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            // EnumDeclarationSyntax/RecordDeclarationSyntax/StructDeclarationSyntax are all
            // MemberDeclarationSyntax (via BaseTypeDeclarationSyntax) and so are already collected
            // as candidates by ResolveMemberByNameOrSnippet's DescendantNodes().OfType<...>() scan ->
            // omitting them here didn't exclude them, it silently made GetMemberName return null for
            // them, so the `GetMemberName(m) == memberName` filter dropped them regardless of what
            // name was searched for. Confirmed: AddSummaryCommentAsync("OrderStatus", ...) against a
            // real, unambiguous top-level enum failed "target not found" purely because of this gap.
            EnumDeclarationSyntax e => e.Identifier.Text,
            RecordDeclarationSyntax r => r.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Returns every syntax-level declaration matching <paramref name="name"/> in this file's parse
    /// tree, unfiltered by kind or container. Replaces ResolveMemberByNameOrSnippet,
    /// ResolveMemberOrEnumMemberByNameOrSnippet, and ResolveTypeByNameOrSnippet's candidate-collection
    /// step with one kind-complete query -- see docs/current/proposal_universal_symbol_resolver.md.
    ///
    /// Never throws: zero matches is an empty list, full stop. This is what preserves the
    /// member-then-type fallback chain callers like AddAttributeAsync depend on (a discarded
    /// earlier attempt at throw-on-empty regressed four tests by breaking that fallback -- see the
    /// proposal's Motivation section). Disambiguation (constructor-over-type, contextSnippet
    /// narrowing, etc.) is deliberately not performed here; callers apply the narrowest helper(s)
    /// they need over the returned list (see PreferConstructorOverType, PreferNonInterfaceMember,
    /// FilterByContainingType, ResolveBySnippetOrThrow below).
    ///
    /// Use this when you want to disambiguate down to ONE target declaration in the current file
    /// (rename it, replace it, read it, etc.) and don't need solution-wide ISymbol data. For a
    /// solution-wide ISymbol per match, call ResolveCandidatesWithSemanticAsync instead. For a
    /// flattened, display-ready report of EVERY match (name, signature, containing type/namespace,
    /// file, line, docCommentId) -- e.g. building a "did you mean" hint or a discovery listing --
    /// use LocateSymbolAsync instead; that is a different consumer shape than this method solves.
    /// </summary>
    public static List<SyntaxNodeCandidate> ResolveCandidates(SyntaxNode root, SourceText sourceText, string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = SyntaxTargetResolver.NormalizeTypeName(name);
        var results = new List<SyntaxNodeCandidate>();
        var lines = sourceText.Lines;

        void Add(SyntaxNode node, CandidateKind kind, string candidateName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containingTypeName = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
            var startLine = lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
            var endLine = lines.GetLineFromPosition(node.Span.End).LineNumber + 1;
            var preview = node.ToString().Split('\n').First().Trim();
            if (preview.Length > 80)
            {
                preview = preview.Substring(0, 77) + "...";
            }

            results.Add(new SyntaxNodeCandidate(node, kind, candidateName, containingTypeName, startLine, endLine, preview));
        }

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            switch (member)
            {
                case MethodDeclarationSyntax m when m.Identifier.Text == name:
                    Add(m, CandidateKind.Method, m.Identifier.Text);
                    break;
                case PropertyDeclarationSyntax p when p.Identifier.Text == name:
                    Add(p, CandidateKind.Property, p.Identifier.Text);
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables.Where(v => v.Identifier.Text == name))
                    {
                        Add(f, CandidateKind.Field, v.Identifier.Text);
                    }

                    break;
                case ConstructorDeclarationSyntax ctor when ctor.Identifier.Text == name:
                    Add(ctor, CandidateKind.Constructor, ctor.Identifier.Text);
                    break;
                case EventDeclarationSyntax ev when ev.Identifier.Text == name:
                    Add(ev, CandidateKind.Event, ev.Identifier.Text);
                    break;
                case EventFieldDeclarationSyntax evf:
                    foreach (var v in evf.Declaration.Variables.Where(v => v.Identifier.Text == name))
                    {
                        Add(evf, CandidateKind.Event, v.Identifier.Text);
                    }

                    break;
                case IndexerDeclarationSyntax idx when normalizedName == "this":
                    Add(idx, CandidateKind.Indexer, "this");
                    break;
                case ClassDeclarationSyntax c when SyntaxTargetResolver.NormalizeTypeName(c.Identifier.Text) == normalizedName:
                    Add(c, CandidateKind.Class, c.Identifier.Text);
                    break;
                case InterfaceDeclarationSyntax i when SyntaxTargetResolver.NormalizeTypeName(i.Identifier.Text) == normalizedName:
                    Add(i, CandidateKind.Interface, i.Identifier.Text);
                    break;
                case StructDeclarationSyntax s when SyntaxTargetResolver.NormalizeTypeName(s.Identifier.Text) == normalizedName:
                    Add(s, CandidateKind.Struct, s.Identifier.Text);
                    break;
                case RecordDeclarationSyntax r when SyntaxTargetResolver.NormalizeTypeName(r.Identifier.Text) == normalizedName:
                    Add(r, CandidateKind.Record, r.Identifier.Text);
                    break;
                case EnumDeclarationSyntax e when SyntaxTargetResolver.NormalizeTypeName(e.Identifier.Text) == normalizedName:
                    Add(e, CandidateKind.Enum, e.Identifier.Text);
                    break;
            }
        }

        foreach (var enumMember in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>().Where(m => m.Identifier.Text == name))
        {
            Add(enumMember, CandidateKind.EnumMember, enumMember.Identifier.Text);
        }

        return results;
    }

    /// <summary>
    /// Disambiguation helper: resolves a contextSnippet against a candidate list the same way
    /// ResolveMemberByNameOrSnippet/ResolveTypeByNameOrSnippet did inline. Returns the single
    /// unambiguous candidate; when contextSnippet is null or the set already has &lt;= 1 entry, returns
    /// the sole candidate (or null) without requiring a snippet at all -- a defensively-supplied
    /// snippet must never fail a call that didn't actually need disambiguating.
    /// Throws InvalidOperationException with a hintBuilder-produced message on zero or 2+ snippet
    /// matches, matching the two resolvers' existing throw behavior.
    /// </summary>
    public static SyntaxNodeCandidate? ResolveBySnippetOrThrow(
        List<SyntaxNodeCandidate> candidates,
        SourceText sourceText,
        string? contextSnippet,
        string? lineBefore,
        string? lineAfter,
        Func<List<SyntaxNodeCandidate>, List<int>, string, string> hintBuilder)
    {
        if (contextSnippet == null || candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var matches = ContextHelper.FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(hintBuilder(candidates, matches, "not found"));
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            var matchedCandidate = candidates.FirstOrDefault(c => c.Node.Span.Contains(match));
            if (matchedCandidate != null)
            {
                return matchedCandidate;
            }

            throw new InvalidOperationException(hintBuilder(candidates, matches, "ambiguous"));
        }

        // 2+ matches
        throw new InvalidOperationException(hintBuilder(candidates, matches, "ambiguous"));
    }
}

/// <summary>
/// A closed set of declaration kinds recognized by ResolveCandidates, replacing the ad-hoc
/// per-call-site MemberDeclarationSyntax/BaseTypeDeclarationSyntax/EnumMemberDeclarationSyntax
/// switch statements previously duplicated across GetMemberName, GetContainerMembersAsync, and
/// FindImplementationsForMemberAsync (see docs/current/proposal_universal_symbol_resolver.md).
/// </summary>
public enum CandidateKind
{
    Method, Property, Field, Constructor, Event, Indexer,
    Class, Interface, Struct, Record, Enum, EnumMember
}

/// <summary>
/// One syntax-level declaration matching a requested name in a single file, as returned by
/// ResolveCandidates. Unfiltered by kind or container -- callers narrow with ordinary LINQ over
/// Kind, rather than threading an extraFilter callback into the query itself.
/// </summary>
public sealed record SyntaxNodeCandidate(
    SyntaxNode Node,
    CandidateKind Kind,
    string Name,
    string? ContainingTypeName,
    int StartLine,
    int EndLine,
    /// <summary>First-line declaration text, for hints/diagnostics.</summary>
    string Preview);
