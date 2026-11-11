using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Glue between a Tools-layer compile-gate site and <see cref="EditImpactAnalyzer"/>: fetches the committed solution (still the
/// baseline when the gate rejects), picks the active-tool check, and returns the analyzer's text or null. Static and
/// stateless, no MCP types, so the Basic and Advanced tool classes share one wiring. A null result means "nothing attributable":
/// the caller keeps its existing text (<c>CompilerErrorLookupHelper</c>, <c>ValidateAndApplyHelper.DescribeUntouchedFileErrors</c>).
/// </summary>
public static class EditImpactGate
{
    // TODO the verdict list reaches the wire only on a rejected ReplaceSnippet (single and batch): WorkspaceFileEditImpl.RejectionDetail builds one EditImpactDetail {verdicts, diagnostics}
    // from AnalyzeAsync and puts it in ResultError.StructuredDetail. Still text only: Describer (its callback in ValidateAndApplyHelper.ValidateAndApplyAsync(describeEditImpact:) returns string?,
    // so it needs a structured return; used by the refactoring tools in RefactoringStructuralImpl, RefactoringSignatureImpl, RefactoringExtractionDocsImpl and Tools.Advanced GenerationTools)
    // and PrependAsync (WriteFile, ApplyDiff, ApplyUnifiedDiff, CreateFile in WholeFileWriteTools and WorkspaceFileEditImpl.CreateFile). Move them to AnalyzeAsync and EditImpactDetail.
    /// <summary>
    /// The callback shape <c>ValidateAndApplyHelper.ValidateAndApplyAsync(describeEditImpact:)</c> takes, closing over the
    /// change set the gate validates.
    /// </summary>
    /// <param name="workspaceManager">Supplies the committed (baseline) solution.</param>
    /// <param name="changes">Edited and created files with their proposed text.</param>
    /// <param name="activeToolSurface">Decides which tools the next step may name; null (unit tests, hosts without a surface) treats every tool as callable.</param>
    /// <param name="removePaths">Files the edit moves away (a rename's old path); their declarations count as removed.</param>
    /// <param name="deletePaths">Files the edit deletes; their declarations count as removed.</param>
    public static Func<DiagnosticReport, CancellationToken, Task<string?>> Describer(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        ActiveToolSurface? activeToolSurface,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null)
        => (report, ct) => DescribeAsync(workspaceManager, changes, report, activeToolSurface, removePaths, deletePaths, ct);

    /// <summary>
    /// For the sites that build their own rejection text (WriteFile, ApplyDiff, ApplyUnifiedDiff): the analyzer's report
    /// followed by <paramref name="existingText"/>, or <paramref name="existingText"/> unchanged when nothing is attributable.
    /// Those sites add no untouched-file sentence, so nothing is duplicated.
    /// </summary>
    public static async Task<string> PrependAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport report,
        ActiveToolSurface? activeToolSurface,
        string existingText,
        CancellationToken cancellationToken = default)
    {
        var impact = await DescribeAsync(workspaceManager, changes, report, activeToolSurface, cancellationToken: cancellationToken);
        return impact is null ? existingText : impact + "\n" + existingText;
    }

    /// <summary>The analyzer's report for a rejected edit, or null when no error in an unedited file can be attributed.</summary>
    public static async Task<string?> DescribeAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport report,
        ActiveToolSurface? activeToolSurface,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? editedSpans = null,
        IReadOnlyDictionary<string, string>? rejectedSiblingTexts = null)
        => (await AnalyzeAsync(workspaceManager, changes, report, activeToolSurface, removePaths, deletePaths, cancellationToken, editedSpans, rejectedSiblingTexts))?.Text;

    /// <summary>
    /// The analyzer's structured result for a rejected edit (text, next step, diff, attributed diagnostics), or null when no error outside the edit can be
    /// attributed or the analysis failed.
    /// </summary>
    /// <param name="editedSpans">Per edited file, the model's replacements as spans of the baseline text; lets errors outside them in an edited file be attributed (see <see cref="EditImpactAnalyzer.AnalyzeAsync"/>).</param>
    /// <param name="rejectedSiblingTexts">Full file path to the newContent of other files' edits that were rejected or are still in flight (the analyzer keeps only this solution's documents), for names only an unapplied sibling declares.</param>
    public static async Task<EditImpactResult?> AnalyzeAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport report,
        ActiveToolSurface? activeToolSurface,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? editedSpans = null,
        IReadOnlyDictionary<string, string>? rejectedSiblingTexts = null)
    {
        try
        {
            var baseline = await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            Func<string, bool> isToolActive = activeToolSurface is null ? _ => true : activeToolSurface.IsCallable;
            var removed = (removePaths ?? []).Concat(deletePaths ?? []).ToList();
            return await EditImpactAnalyzer.AnalyzeAsync(baseline, changes, report, isToolActive, removed, cancellationToken, editedSpans, rejectedSiblingTexts);
        }
        // The impact text is an optional extra on a rejection that is already being reported: any analyzer or workspace
        // failure falls back to the existing text instead of masking the real compiler errors with a second error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
