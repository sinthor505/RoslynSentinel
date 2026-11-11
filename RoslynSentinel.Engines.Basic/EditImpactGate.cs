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
    /// <summary>
    /// The callback shape <c>ValidateAndApplyHelper.ValidateAndApplyAsync(describeEditImpact:)</c> takes, closing over the
    /// change set the gate validates.
    /// </summary>
    /// <param name="workspaceManager">Supplies the committed (baseline) solution.</param>
    /// <param name="changes">Edited and created files with their proposed text.</param>
    /// <param name="activeToolSurface">Decides which tools the next step may name; null (unit tests, hosts without a surface) treats every tool as callable.</param>
    /// <param name="removePaths">Files the edit moves away (a rename's old path); their declarations count as removed.</param>
    /// <param name="deletePaths">Files the edit deletes; their declarations count as removed.</param>
    public static Func<DiagnosticReport, CancellationToken, Task<(string? Text, IReadOnlyList<object>? StructuredDetail)>> Describer(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        ActiveToolSurface? activeToolSurface,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null)
        => async (report, ct) =>
        {
            var impact = await AnalyzeAsync(workspaceManager, changes, report, activeToolSurface, removePaths, deletePaths, ct);
            // The detail is built even when nothing is attributable, so the shape never varies (same as a rejected ReplaceSnippet).
            return (impact?.Text, BuildDetail(impact, report.Diagnostics));
        };

    /// <summary>
    /// The <c>errorData.structuredDetail</c> of a rejected edit, shared by every compile-gate site: one <see cref="EditImpactDetail"/> holding the verdicts
    /// (<paramref name="liftVerdict"/> first when the edit was tried as a RenameSymbol, then the symbol-diff verdicts) and the compiler diagnostics as two
    /// named lists. Both lists may be empty; the shape does not vary.
    /// </summary>
    public static List<object> BuildDetail(EditImpactResult? impact, IReadOnlyList<DiagnosticInfo> diagnostics, IEnumerable<ImpactVerdict>? liftVerdicts = null)
    {
        var verdicts = new List<ImpactVerdict>();
        if (liftVerdicts is not null)
            verdicts.AddRange(liftVerdicts);
        if (impact?.Verdicts is { } fromImpact)
            verdicts.AddRange(fromImpact);
        return [new EditImpactDetail(verdicts, diagnostics)];
    }

    /// <summary>
    /// For the sites that build their own rejection text (WriteFile, ApplyDiff, ApplyUnifiedDiff, CreateFile): the whole ValidationFailed error. The message is
    /// <paramref name="messagePrefix"/>, then the analyzer's report followed by <paramref name="existingText"/> (or <paramref name="existingText"/> alone when
    /// nothing is attributable); <c>structuredDetail[0]</c> is the <see cref="EditImpactDetail"/> (see <see cref="BuildDetail"/>), always present.
    /// Those sites add no untouched-file sentence, so nothing is duplicated.
    /// </summary>
    public static async Task<ResultError> RejectionAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        DiagnosticReport report,
        ActiveToolSurface? activeToolSurface,
        string messagePrefix,
        string existingText,
        CancellationToken cancellationToken = default)
    {
        var impact = await AnalyzeAsync(workspaceManager, changes, report, activeToolSurface, cancellationToken: cancellationToken);
        var text = impact is null ? existingText : impact.Text + "\n" + existingText;
        return new ResultError(ToolErrorCode.ValidationFailed, messagePrefix + text, StructuredDetail: BuildDetail(impact, report.Diagnostics));
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
