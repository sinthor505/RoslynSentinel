using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// A rejected edit re-expressed as the rename it was: the model's candidate text for every edited document, plus the
/// RenameSymbol output for every document the model did not edit. Engine-internal: it carries no status, only the
/// merged change set the caller validates and the numbers it reports.
/// </summary>
/// <param name="Renames">Every rename the symbol diff found, in diff order; each was resolved to exactly one symbol and renamed on its own against the baseline (there is at least one).</param>
/// <param name="Changes">Every edited document with the model's candidate text, plus every other document the rename changed with the renamed text. The set the compile gate validates.</param>
/// <param name="RenamedFiles">File names (no directory) of the documents the model did not edit and the rename changed.</param>
/// <param name="CallSiteCount">Number of text changes the rename made in <paramref name="RenamedFiles"/>.</param>
/// <param name="EditedFileUseCount">Number of old-name uses the rename rewrote inside the files the model edited, outside its edited spans (for example the class's own constructor).</param>
/// <param name="RewrittenLines">Per file in <paramref name="RenamedFiles"/>, the rewritten lines as <c>File.cs:12  trimmed source line</c> (one per changed line, in file order); null when not collected.</param>
/// <param name="UsesPerRename">Per entry of <paramref name="Renames"/>, how many uses outside the model's edited spans that rename rewrote (other files plus edited files); null when not collected.</param>
public sealed record RenameLiftResult(
    IReadOnlyList<SymbolChange> Renames,
    Dictionary<FilePathWrapper, string> Changes,
    IReadOnlyList<string> RenamedFiles,
    int CallSiteCount,
    int EditedFileUseCount = 0,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RewrittenLines = null,
    IReadOnlyList<int>? UsesPerRename = null)
{
    /// <summary>Each rename as "class A -> B, method C -> D".</summary>
    public string DescribeRenames() => string.Join(", ", Renames.Select(r => $"{r.DeclarationKind} {r.OldName} -> {r.NewName}"));

    /// <summary>
    /// The lines the lift rewrote in the other files, at most <paramref name="maxPerFile"/> per file and then "+N more", so the model
    /// sees its call sites are already updated (batch G R3: it re-sent its own call-site edit with the old text). Empty when none were collected.
    /// </summary>
    public string DescribeRewrittenLines(int maxPerFile = 5) => RenameLifter.DescribeRewrittenLines(RewrittenLines, maxPerFile);

    /// <summary>One sentence for the response: what was renamed and how many uses in how many other files the lift rewrote.</summary>
    public string Describe()
        => $"{DescribeRenames()}: {CallSiteCount} call site(s) in {RenamedFiles.Count} other file(s) ({string.Join(", ", RenamedFiles)}), "
            + (EditedFileUseCount > 0
                ? $"plus {EditedFileUseCount} more use(s) in the file(s) you edited that your edit left on the old name; your own edited text was kept."
                : "while your edit's own text was kept for the file(s) you edited.");

    /// <summary>
    /// The wire verdicts for this lift, one per rename: "lifted" when the merged set applied, "liftRejected" when it was tried and still had errors.
    /// <see cref="ImpactVerdict.BrokenUseCount"/> carries the uses that rename rewrote (call sites in other files plus uses elsewhere in the edited files);
    /// without per-rename counts the total sits on the first entry.
    /// </summary>
    public List<ImpactVerdict> ToVerdicts(bool rejectedAfterLift)
        => Renames.Select((r, i) => new ImpactVerdict("renamed", r.DeclarationKind, r.Container, r.OldName, r.NewName,
            r.File is null ? null : r.Line > 0 ? $"{r.File}:{r.Line}" : r.File,
            rejectedAfterLift ? "liftRejected" : "lifted",
            UsesPerRename is not null && i < UsesPerRename.Count ? UsesPerRename[i] : i == 0 ? CallSiteCount + EditedFileUseCount : 0,
            [], "RenameSymbol", null)).ToList();
}

/// <summary>
/// Rename lifting (proposal_semantic_edit_impact_diagnostics.md, "Second slice", step 1). When the symbol diff of a rejected
/// edit finds exactly one unambiguous rename, runs <c>Renamer.RenameSymbolAsync</c> on the baseline for the old symbol and
/// merges its output with the candidate text: the candidate text wins for every document the edit touched, the renamed text
/// is taken for every other document. The caller validates the merged set through the normal compile gate; this class
/// never writes. Returns null (so the caller keeps the first-slice redirect) whenever the lift is not clearly safe.
/// </summary>
public static class RenameLifter
{
    /// <summary>
    /// The merged change set for a rejected edit, or null when the edit is not a lone, unambiguous rename: no or several
    /// <see cref="SymbolChangeKind.Renamed"/> entries (two added members of one shape leave the diff with no rename at all),
    /// an edited file the baseline does not contain, an old name that resolves to more than one symbol (several
    /// declarations in the solution, or an overloaded method name), a rename failure, or a rename that touches no
    /// other file. Null then comes with a one-line <c>Refusal</c> for the response whenever the diff held at least one rename (the lift is
    /// silent only when there was nothing rename-shaped to lift), so a refusal is never mistaken for "the server did not try".
    /// </summary>
    /// <param name="workspaceManager">Supplies the committed (baseline) solution and the solution root.</param>
    /// <param name="changes">Edited files with the model's proposed text (the dictionary the gate rejected).</param>
    /// <param name="editedSpans">
    /// Per edited file, the model's replacements as spans of the BASELINE text (the matched oldContent range and its newContent), as
    /// the ReplaceSnippet paths resolved them. With them, old-name uses elsewhere in an edited file (its own constructor, a self-call)
    /// are renamed too; without an entry the file keeps the model's text verbatim.
    /// </param>
    public static async Task<(RenameLiftResult? Plan, string? Refusal)> PlanAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? editedSpans = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var baselineTexts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var candidateTexts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, text) in changes)
            {
                // A file the baseline lacks is a created file: not an edit of existing declarations, so nothing to lift.
                if (await EditImpactAnalyzer.DocumentTextAsync(baseline, path.ToString(), cancellationToken) is not { } before)
                {
                    return (null, "an edited file is a new file, and only edits of existing files are lifted");
                }

                baselineTexts[path.ToString()] = before;
                candidateTexts[path.ToString()] = text;
            }

            var renames = EditImpactAnalyzer.DiffSymbols(baselineTexts, candidateTexts).Where(c => c.Kind == SymbolChangeKind.Renamed).ToList();
            if (renames.Count == 0 || renames.Any(r => string.IsNullOrEmpty(r.NewName)))
            {
                return (null, null); // nothing rename-shaped: stay silent
            }

            // Every entry must resolve to exactly one symbol; one that does not (ambiguous, overloaded, declared twice) refuses the whole
            // lift, because lifting only part of the edit's renames would leave the rest of the model's intent half applied.
            var symbols = new List<ISymbol>();
            foreach (var renameEntry in renames)
            {
                if (await ResolveUniqueOldSymbolAsync(baseline, renameEntry, cancellationToken) is not { } resolved)
                {
                    return (null, $"{renameEntry.DeclarationKind} {renameEntry.OldName} does not resolve to exactly one symbol in the solution (it is overloaded, declared in more than one place, or was not found), and a lift needs every rename of the edit to be unambiguous");
                }

                symbols.Add(resolved);
            }

            // Comments and strings stay untouched, as in the RenameSymbol tool (Roslyn would rewrite any string containing the old name).
            var renameOptions = new SymbolRenameOptions { RenameInComments = false, RenameInStrings = false };

            // Each rename runs on its own against the baseline (H1 R5 T3/T4: one whole-file edit renamed a class and its method, and only the
            // class had callers outside the file). Their text changes are unioned per document in baseline coordinates; a change that overlaps
            // one already taken is dropped. A rename with no use outside the model's edit adds nothing here. The compile gate then validates.
            var renamerChanges = new Dictionary<DocumentId, List<(TextChange Change, int Rename)>>();
            for (var i = 0; i < symbols.Count; i++)
            {
                var renamed = await Renamer.RenameSymbolAsync(baseline, symbols[i], renameOptions, renames[i].NewName!, cancellationToken);
                foreach (var projectChange in renamed.GetChanges(baseline).GetProjectChanges())
                {
                    foreach (var changedId in projectChange.GetChangedDocuments())
                    {
                        if (baseline.GetDocument(changedId) is not { } oldDocument || renamed.GetDocument(changedId) is not { } newDocument)
                        {
                            continue;
                        }

                        if (!renamerChanges.TryGetValue(changedId, out var taken))
                        {
                            renamerChanges[changedId] = taken = [];
                        }

                        foreach (var change in await newDocument.GetTextChangesAsync(oldDocument, cancellationToken))
                        {
                            if (!taken.Any(existing => Overlaps(existing.Change.Span, change.Span)))
                            {
                                taken.Add((change, i));
                            }
                        }
                    }
                }
            }

            // Every document id the model edited (a linked file has one id per project): the candidate text wins there, and
            // the renamed text of that document, which also holds the model's old declaration site, is discarded.
            var editedIds = new Dictionary<DocumentId, FilePathWrapper>();
            foreach (var path in changes.Keys)
            {
                foreach (var id in baseline.GetDocumentIdsWithFilePath(path.ToString()))
                {
                    editedIds[id] = path;
                }
            }

            var merged = new Dictionary<FilePathWrapper, string>(changes);
            var renamedFiles = new List<string>();
            var callSites = 0;
            var editedFileUses = 0;
            var usesPerRename = new int[renames.Count];
            var rewrittenLines = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var mergedEditedPaths = new HashSet<FilePathWrapper>();
            foreach (var (documentId, documentChanges) in renamerChanges)
            {
                if (documentChanges.Count == 0 || baseline.GetDocument(documentId) is not { FilePath: { } filePath } oldDocument)
                {
                    continue;
                }

                if (editedIds.TryGetValue(documentId, out var editedPath))
                {
                    // An edited file: the model's text wins inside its edited spans; the renamers' other changes (outside every
                    // span) are applied on top, so a use of the old name its snippet did not reach is renamed too. A linked file
                    // is merged once, through its first document.
                    if (editedSpans is not null && editedSpans.TryGetValue(editedPath, out var spans) && mergedEditedPaths.Add(editedPath)
                        && await MergeIntoEditedFileAsync(workspaceManager, editedPath, oldDocument, documentChanges, spans, cancellationToken) is { } mergedFile)
                    {
                        merged[editedPath] = mergedFile.Text;
                        editedFileUses += mergedFile.SurvivingRenames.Count;
                        foreach (var renameIndex in mergedFile.SurvivingRenames)
                        {
                            usesPerRename[renameIndex]++;
                        }
                    }

                    continue;
                }

                var wrapper = new FilePathWrapper(filePath, workspaceManager.GetSolutionRoot());
                if (merged.ContainsKey(wrapper))
                {
                    continue; // a linked file reached through a second project
                }

                foreach (var (_, renameIndex) in documentChanges)
                {
                    usesPerRename[renameIndex]++;
                }

                var orderedChanges = documentChanges.Select(c => c.Change).OrderBy(c => c.Span.Start).ToList();
                var newText = (await oldDocument.GetTextAsync(cancellationToken)).WithChanges(orderedChanges);
                merged[wrapper] = newText.ToString();
                callSites += orderedChanges.Count;
                renamedFiles.Add(Path.GetFileName(filePath));
                rewrittenLines[Path.GetFileName(filePath)] = RewrittenLinesOf(Path.GetFileName(filePath), newText, orderedChanges);
            }

            return renamedFiles.Count == 0 && editedFileUses == 0
                ? (null, "the renames change nothing outside your edit, so applying them as RenameSymbol would not fix the errors")
                : (new RenameLiftResult(renames, merged, renamedFiles, callSites, editedFileUses, rewrittenLines, usesPerRename), null);
        }
        // The lift is an optional extra on a rejection that is already being reported: any Roslyn or workspace failure falls
        // back to the first-slice redirect instead of masking the real compiler errors with a second error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "the renames could not be computed on the current solution");
        }
    }

    /// <summary>Whether two renamer changes collide: their spans share text, or both insert at the same position.</summary>
    private static bool Overlaps(TextSpan a, TextSpan b)
        => (a.Start < b.End && b.Start < a.End) || (a.Length == 0 && b.Length == 0 && a.Start == b.Start);

    /// <summary>
    /// One entry per distinct line the rename changed in a document, as <c>File.cs:12  trimmed line</c> from the NEW text. The changes are
    /// in baseline coordinates and ordered, so each one's new position is its start shifted by the length delta of the changes before it.
    /// </summary>
    private static List<string> RewrittenLinesOf(string fileName, SourceText newText, IReadOnlyList<TextChange> orderedChanges)
    {
        var lines = new List<string>();
        var seen = new HashSet<int>();
        var delta = 0;
        foreach (var change in orderedChanges)
        {
            var newStart = Math.Clamp(change.Span.Start + delta, 0, newText.Length);
            delta += (change.NewText?.Length ?? 0) - change.Span.Length;
            var line = newText.Lines.GetLineFromPosition(newStart);
            if (seen.Add(line.LineNumber))
            {
                lines.Add($"{fileName}:{line.LineNumber + 1}  {line.ToString().Trim()}");
            }
        }

        return lines;
    }

    /// <summary>
    /// The model's text for an edited file with the renamer's remaining changes applied: every renamer change that overlaps one of the
    /// model's edited spans is dropped (the model's text wins there), and the rest are applied together with the model's own
    /// replacements. All spans are in baseline coordinates and disjoint, so applying from the highest start down gives the same text as
    /// shifting each surviving change by the length delta of the edited spans before it. Line endings are then normalised the way the
    /// ReplaceSnippet paths normalise the candidate. Null when the renamer has no surviving change in this file.
    /// </summary>
    private static async Task<(string Text, IReadOnlyList<int> SurvivingRenames)?> MergeIntoEditedFileAsync(
        IWorkspaceManager workspaceManager, FilePathWrapper path, Document baselineDocument, IReadOnlyList<(TextChange Change, int Rename)> renamerChanges,
        IReadOnlyList<TextChange> modelEdits, CancellationToken cancellationToken)
    {
        var surviving = renamerChanges
            .Where(c => !modelEdits.Any(e => c.Change.Span.Start < e.Span.End && c.Change.Span.End > e.Span.Start))
            .ToList();
        if (surviving.Count == 0)
        {
            return null;
        }

        var baselineText = (await baselineDocument.GetTextAsync(cancellationToken)).ToString();
        var text = baselineText;
        foreach (var change in modelEdits.Concat(surviving.Select(s => s.Change)).OrderByDescending(c => c.Span.Start).ThenByDescending(c => c.Span.End))
        {
            text = text.Remove(change.Span.Start, change.Span.Length).Insert(change.Span.Start, change.NewText ?? string.Empty);
        }

        return (EolUtilities.NormalizeEol(text, workspaceManager.ResolveEol((string)path, baselineText)), surviving.Select(s => s.Rename).ToList());
    }

    /// <summary>
    /// The declaration the diff entry's old name denotes in the baseline, or null unless it is exactly one symbol: found
    /// in no project, declared at more than one source location (a same-named type in two projects), or a method whose
    /// name is overloaded in its type (the syntax-only diff pairs one overload by signature; the lift stays away from the set).
    /// </summary>
    private static async Task<ISymbol?> ResolveUniqueOldSymbolAsync(Solution baseline, SymbolChange change, CancellationToken cancellationToken)
    {
        ISymbol? found = null;
        var declarations = new HashSet<(string? File, int Start)>();
        foreach (var project in baseline.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null || EditImpactAnalyzer.ResolveOldSymbol(compilation, change) is not { } symbol)
            {
                continue;
            }

            // A dependent project reaches the symbol through a compilation reference: the same declaration, so it adds no entry.
            if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { SourceTree: { } tree } location)
            {
                continue;
            }

            declarations.Add((tree.FilePath, location.SourceSpan.Start));
            found ??= symbol;
        }

        if (declarations.Count != 1 || found is null)
        {
            return null;
        }

        if (found is IMethodSymbol && found.ContainingType?.GetMembers(found.Name).OfType<IMethodSymbol>().Count() > 1)
        {
            return null;
        }

        return found;
    }

    /// <summary>
    /// "Rewritten call sites:" plus the lines, at most <paramref name="maxPerFile"/> per file and then "+N more", so the model sees its call
    /// sites are already updated (batch G R3, H2 R3: it re-sent a call-site edit with the old text). Empty when there are none. Shared by
    /// the rename lift summary and RenameSymbol's own success result so the two read alike.
    /// </summary>
    public static string DescribeRewrittenLines(IReadOnlyDictionary<string, IReadOnlyList<string>>? rewrittenLines, int maxPerFile = 5)
    {
        if (rewrittenLines is null || rewrittenLines.Count == 0)
        {
            return string.Empty;
        }

        var rows = new List<string>();
        foreach (var (file, lines) in rewrittenLines)
        {
            rows.AddRange(lines.Take(maxPerFile).Select(l => "  " + l));
            if (lines.Count > maxPerFile)
            {
                rows.Add($"  {file}: +{lines.Count - maxPerFile} more");
            }
        }

        return rows.Count == 0 ? string.Empty : "Rewritten call sites:\n" + string.Join("\n", rows);
    }

    /// <summary>The sentence that follows the rewritten lines: tells the model not to edit them again.</summary>
    public const string AlreadyUpdatedSentence = "These call sites are already updated; do not edit them again.";

    /// <summary>
    /// The lines a finished rename rewrote, per file name (no directory), as <c>File.cs:12  trimmed source line</c>: each changed document's
    /// new text diffed against the document in <paramref name="baseline"/>. For RenameSymbol's own result; the lift collects the same rows
    /// from the renamer's text changes while it plans. A path with no baseline document (a new file) is skipped.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RewrittenLinesAsync(
        Solution baseline, IReadOnlyDictionary<FilePathWrapper, string> changes, CancellationToken cancellationToken)
    {
        var rewritten = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, newContent) in changes)
        {
            if (await EditImpactAnalyzer.DocumentTextAsync(baseline, path.ToString(), cancellationToken) is not { } before)
            {
                continue;
            }

            var fileName = Path.GetFileName(path.ToString());
            var oldLines = before.Split('\n');
            var newLines = newContent.Split('\n');
            List<string> lines;
            if (oldLines.Length == newLines.Length)
            {
                // A rename does not add or remove lines, so compare line by line: the hunks SourceText.GetTextChanges returns can be much
                // coarser than the renamed identifiers (they quoted the file's first line).
                lines = [];
                for (var i = 0; i < newLines.Length; i++)
                {
                    if (!string.Equals(oldLines[i], newLines[i], StringComparison.Ordinal))
                    {
                        lines.Add($"{fileName}:{i + 1}  {newLines[i].Trim()}");
                    }
                }
            }
            else
            {
                var newText = SourceText.From(newContent);
                lines = RewrittenLinesOf(fileName, newText, newText.GetTextChanges(SourceText.From(before)).OrderBy(c => c.Span.Start).ToList());
            }

            if (lines.Count > 0)
            {
                rewritten[fileName] = lines;
            }
        }

        return rewritten;
    }
}
