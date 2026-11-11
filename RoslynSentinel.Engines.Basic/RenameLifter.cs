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
/// <param name="Rename">The single unambiguous rename found by the symbol diff.</param>
/// <param name="Changes">Every edited document with the model's candidate text, plus every other document the rename changed with the renamed text. The set the compile gate validates.</param>
/// <param name="RenamedFiles">File names (no directory) of the documents the model did not edit and the rename changed.</param>
/// <param name="CallSiteCount">Number of text changes the rename made in <paramref name="RenamedFiles"/>.</param>
/// <param name="EditedFileUseCount">Number of old-name uses the rename rewrote inside the files the model edited, outside its edited spans (for example the class's own constructor).</param>
public sealed record RenameLiftResult(
    SymbolChange Rename,
    Dictionary<FilePathWrapper, string> Changes,
    IReadOnlyList<string> RenamedFiles,
    int CallSiteCount,
    int EditedFileUseCount = 0)
{
    /// <summary>One sentence for the response: what was renamed and how many uses in how many other files the lift rewrote.</summary>
    public string Describe()
        => $"{Rename.DeclarationKind} {Rename.OldName} -> {Rename.NewName}: {CallSiteCount} call site(s) in {RenamedFiles.Count} other file(s) ({string.Join(", ", RenamedFiles)}), "
            + (EditedFileUseCount > 0
                ? $"plus {EditedFileUseCount} more use(s) in the file(s) you edited that your edit left on the old name; your own edited text was kept."
                : "while your edit's own text was kept for the file(s) you edited.");

    /// <summary>
    /// The wire verdict for this lift: "lifted" when the merged set applied, "liftRejected" when it was tried and still had errors.
    /// <see cref="ImpactVerdict.BrokenUseCount"/> carries the uses the rename rewrote (call sites in other files plus uses elsewhere in the edited files).
    /// </summary>
    public ImpactVerdict ToVerdict(bool rejectedAfterLift)
        => new("renamed", Rename.DeclarationKind, Rename.Container, Rename.OldName, Rename.NewName,
            Rename.File is null ? null : Rename.Line > 0 ? $"{Rename.File}:{Rename.Line}" : Rename.File,
            rejectedAfterLift ? "liftRejected" : "lifted", CallSiteCount + EditedFileUseCount, [], "RenameSymbol", null);
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
    /// other file.
    /// </summary>
    /// <param name="workspaceManager">Supplies the committed (baseline) solution and the solution root.</param>
    /// <param name="changes">Edited files with the model's proposed text (the dictionary the gate rejected).</param>
    /// <param name="editedSpans">
    /// Per edited file, the model's replacements as spans of the BASELINE text (the matched oldContent range and its newContent), as
    /// the ReplaceSnippet paths resolved them. With them, old-name uses elsewhere in an edited file (its own constructor, a self-call)
    /// are renamed too; without an entry the file keeps the model's text verbatim.
    /// </param>
    public static async Task<RenameLiftResult?> PlanAsync(
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
                    return null;
                }

                baselineTexts[path.ToString()] = before;
                candidateTexts[path.ToString()] = text;
            }

            var renames = EditImpactAnalyzer.DiffSymbols(baselineTexts, candidateTexts).Where(c => c.Kind == SymbolChangeKind.Renamed).ToList();
            if (renames.Count != 1 || renames[0].NewName is not { Length: > 0 } newName)
            {
                return null;
            }

            var rename = renames[0];
            var symbol = await ResolveUniqueOldSymbolAsync(baseline, rename, cancellationToken);
            if (symbol is null)
            {
                return null;
            }

            // Comments and strings stay untouched, as in the RenameSymbol tool (Roslyn would rewrite any string containing the old name).
            var renameOptions = new SymbolRenameOptions { RenameInComments = false, RenameInStrings = false };
            var renamed = await Renamer.RenameSymbolAsync(baseline, symbol, renameOptions, newName, cancellationToken);

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
            var mergedEditedPaths = new HashSet<FilePathWrapper>();
            foreach (var projectChange in renamed.GetChanges(baseline).GetProjectChanges())
            {
                foreach (var documentId in projectChange.GetChangedDocuments())
                {
                    if (baseline.GetDocument(documentId) is not { } oldDocument || renamed.GetDocument(documentId) is not { FilePath: { } filePath } newDocument)
                    {
                        continue;
                    }

                    if (editedIds.TryGetValue(documentId, out var editedPath))
                    {
                        // An edited file: the model's text wins inside its edited spans; the renamer's other changes (outside every
                        // span) are applied on top, so a use of the old name its snippet did not reach is renamed too. A linked file
                        // is merged once, through its first document.
                        if (editedSpans is not null && editedSpans.TryGetValue(editedPath, out var spans) && mergedEditedPaths.Add(editedPath)
                            && await MergeIntoEditedFileAsync(workspaceManager, editedPath, oldDocument, newDocument, spans, cancellationToken) is { } mergedFile)
                        {
                            merged[editedPath] = mergedFile.Text;
                            editedFileUses += mergedFile.Uses;
                        }

                        continue;
                    }

                    var wrapper = new FilePathWrapper(filePath, workspaceManager.GetSolutionRoot());
                    if (merged.ContainsKey(wrapper))
                    {
                        continue; // a linked file reached through a second project
                    }

                    merged[wrapper] = (await newDocument.GetTextAsync(cancellationToken)).ToString();
                    callSites += (await newDocument.GetTextChangesAsync(oldDocument, cancellationToken)).Count();
                    renamedFiles.Add(Path.GetFileName(filePath));
                }
            }

            return renamedFiles.Count == 0 && editedFileUses == 0 ? null : new RenameLiftResult(rename, merged, renamedFiles, callSites, editedFileUses);
        }
        // The lift is an optional extra on a rejection that is already being reported: any Roslyn or workspace failure falls
        // back to the first-slice redirect instead of masking the real compiler errors with a second error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The model's text for an edited file with the renamer's remaining changes applied: every renamer change that overlaps one of the
    /// model's edited spans is dropped (the model's text wins there), and the rest are applied together with the model's own
    /// replacements. All spans are in baseline coordinates and disjoint, so applying from the highest start down gives the same text as
    /// shifting each surviving change by the length delta of the edited spans before it. Line endings are then normalised the way the
    /// ReplaceSnippet paths normalise the candidate. Null when the renamer has no surviving change in this file.
    /// </summary>
    private static async Task<(string Text, int Uses)?> MergeIntoEditedFileAsync(
        IWorkspaceManager workspaceManager, FilePathWrapper path, Document baselineDocument, Document renamedDocument,
        IReadOnlyList<TextChange> modelEdits, CancellationToken cancellationToken)
    {
        var surviving = (await renamedDocument.GetTextChangesAsync(baselineDocument, cancellationToken))
            .Where(c => !modelEdits.Any(e => c.Span.Start < e.Span.End && c.Span.End > e.Span.Start))
            .ToList();
        if (surviving.Count == 0)
        {
            return null;
        }

        var baselineText = (await baselineDocument.GetTextAsync(cancellationToken)).ToString();
        var text = baselineText;
        foreach (var change in modelEdits.Concat(surviving).OrderByDescending(c => c.Span.Start).ThenByDescending(c => c.Span.End))
        {
            text = text.Remove(change.Span.Start, change.Span.Length).Insert(change.Span.Start, change.NewText ?? string.Empty);
        }

        return (EolUtilities.NormalizeEol(text, workspaceManager.ResolveEol((string)path, baselineText)), surviving.Count);
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
}
