using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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
    public static readonly IReadOnlyList<string> NamedTools = ["RenameSymbol", "ChangeSignature", "MethodSignature", "ChangeAccessibility", "ReplaceSnippet", "DeleteFile"];

    private const int MaxChangesShown = 2;
    private const int MaxUsesShown = 2;

    // The wire verdict list is capped so errorData.structuredDetail stays small (ResultPager cuts a big one by path).
    private const int MaxVerdicts = 20;
    private const int MaxBrokenUsesOnTheWire = 5;

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"'([^']*)'", RegexOptions.Compiled);

    // Diagnostics a removed or renamed declaration produces at its remaining uses.
    private static readonly HashSet<string> MissingNameIds = ["CS1061", "CS0103", "CS0117", "CS0246", "CS0234"];

    // Diagnostics a changed parameter list or return type produces at existing calls.
    private static readonly HashSet<string> SignatureIds = ["CS7036", "CS1501", "CS1503", "CS1729", "CS0029", "CS0266"];

    private static readonly HashSet<string> AccessibilityIds = ["CS0122"];

    // A local or local function that is gone is only ever reported as an unknown simple name.
    private static readonly HashSet<string> LocalIds = ["CS0103"];

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
    /// <param name="editedSpans">
    /// Per edited file, the model's replacements as spans of the BASELINE text (as the ReplaceSnippet paths resolved them). With them, a diagnostic in an
    /// edited file but outside every replaced span is attributed like one in an unedited file; without an entry the whole file counts as edited.
    /// </param>
    /// <param name="rejectedSiblingTexts">
    /// Full file path to the newContent of recent edits of OTHER files that the gate rejected (or that are still in flight). Only paths that are documents of
    /// <paramref name="baseline"/> count (the memory behind it is process-wide). A name this edit uses that is declared nowhere in the solution
    /// but appears there is attributed to that unapplied edit (parallel calls cannot see each other).
    /// </param>
    public static async Task<EditImpactResult?> AnalyzeAsync(
        Solution baseline,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport validation,
        Func<string, bool> isToolActive,
        IReadOnlyCollection<FilePathWrapper>? removedPaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? editedSpans = null,
        IReadOnlyDictionary<string, string>? rejectedSiblingTexts = null,
        Func<string, string?>? declaringFileOf = null)
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

        // Baseline-coordinate spans to candidate-coordinate regions; a file whose spans cannot be verified against the candidate text keeps
        // no entry, so all of it counts as edited (the first-slice behavior).
        var editedRegions = new Dictionary<string, IReadOnlyList<TextSpan>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, spans) in editedSpans ?? new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>())
        {
            if (baselineTexts.GetValueOrDefault(path.ToString()) is { } before
                && candidateTexts.GetValueOrDefault(path.ToString()) is { } after
                && CandidateRegions(before, after, spans) is { } regions)
            {
                editedRegions[path.ToString()] = regions;
            }
        }

        // Sibling texts of this solution only, by file name for the message; then which names the unapplied-sibling rule may claim: only quoted
        // names from missing-name errors that a sibling's text mentions.
        var siblings = rejectedSiblingTexts?
            .Where(kv => baseline.GetDocumentIdsWithFilePath(kv.Key).Any())
            .GroupBy(kv => Path.GetFileName(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(kv => kv.Value)), StringComparer.OrdinalIgnoreCase);
        Func<string, bool>? isDeclared = null;
        if (siblings is { Count: > 0 })
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in validation.Diagnostics.Where(d => d.Severity == "Error" && MissingNameIds.Contains(d.Id)))
            {
                wanted.UnionWith(MessageIdentifiers(d.Message));
            }

            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in baseline.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                {
                    continue;
                }

                foreach (var name in wanted.Where(n => !declared.Contains(n) && compilation.ContainsSymbolsWithName(n, SymbolFilter.TypeAndMember)).ToList())
                {
                    declared.Add(name);
                }
            }

            isDeclared = declared.Contains;
        }

        // Type renames only: for a new type name, the baseline file outside this edit that already declares it (the rename would be a duplicate type).
        if (validation.Diagnostics.Any(d => d.Severity == "Error"))
        {
            var renamedFileOf = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var change in EditImpactSymbolDiff.Diff(baselineTexts, candidateTexts).Where(c => c.Kind == SymbolChangeKind.Renamed && c.NewName is not null && !EditImpactLocalDiff.IsLocal(c)))
            {
                renamedFileOf[change.NewName!] = change.File;
            }

            var declaredElsewhere = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var project in baseline.Projects)
            {
                if (renamedFileOf.Count == 0 || await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                {
                    continue;
                }

                foreach (var name in renamedFileOf.Keys.Where(n => !declaredElsewhere.ContainsKey(n)).ToList())
                {
                    foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Type, cancellationToken))
                    {
                        var path = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
                        if (path is not null && !candidateTexts.ContainsKey(path)
                            && !string.Equals(Path.GetFileName(path), renamedFileOf[name], StringComparison.OrdinalIgnoreCase))
                        {
                            declaredElsewhere[name] = path;
                            break;
                        }
                    }
                }
            }

            declaringFileOf = name => declaredElsewhere.GetValueOrDefault(name);
        }

        return Analyze(baselineTexts, candidateTexts, validation.Diagnostics, isToolActive, unchanged,
            symbolHandleOf: change => handles.TryGetValue(change, out var handle) ? handle : null,
            editedRegions: editedRegions, rejectedSiblingTexts: siblings, isDeclared: isDeclared,
            declaringFileOf: declaringFileOf);
    }

    /// <summary>
    /// The replaced regions in CANDIDATE text coordinates, from the replacements' spans in baseline coordinates. Each replacement's new text is looked
    /// up at its computed offset in the candidate (line-ending normalized, because the write path normalizes); null when any of them is not found
    /// there, for example when the candidate is a merged set whose text was changed outside the model's spans, so the caller treats the whole file as edited.
    /// </summary>
    internal static IReadOnlyList<TextSpan>? CandidateRegions(string baseline, string candidate, IReadOnlyList<TextChange> changes)
    {
        var eol = candidate.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var regions = new List<TextSpan>();
        int delta = 0;
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.End > baseline.Length)
            {
                return null;
            }

            var start = change.Span.Start + delta;
            var written = Regex.Replace(change.NewText ?? string.Empty, "\r\n|\r|\n", eol);
            if (start < 0 || start + written.Length > candidate.Length || string.CompareOrdinal(candidate, start, written, 0, written.Length) != 0)
            {
                return null;
            }

            regions.Add(new TextSpan(start, written.Length));
            delta += written.Length - change.Span.Length;
        }

        return regions;
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
        Func<SymbolChange, SymbolHandle?>? symbolHandleOf = null,
        IReadOnlyDictionary<string, IReadOnlyList<TextSpan>>? editedRegions = null,
        IReadOnlyDictionary<string, string>? rejectedSiblingTexts = null,
        Func<string, bool>? isDeclared = null,
        Func<string, string?>? declaringFileOf = null)
    {
        var editedPaths = baselineTexts.Keys.Concat(candidateTexts.Keys).ToList();
        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        var outside = errors.Where(d => IsOutsideEdit(d, editedPaths, candidateTexts, editedRegions)).ToList();
        var unapplied = UnappliedNames(errors, rejectedSiblingTexts, isDeclared);
        if (outside.Count == 0 && unapplied.Count == 0)
        {
            return null;
        }

        // Source lines to quote: the baseline text for an unedited file, the candidate text for an edited one (diagnostic positions are in it).
        var lineTexts = new Dictionary<string, string>(unchangedTexts ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (candidatePath, candidateText) in candidateTexts)
        {
            if (candidateText is not null)
            {
                lineTexts[candidatePath] = candidateText;
            }
        }

        var changes = EditImpactSymbolDiff.Diff(baselineTexts, candidateTexts).ToList();
        changes.AddRange(EditImpactLocalDiff.Diff(baselineTexts, candidateTexts));
        changes.AddRange(unapplied);
        if (declaringFileOf is not null)
        {
            changes = changes.Select(c => c.Kind == SymbolChangeKind.Renamed && !EditImpactLocalDiff.IsLocal(c)
                && c.DeclarationKind is ("class" or "struct" or "record" or "interface" or "enum" or "delegate")
                && c.NewName is { } newName
                && declaringFileOf(newName) is { } takenIn
                    ? c with { NewNameTakenIn = takenIn }
                    : c).ToList();
        }

        // A member renamed to a name another declaration in the same container already uses (CS0102/CS0111).
        changes = changes.Select(c => c.Kind == SymbolChangeKind.Renamed && !EditImpactLocalDiff.IsLocal(c) && c.NewNameTakenIn is null
            && EditImpactSymbolDiff.FindTakenMember(candidateTexts, c) is { } taken
                ? c with { NewNameTakenIn = taken.File, NewNameTakenLine = taken.Line }
                : c).ToList();

        var candidates = changes
            .Where(c => c.Kind is SymbolChangeKind.Removed or SymbolChangeKind.Renamed or SymbolChangeKind.SignatureChanged or SymbolChangeKind.Unapplied
                || (c.Kind == SymbolChangeKind.AccessibilityChanged && c.AccessibilityNarrowed))
            .Where(c => MatchName(c) is not null)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var outsideSet = new HashSet<DiagnosticInfo>(outside);
        var attributed = new Dictionary<SymbolChange, List<(DiagnosticInfo Diagnostic, string? Line)>>();
        var attributedDiagnostics = new List<DiagnosticInfo>();
        foreach (var diagnostic in errors)
        {
            // An error inside the edit itself is only ever explained by an unapplied sibling edit; everything else here is by old name.
            bool isOutside = outsideSet.Contains(diagnostic);
            var line = SourceLineOf(diagnostic, lineTexts);
            var messageTokens = MessageIdentifiers(diagnostic.Message);
            var lineTokens = line is null ? [] : Identifier.Matches(line).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

            // Prefer an entry whose old name is in the message (the compiler named it) over one only found in the source line.
            SymbolChange? best = null;
            int bestScore = 0;
            foreach (var candidate in candidates)
            {
                if (!isOutside && candidate.Kind != SymbolChangeKind.Unapplied)
                {
                    continue;
                }

                if (!IdsFor(candidate).Contains(diagnostic.Id))
                {
                    continue;
                }

                // A local is only visible in the file that declares it.
                if (EditImpactLocalDiff.IsLocal(candidate)
                    && !string.Equals(Path.GetFileName(diagnostic.FilePath.ToString()), candidate.File, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = MatchName(candidate)!;
                int score = messageTokens.Contains(name) ? 2 : candidate.Kind == SymbolChangeKind.Unapplied ? 0 : lineTokens.Contains(name) ? 1 : 0;
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
        var nextStep = NextStepOf(primary.Key, primary.Value.Select(u => u.Diagnostic).ToList(), changes, isToolActive, symbolHandleOf);

        // H2 R1: a model whose edit renamed two symbols looped 10 times on a redirect that named only the first. Name every other rename that
        // the RenameSymbol redirect would also apply to (not locals, not renames onto a taken name), one copyable call each.
        if (isToolActive("RenameSymbol") && primary.Key.Kind == SymbolChangeKind.Renamed && !EditImpactLocalDiff.IsLocal(primary.Key))
        {
            // Not only the renames with attributed uses: a member rename whose call sites sit behind a type renamed in the same edit has none attributed
            // (their receiver no longer binds, so no CS1061 names the method), yet the model must rename it too (eval run 20261112-151531-550 T6 vs T15).
            // Attributed ones first, in the order of the impact list, then the remaining renames in diff order.
            var alsoRenamed = ordered.Skip(1)
                .Select(kv => kv.Key)
                .Concat(changes)
                .Where(c => c != primary.Key)
                .Where(c => c.Kind == SymbolChangeKind.Renamed && !EditImpactLocalDiff.IsLocal(c) && c.NewNameTakenIn is null && !string.IsNullOrEmpty(c.NewName))
                .Distinct()
                .ToList();
            if (alsoRenamed.Count > 0)
            {
                nextStep += " The same edit also renames " + string.Join("; ", alsoRenamed.Select(c => $"{c.OldName} to {c.NewName}: use {RenameCallFor(c, symbolHandleOf)}"))
                    + ". Send one RenameSymbol call per rename, then redo the rest of your edit.";

                // A member's docCommentId contains its type's name, so renaming the type first leaves the member's id unresolvable.
                if (IsTypeKind(primary.Key) && alsoRenamed.Any(c => !IsTypeKind(c)))
                {
                    nextStep += " Rename the members before the type " + primary.Key.OldName + " (their docCommentId names the current type).";
                }
            }
        }

        var text = new StringBuilder();
        text.Append("Next step: ").AppendLine(nextStep);
        text.AppendLine("Edit impact (symbols this edit changed that code outside your edit still uses):");
        foreach (var (change, uses) in ordered.Take(MaxChangesShown))
        {
            var distinctUses = uses
                .GroupBy(u => (File: Path.GetFileName(u.Diagnostic.FilePath.ToString()), u.Diagnostic.StartLine))
                .Select(g => g.First())
                .ToList();
            var more = distinctUses.Count > MaxUsesShown ? $" (+{distinctUses.Count - MaxUsesShown} more)" : string.Empty;
            var outsideCount = EditImpactLocalDiff.IsLocal(change) ? $"; {distinctUses.Count} use(s) outside your edit" : string.Empty;
            text.Append("  - ").Append(Describe(change, changes)).Append(outsideCount).AppendLine(more);
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
        return new EditImpactResult(text.ToString().TrimEnd(), nextStep, changes, attributedDiagnostics, unattributed,
            BuildVerdicts(changes, attributed, isToolActive, symbolHandleOf));
    }

    /// <summary>
    /// True when the diagnostic is not inside the edit: its file is not an edited file, or (with regions for that file) its start lies outside
    /// every replaced region. A file with no regions entry counts as edited as a whole.
    /// </summary>
    private static bool IsOutsideEdit(
        DiagnosticInfo diagnostic,
        IReadOnlyList<string> editedPaths,
        IReadOnlyDictionary<string, string?> candidateTexts,
        IReadOnlyDictionary<string, IReadOnlyList<TextSpan>>? editedRegions)
    {
        var path = diagnostic.FilePath.ToString();
        var edited = editedPaths.FirstOrDefault(e => SameFile(e, path));
        if (edited is null)
        {
            return true;
        }

        if (editedRegions is null || editedRegions.FirstOrDefault(kv => SameFile(kv.Key, path)).Value is not { } regions
            || !candidateTexts.TryGetValue(edited, out var candidateText) || candidateText is null)
        {
            return false;
        }

        int offset = 0;
        for (int line = 1; line < diagnostic.StartLine; line++)
        {
            int newline = candidateText.IndexOf('\n', offset);
            if (newline < 0)
            {
                return false;
            }

            offset = newline + 1;
        }

        offset += Math.Max(diagnostic.StartColumn - 1, 0);
        return !regions.Any(r => offset >= r.Start && offset < Math.Max(r.End, r.Start + 1));
    }

    /// <summary>
    /// The names the compiler reports as missing that are declared nowhere in the baseline solution but appear in a recent rejected edit of another
    /// file: one <see cref="SymbolChangeKind.Unapplied"/> entry per name. Empty when there are no rejected sibling edits.
    /// </summary>
    private static List<SymbolChange> UnappliedNames(
        IReadOnlyList<DiagnosticInfo> errors,
        IReadOnlyDictionary<string, string>? rejectedSiblingTexts,
        Func<string, bool>? isDeclared)
    {
        // Order-independent for parallel calls: the caller supplies both the edits that were already rejected and the ones still in flight (WorkspaceFileEditImpl.SiblingEditTexts).
        var result = new List<SymbolChange>();
        if (rejectedSiblingTexts is null || rejectedSiblingTexts.Count == 0)
        {
            return result;
        }

        var tokensByFile = rejectedSiblingTexts.ToDictionary(
            kv => kv.Key,
            kv => Identifier.Matches(kv.Value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal));
        foreach (var diagnostic in errors.Where(d => MissingNameIds.Contains(d.Id)))
        {
            foreach (var name in MessageIdentifiers(diagnostic.Message))
            {
                if (result.Any(c => c.OldName == name) || isDeclared?.Invoke(name) == true)
                {
                    continue;
                }

                var file = tokensByFile.Where(kv => kv.Value.Contains(name)).Select(kv => kv.Key).FirstOrDefault();
                if (file is not null)
                {
                    result.Add(new SymbolChange(SymbolChangeKind.Unapplied, "name", string.Empty, name, null, file, 0, null, null));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// One <see cref="ImpactVerdict"/> per diff entry except added declarations (noise), the entries that break uses first, capped at
    /// <see cref="MaxVerdicts"/>: "breaksUses" with the first few broken uses and the next step when diagnostics outside the edit were attributed to it,
    /// "unverified" for a member of a type this edit renames or removes whose uses break (the members' own uses are masked by the unbound type),
    /// "valid" otherwise.
    /// </summary>
    private static List<ImpactVerdict> BuildVerdicts(
        IReadOnlyList<SymbolChange> changes,
        Dictionary<SymbolChange, List<(DiagnosticInfo Diagnostic, string? Line)>> attributed,
        Func<string, bool> isToolActive,
        Func<SymbolChange, SymbolHandle?>? symbolHandleOf)
    {
        // Rank 2 = breaks uses, 1 = unverified, 0 = valid.
        var verdicts = new List<(int Rank, ImpactVerdict Verdict)>();

        // F2 (eval run 20261112-151531-550 T6): a type renamed or removed in this edit that breaks uses. The call sites of its members then have an
        // unbound receiver type, so the compiler reports no CS1061 for a renamed member and its uses cannot be checked by the symbol diff.
        // TODO also mark unverified when the only evidence is other unattributed CS0246/CS0103/CS0234 in the member's call-site files (not only a renamed owner type)
        var brokenTypes = attributed.Keys
            .Where(c => IsTypeKind(c) && c.Kind is SymbolChangeKind.Renamed or SymbolChangeKind.Removed)
            .ToList();
        foreach (var change in changes.Where(c => c.Kind != SymbolChangeKind.Added))
        {
            var location = change.File is null ? null : change.Line > 0 ? $"{change.File}:{change.Line}" : change.File;
            var kind = change.Kind switch
            {
                SymbolChangeKind.Renamed => "renamed",
                SymbolChangeKind.SignatureChanged => "signatureChanged",
                SymbolChangeKind.AccessibilityChanged => "accessibilityChanged",
                SymbolChangeKind.Unapplied => "unapplied",
                _ => "removed",
            };
            if (attributed.TryGetValue(change, out var uses))
            {
                var useDiagnostics = uses.Select(u => u.Diagnostic).ToList();
                verdicts.Add((2, new ImpactVerdict(kind, change.DeclarationKind, change.Container, change.OldName, change.NewName, location, "breaksUses",
                    useDiagnostics.Count, useDiagnostics.Take(MaxBrokenUsesOnTheWire).ToList(), SuggestedToolFor(change, isToolActive),
                    NextStepOf(change, useDiagnostics, changes, isToolActive, symbolHandleOf))));
            }
            else if (!IsTypeKind(change) && !EditImpactLocalDiff.IsLocal(change) && change.Kind != SymbolChangeKind.Unapplied
                && OwningRenamedType(change, brokenTypes) is { } owner)
            {
                var ownerNewName = owner.Kind == SymbolChangeKind.Renamed ? $" ({owner.OldName} -> {owner.NewName})" : string.Empty;
                verdicts.Add((1, new ImpactVerdict(kind, change.DeclarationKind, change.Container, change.OldName, change.NewName, location, "unverified", 0, [],
                    change.Kind == SymbolChangeKind.Renamed ? SuggestedToolFor(change, isToolActive) : null,
                    $"Uses of {change.OldName} could not be checked: the receiver type no longer binds because of another change in this edit{ownerNewName}. "
                    + "Do not read this as valid; apply that change first, then check this one.")));
            }
            else
            {
                verdicts.Add((0, new ImpactVerdict(kind, change.DeclarationKind, change.Container, change.OldName, change.NewName, location, "valid", 0, [], null, null)));
            }
        }

        return verdicts.OrderByDescending(v => v.Rank).Select(v => v.Verdict).Take(MaxVerdicts).ToList();
    }

    /// <summary>The tool the next step for this entry names (the first callable one of its row in the next-step table), or null when it names none.</summary>
    private static string? SuggestedToolFor(SymbolChange change, Func<string, bool> isToolActive)
    {
        if (change.Kind == SymbolChangeKind.Unapplied || EditImpactLocalDiff.IsLocal(change))
        {
            return null;
        }

        var tools = change.Kind switch
        {
            SymbolChangeKind.Renamed => change.NewNameTakenIn is null ? new[] { "RenameSymbol" } : Array.Empty<string>(),
            SymbolChangeKind.SignatureChanged => ["ChangeSignature", "MethodSignature"],
            SymbolChangeKind.AccessibilityChanged => ["ChangeAccessibility"],
            _ => Array.Empty<string>(),
        };
        return tools.FirstOrDefault(isToolActive);
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
                    if (change.NewNameTakenIn is { } takenMemberFile && change.NewNameTakenLine > 0)
                    {
                        return $"This edit renames {name} to {change.NewName}, but {change.NewName} is already declared in {change.Container} (at {takenMemberFile}:{change.NewNameTakenLine} and {change.File}:{change.Line}), so the rename would create a duplicate member and {files} file(s) still use {name}: keep {name} in newContent, or pick a name {change.Container} does not declare.";
                    }

                    if (change.NewNameTakenIn is { } takenIn && change.NewNameTakenLine == 0)
                    {
                        var takenFile = Path.GetFileName(takenIn);
                        var taken = $"This edit renames {name} to {change.NewName}, but {change.NewName} is already declared in {takenFile}, so the rename would create a duplicate type and {files} file(s) still use {name}: keep {name} in newContent, or pick a name no file declares";
                        return isToolActive("DeleteFile")
                            ? taken + $". If {takenFile} is the superseded copy, delete it with {ToolCallHint.Call("DeleteFile", ("filePath", takenIn))} and make this change in the same step."
                            : taken + $". If {takenFile} is the superseded copy, remove its {change.NewName} declaration in the same call: " + batch;
                    }

                    var lead = $"This edit renames {name} to {change.NewName} and {files} file(s) still use {name}: ";
                    // Built only when active: ToolCallHint.Call records the tool as suggested guidance.
                    if (!isToolActive("RenameSymbol"))
                    {
                        return lead + batch;
                    }

                    var renameCall = RenameCallFor(change, symbolHandleOf);
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

    /// <summary>
    /// The next step for one attributed entry: a local or an unapplied sibling name gets its own wording (no tool renames a local, and a
    /// sibling's missing declaration is fixed in that sibling), every other entry goes to <see cref="NextStepFor"/>.
    /// </summary>
    internal static string NextStepOf(SymbolChange change, List<DiagnosticInfo> uses, IReadOnlyList<SymbolChange> all, Func<string, bool> isToolActive, Func<SymbolChange, SymbolHandle?>? symbolHandleOf = null)
    {
        var batch = isToolActive("ReplaceSnippet") ? "ReplaceSnippet call with batchEdits (one item per file, each with its own filePath)" : "call that edits each affected file";
        if (change.Kind == SymbolChangeKind.Unapplied)
        {
            return $"{change.OldName} is declared nowhere in the solution as it is on disk now, and the only place your edits mention it is your other edit of {change.File}, which was rejected or was still being applied when this call was checked "
                + $"(each call is checked on its own against the files as they are on disk, so parallel calls cannot see each other): wait for that edit to land (fix it first if it was rejected), then send this one, or put both files in one {batch}.";
        }

        if (EditImpactLocalDiff.IsLocal(change))
        {
            var member = change.Container;
            var count = uses.Select(u => (File: Path.GetFileName(u.FilePath.ToString()), u.StartLine)).Distinct().Count();
            return change.Kind == SymbolChangeKind.Renamed
                ? $"This edit renames {change.DeclarationKind} {change.OldName} to {change.NewName} in {member}, but {count} use(s) of {change.OldName} are outside your edit: "
                    + $"widen oldContent to cover every use (for example the whole {member} body), or add a batchEdits item per use, so all uses are renamed in the same call."
                : $"This edit removes {change.DeclarationKind} {change.OldName} from {member} and {count} use(s) of it are outside your edit: "
                    + $"keep {change.OldName} in newContent, or widen oldContent to cover every use and update them in the same call.";
        }

        return NextStepFor(change, uses, all, isToolActive, symbolHandleOf);
    }

    private static string Describe(SymbolChange change, IReadOnlyList<SymbolChange> all)
    {
        var where = change.File is null ? string.Empty : $" ({change.File}:{change.Line})";
        if (change.Kind == SymbolChangeKind.Unapplied)
        {
            return $"uses {change.OldName}, which only your other edit of {change.File} declares";
        }

        if (EditImpactLocalDiff.IsLocal(change))
        {
            return change.Kind == SymbolChangeKind.Renamed
                ? $"renamed {change.DeclarationKind} {change.OldName} -> {change.NewName} in {change.Container}{where}"
                : $"removed {change.DeclarationKind} {change.OldName} from {change.Container}{where}";
        }

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

    private static HashSet<string> IdsFor(SymbolChange change) => EditImpactLocalDiff.IsLocal(change) ? LocalIds : change.Kind switch
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

    /// <summary>
    /// The copyable RenameSymbol call for one rename of the edit: the real project and docCommentId when the symbol resolved, else the
    /// placeholders. Call only when RenameSymbol is active (ToolCallHint.Call records the tool as suggested guidance).
    /// </summary>
    private static string RenameCallFor(SymbolChange change, Func<SymbolChange, SymbolHandle?>? symbolHandleOf)
        => symbolHandleOf?.Invoke(change) is { } h
            ? ToolCallHint.Call("RenameSymbol", ("projectName", h.ProjectName), ("docCommentId", h.DocCommentId), ("newName", change.NewName!))
            : ToolCallHint.Call("RenameSymbol", ("newName", change.NewName!));

    /// <summary>True when the entry declares a type (class, struct, record, interface, enum or delegate), not a member or a local.</summary>
    private static bool IsTypeKind(SymbolChange change)
        => change.DeclarationKind is "class" or "struct" or "record" or "interface" or "enum" or "delegate";

    /// <summary>
    /// The entry of <paramref name="brokenTypes"/> (types renamed or removed by this edit whose uses break) that declares <paramref name="member"/> or one
    /// of its enclosing types, else null. A member's <see cref="SymbolChange.Container"/> is the baseline path, so it ends in the type's old name.
    /// </summary>
    private static SymbolChange? OwningRenamedType(SymbolChange member, IReadOnlyList<SymbolChange> brokenTypes)
    {
        foreach (var type in brokenTypes)
        {
            var path = string.IsNullOrEmpty(type.Container) ? type.OldName : type.Container + "." + type.OldName;
            if (member.Container == path || member.Container.StartsWith(path + ".", StringComparison.Ordinal))
            {
                return type;
            }
        }

        return null;
    }
}
