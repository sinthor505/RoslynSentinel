using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Explains a rejected edit by what it did to the symbols it touched: diffs the declarations of the edited documents
/// (baseline against candidate), links each new compiler error in a file the edit did not change to the removed, renamed
/// or changed declaration it names, and picks one next step from the tools active in the current mode. Pure and
/// deterministic, no MCP types. Returns null when no error in an unedited file can be attributed, so the caller keeps its
/// existing text (<c>CompilerErrorLookupHelper</c>, <c>ValidateAndApplyHelper.DescribeUntouchedFileErrors</c>).
/// </summary>
/// <remarks>
/// Design: docs/current/proposals/proposal_semantic_edit_impact_diagnostics.md, "First slice". E2 wires it into the gate
/// messages from the Tools layer (Common cannot call an engine); see the report that accompanied this slice.
/// </remarks>
public static class EditImpactAnalyzer
{
    /// <summary>
    /// Tool names the next-step lines can mention. A guard test checks each is a real tool name, because they are plain
    /// strings here (Engines cannot see the tool classes).
    /// </summary>
    public static readonly IReadOnlyList<string> NamedTools = ["RenameSymbol", "ChangeSignature", "MethodSignature", "ChangeAccessibility", "ReplaceSnippet"];

    private const int MaxChangesShown = 2;
    private const int MaxUsesShown = 2;

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"'([^']*)'", RegexOptions.Compiled);

    // Diagnostics a removed or renamed declaration produces at its remaining uses.
    private static readonly HashSet<string> MissingNameIds = ["CS1061", "CS0103", "CS0117", "CS0246", "CS0234"];

    // Diagnostics a changed parameter list or return type produces at existing calls.
    private static readonly HashSet<string> SignatureIds = ["CS7036", "CS1501", "CS1503", "CS1729", "CS0029", "CS0266"];

    private static readonly HashSet<string> AccessibilityIds = ["CS0122"];

    /// <summary>The symbol diff of the edited files; public so the diff can be unit-tested without diagnostics.</summary>
    /// <param name="baselineTexts">Path to the text before the edit; null or empty for a file the edit creates.</param>
    /// <param name="candidateTexts">Path to the text after the edit; null or empty for a file the edit removes.</param>
    public static IReadOnlyList<SymbolChange> DiffSymbols(
        IReadOnlyDictionary<string, string?> baselineTexts,
        IReadOnlyDictionary<string, string?> candidateTexts)
        => EditImpactSymbolDiff.Diff(baselineTexts, candidateTexts);

    /// <summary>
    /// Solution-based entry the Tools layer calls: the baseline is the committed solution (still unchanged when the gate
    /// rejects), the candidate text is what the edit proposes to write.
    /// </summary>
    /// <param name="baseline">The committed solution the edit was validated against.</param>
    /// <param name="changes">Edited and created files with their proposed text (the same dictionary the gate validated).</param>
    /// <param name="validation">The gate's report; only errors are read.</param>
    /// <param name="isToolActive">Whether a tool can be named to the caller, for example <c>ActiveToolSurface.IsCallable</c>.</param>
    /// <param name="removedPaths">Files the edit removes or moves away; their declarations count as removed.</param>
    public static async Task<EditImpactResult?> AnalyzeAsync(
        Solution baseline,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport validation,
        Func<string, bool> isToolActive,
        IReadOnlyCollection<FilePathWrapper>? removedPaths = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baselineTexts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var candidateTexts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in changes)
        {
            candidateTexts[path.ToString()] = text;
            baselineTexts[path.ToString()] = await DocumentTextAsync(baseline, path.ToString(), cancellationToken);
        }

        foreach (var path in removedPaths ?? [])
        {
            candidateTexts[path.ToString()] = null;
            baselineTexts[path.ToString()] = await DocumentTextAsync(baseline, path.ToString(), cancellationToken);
        }

        // Unedited files that hold an error: their text gives the "OrderCheckout.cs:9  <line>" use lines.
        var unchanged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in validation.Diagnostics)
        {
            var path = diagnostic.FilePath.ToString();
            if (string.IsNullOrWhiteSpace(path) || unchanged.ContainsKey(path))
            {
                continue;
            }

            if (await DocumentTextAsync(baseline, path, cancellationToken) is { } unchangedText)
            {
                unchanged[path] = unchangedText;
            }
        }

        // The next step may be a RenameSymbol call, whose projectName and docCommentId must be the real ones (placeholders were
        // pasted verbatim by a model, which then concluded its rejected edit had changed the file). The edit was rejected, so the
        // OLD declaration is resolved in the unchanged baseline, which is where RenameSymbol resolves it.
        var handles = new Dictionary<SymbolChange, SymbolHandle>();
        if (validation.Diagnostics.Any(d => d.Severity == "Error"))
        {
            foreach (var change in EditImpactSymbolDiff.Diff(baselineTexts, candidateTexts).Where(c => c.Kind == SymbolChangeKind.Renamed))
            {
                if (await ResolveOldHandleAsync(baseline, change, cancellationToken) is { } handle)
                {
                    handles[change] = handle;
                }
            }
        }

        return Analyze(baselineTexts, candidateTexts, validation.Diagnostics, isToolActive, unchanged,
            symbolHandleOf: change => handles.TryGetValue(change, out var handle) ? handle : null);
    }

    /// <summary>
    /// The wire handle (declaring project name plus documentation comment id) of the declaration a diff entry removed or renamed,
    /// looked up in the baseline. Null when it cannot be pinned down: a generic type in the path, a missing container, or
    /// several overloads that the syntax-only signature text does not tell apart.
    /// </summary>
    private static async Task<SymbolHandle?> ResolveOldHandleAsync(Solution baseline, SymbolChange change, CancellationToken cancellationToken)
    {
        foreach (var project in baseline.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            var symbol = ResolveOldSymbol(compilation, change);
            var id = symbol?.GetDocumentationCommentId();
            if (symbol is null || id is null)
            {
                continue;
            }

            // A dependent project also sees the symbol, as metadata; only the project that declares it in source counts.
            var tree = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
            if (tree is null)
            {
                continue;
            }

            return new SymbolHandle(baseline.GetDocument(tree)?.Project.Name ?? project.Name, id);
        }

        return null;
    }

    internal static ISymbol? ResolveOldSymbol(Compilation compilation, SymbolChange change)
    {
        if (change.DeclarationKind is "class" or "struct" or "record" or "record struct" or "interface" or "enum" or "delegate")
        {
            return DocumentationCommentId.GetFirstSymbolForDeclarationId("T:" + (change.Container.Length == 0 ? change.OldName : change.Container + "." + change.OldName), compilation);
        }

        if (change.Container.Length == 0 || DocumentationCommentId.GetFirstSymbolForDeclarationId("T:" + change.Container, compilation) is not INamedTypeSymbol owner)
        {
            return null;
        }

        var members = owner.GetMembers(change.OldName)
            .Where(m => change.DeclarationKind switch
            {
                "method" => m is IMethodSymbol { MethodKind: MethodKind.Ordinary },
                "property" => m is IPropertySymbol,
                "field" or "enum member" => m is IFieldSymbol,
                "event" => m is IEventSymbol,
                _ => false,
            })
            .ToList();
        if (members.Count == 1)
        {
            return members[0];
        }

        // Overloads: the diff's OldSignature is "(type,type) : ret" in source text with whitespace removed; match it against each
        // overload rendered the same way, and use the match only when it is unique.
        if (members.Count > 1 && change.DeclarationKind == "method" && change.OldSignature is { } wanted)
        {
            var matching = members.OfType<IMethodSymbol>()
                .Where(m => Regex.Replace($"({string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)))}) : {m.ReturnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}", @"\s+", string.Empty)
                    == Regex.Replace(wanted, @"\s+", string.Empty))
                .ToList();
            return matching.Count == 1 ? matching[0] : null;
        }

        return null;
    }

    /// <summary>
    /// Pure core. A file in either dictionary counts as edited. Returns null when there is no error in an unedited file
    /// or none of them can be attributed to a diff entry.
    /// </summary>
    /// <param name="unchangedTexts">Optional text of unedited files, only used to quote the offending source line.</param>
    public static EditImpactResult? Analyze(
        IReadOnlyDictionary<string, string?> baselineTexts,
        IReadOnlyDictionary<string, string?> candidateTexts,
        IReadOnlyList<DiagnosticInfo> diagnostics,
        Func<string, bool> isToolActive,
        IReadOnlyDictionary<string, string>? unchangedTexts = null,
        Func<SymbolChange, SymbolHandle?>? symbolHandleOf = null)
    {
        var editedPaths = baselineTexts.Keys.Concat(candidateTexts.Keys).ToList();
        var outside = diagnostics
            .Where(d => d.Severity == "Error" && !editedPaths.Any(e => SameFile(e, d.FilePath.ToString())))
            .ToList();
        if (outside.Count == 0)
        {
            return null;
        }

        var changes = EditImpactSymbolDiff.Diff(baselineTexts, candidateTexts);
        var candidates = changes
            .Where(c => c.Kind is SymbolChangeKind.Removed or SymbolChangeKind.Renamed or SymbolChangeKind.SignatureChanged
                || (c.Kind == SymbolChangeKind.AccessibilityChanged && c.AccessibilityNarrowed))
            .Where(c => MatchName(c) is not null)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var attributed = new Dictionary<SymbolChange, List<(DiagnosticInfo Diagnostic, string? Line)>>();
        var attributedDiagnostics = new List<DiagnosticInfo>();
        foreach (var diagnostic in outside)
        {
            var line = SourceLineOf(diagnostic, unchangedTexts);
            var messageTokens = MessageIdentifiers(diagnostic.Message);
            var lineTokens = line is null ? [] : Identifier.Matches(line).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

            // Prefer an entry whose old name is in the message (the compiler named it) over one only found in the source line.
            SymbolChange? best = null;
            int bestScore = 0;
            foreach (var candidate in candidates)
            {
                if (!IdsFor(candidate).Contains(diagnostic.Id))
                {
                    continue;
                }

                var name = MatchName(candidate)!;
                int score = messageTokens.Contains(name) ? 2 : lineTokens.Contains(name) ? 1 : 0;
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                continue;
            }

            if (!attributed.TryGetValue(best, out var list))
            {
                attributed[best] = list = [];
            }

            list.Add((diagnostic, line));
            attributedDiagnostics.Add(diagnostic);
        }

        if (attributed.Count == 0)
        {
            return null;
        }

        var ordered = attributed
            .OrderByDescending(kv => kv.Value.Select(u => u.Diagnostic.FilePath.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenByDescending(kv => kv.Value.Count)
            .ToList();
        var primary = ordered[0];
        var nextStep = NextStepFor(primary.Key, primary.Value.Select(u => u.Diagnostic).ToList(), changes, isToolActive, symbolHandleOf);

        var text = new StringBuilder();
        text.Append("Next step: ").AppendLine(nextStep);
        text.AppendLine("Edit impact (symbols this edit changed that unedited files still use):");
        foreach (var (change, uses) in ordered.Take(MaxChangesShown))
        {
            var distinctUses = uses
                .GroupBy(u => (File: Path.GetFileName(u.Diagnostic.FilePath.ToString()), u.Diagnostic.StartLine))
                .Select(g => g.First())
                .ToList();
            var more = distinctUses.Count > MaxUsesShown ? $" (+{distinctUses.Count - MaxUsesShown} more)" : string.Empty;
            text.Append("  - ").Append(Describe(change, changes)).AppendLine(more);
            foreach (var (diagnostic, line) in distinctUses.Take(MaxUsesShown))
            {
                text.Append("      ").Append(Path.GetFileName(diagnostic.FilePath.ToString())).Append(':').Append(diagnostic.StartLine);
                if (line is not null)
                {
                    text.Append("  ").Append(line.Length > 100 ? line[..100] + "..." : line);
                }

                text.AppendLine();
            }
        }

        if (ordered.Count > MaxChangesShown)
        {
            text.AppendLine($"  (+{ordered.Count - MaxChangesShown} more changed symbol(s) still in use)");
        }

        var unattributed = outside.Where(d => !attributedDiagnostics.Contains(d)).ToList();
        return new EditImpactResult(text.ToString().TrimEnd(), nextStep, changes, attributedDiagnostics, unattributed);
    }

    private static string NextStepFor(SymbolChange change, List<DiagnosticInfo> uses, IReadOnlyList<SymbolChange> all, Func<string, bool> isToolActive, Func<SymbolChange, SymbolHandle?>? symbolHandleOf = null)
    {
        int files = uses.Select(u => Path.GetFileName(u.FilePath.ToString())).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var name = change.OldName;
        var batch = isToolActive("ReplaceSnippet")
            ? "make the change and every affected use in ONE call: ReplaceSnippet with batchEdits, one item per file (each item has its own filePath)."
            : "make the change and every affected use in ONE call that edits each affected file.";
        switch (change.Kind)
        {
            case SymbolChangeKind.Renamed:
                {
                    // TODO when the same edit also has a CS0101 duplicate-type error, this rename headline leads and buries it (eval run 20261111-061005-246 T12); lead with the duplicate instead
                    var lead = $"This edit renames {name} to {change.NewName} and {files} file(s) still use {name}: ";
                    // Built only when active: ToolCallHint.Call records the tool as suggested guidance.
                    if (!isToolActive("RenameSymbol"))
                    {
                        return lead + batch;
                    }

                    // The real project and docCommentId (null when unresolved: the placeholders stay, as before).
                    var handle = symbolHandleOf?.Invoke(change);
                    var renameCall = handle is { } h
                        ? ToolCallHint.Call("RenameSymbol", ("projectName", h.ProjectName), ("docCommentId", h.DocCommentId), ("newName", change.NewName!))
                        : ToolCallHint.Call("RenameSymbol", ("newName", change.NewName!));
                    var rename = lead + $"use {renameCall} instead.";

                    // The edit also changed the accessibility: the rename keeps the old one, so name the follow-up (only when callable).
                    return change.NewAccessibility is not null && isToolActive("ChangeAccessibility")
                        ? rename + $" The edit also makes it {change.NewAccessibility}; after the rename apply that with {ToolCallHint.Call("ChangeAccessibility", ("targetName", change.NewName!), ("accessibility", change.NewAccessibility))}."
                        : rename;
                }

            case SymbolChangeKind.SignatureChanged:
                {
                    var lead = $"This edit changes the signature of {name} and {files} file(s) still use the old one: ";
                    var tools = new[] { "ChangeSignature", "MethodSignature" }.Where(isToolActive).ToList();
                    return tools.Count > 0 ? lead + $"use {string.Join(" or ", tools)} instead, which update the callers." : lead + batch;
                }

            case SymbolChangeKind.AccessibilityChanged:
                {
                    var lead = $"This edit makes {name} {change.NewSignature} and {files} file(s) still use it: ";
                    return isToolActive("ChangeAccessibility")
                        ? lead + $"use {ToolCallHint.Call("ChangeAccessibility", ("targetName", name), ("accessibility", change.OldSignature ?? "public"))} instead, which checks the callers."
                        : lead + batch;
                }

            default:
                {
                    // Lead with "keep it": a model that removed an unrelated member by accident read "change
                    // every affected use" as "delete the tests that use it" (run 20261110-075436-856).
                    var lead = $"This edit removes {name} and {files} file(s) still use it. If you did not mean to remove {name}, " +
                        $"keep it in newContent. If removing it is intended, update those uses (never delete or disable a test to make it compile): ";
                    return lead + batch;
                }
        }
    }

    private static string Describe(SymbolChange change, IReadOnlyList<SymbolChange> all)
    {
        var where = change.File is null ? string.Empty : $" ({change.File}:{change.Line})";
        switch (change.Kind)
        {
            case SymbolChangeKind.Renamed:
                return $"renamed {change.OldName} -> {change.NewName}{where}";
            case SymbolChangeKind.SignatureChanged:
                return $"changed signature of {change.OldName}{change.OldSignature} -> {change.NewSignature}{where}";
            case SymbolChangeKind.AccessibilityChanged:
                return $"narrowed {change.OldName} from {change.OldSignature} to {change.NewSignature}{where}";
            default:
                // "removed X, added Y" with no rename claim: name what the same container gained, if anything.
                var added = all
                    .Where(c => c.Kind == SymbolChangeKind.Added && c.Container == change.Container && c.DeclarationKind == change.DeclarationKind)
                    .Select(c => c.OldName)
                    .Take(2)
                    .ToList();
                return $"removed {change.OldName}{where}" + (added.Count > 0 ? $", added {string.Join(", ", added)}" : string.Empty);
        }
    }

    private static HashSet<string> IdsFor(SymbolChange change) => change.Kind switch
    {
        SymbolChangeKind.SignatureChanged => SignatureIds,
        SymbolChangeKind.AccessibilityChanged => AccessibilityIds,
        _ => MissingNameIds,
    };

    /// <summary>The identifier a diagnostic would carry for this entry: the old name; for a constructor, its type; none for an indexer.</summary>
    private static string? MatchName(SymbolChange change)
    {
        if (change.DeclarationKind == "indexer")
        {
            return null;
        }

        if (change.DeclarationKind == "ctor")
        {
            var container = change.Container;
            return container.Length == 0 ? null : container[(container.LastIndexOf('.') + 1)..];
        }

        return change.OldName;
    }

    private static HashSet<string> MessageIdentifiers(string message)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match quoted in Quoted.Matches(message))
        {
            foreach (Match identifier in Identifier.Matches(quoted.Groups[1].Value))
            {
                tokens.Add(identifier.Value);
            }
        }

        return tokens;
    }

    private static string? SourceLineOf(DiagnosticInfo diagnostic, IReadOnlyDictionary<string, string>? unchangedTexts)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.SourceLine))
        {
            return diagnostic.SourceLine.Trim();
        }

        if (unchangedTexts is null || diagnostic.StartLine < 1)
        {
            return null;
        }

        var path = diagnostic.FilePath.ToString();
        var match = unchangedTexts.FirstOrDefault(kv => SameFile(kv.Key, path));
        if (match.Value is null)
        {
            return null;
        }

        var lines = match.Value.Split('\n');
        return diagnostic.StartLine <= lines.Length ? lines[diagnostic.StartLine - 1].Trim() : null;
    }

    internal static async Task<string?> DocumentTextAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        foreach (var id in solution.GetDocumentIdsWithFilePath(path))
        {
            if (solution.GetDocument(id) is { } document)
            {
                return (await document.GetTextAsync(cancellationToken)).ToString();
            }
        }

        return null;
    }

    // TODO unify with the SameFile local in ValidateAndApplyHelper.DescribeUntouchedFileErrors once that file is free
    private static bool SameFile(string a, string b)
    {
        a = a.Trim().Replace('/', '\\');
        b = b.Trim().Replace('/', '\\');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool aRooted = Path.IsPathRooted(a);
        bool bRooted = Path.IsPathRooted(b);
        if (aRooted != bRooted)
        {
            var (absolute, relative) = aRooted ? (a, b) : (b, a);
            return absolute.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
