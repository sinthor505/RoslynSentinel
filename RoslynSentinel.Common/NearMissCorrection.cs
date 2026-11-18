using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// EVALUATION data (see <see cref="NearMissOptions"/>): a proposed changeset in which every unresolved near-miss name has been replaced
/// by the one closest declared name, produced by the engine's correction builder and consumed by
/// <see cref="ValidateAndApplyHelper.ValidateAndApplyAsync"/> through its <c>tryAutoCorrect</c> delegate (Common cannot call the engine).
/// </summary>
/// <param name="CorrectedChanges">A copy of the proposed changes with the wrong-name tokens replaced; the same files as the proposed changes.</param>
/// <param name="CorrectionSpans">
/// Per file, one <see cref="TextChange"/> per replaced token. Positions are in the PROPOSED text (the text the compile gate rejected, before
/// correction), not in the file's pre-image: the wrong name sits inside text the caller's own edit inserted, so these spans overlap the caller's
/// <c>expectedEdits</c> and cannot be unioned with them. The collateral-change check is therefore run as
/// <c>CollateralChangeGuard.Check(path, proposedText, correctedText, spans)</c>, on top of the check that already explained the proposed text.
/// </param>
/// <param name="Substitutions">One <see cref="NearMissSubstitution"/> per replaced token with <c>Applied</c> true; recorded only when the write lands.</param>
/// <param name="Findings">One finding per replaced token for the success result, worded by <see cref="NearMissOptions.NameDisplay"/>.</param>
public sealed record NearMissCorrection(
    Dictionary<FilePathWrapper, string> CorrectedChanges,
    IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>> CorrectionSpans,
    IReadOnlyList<NearMissSubstitution> Substitutions,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>
    /// True when, for every file of <paramref name="proposed"/>, <see cref="CorrectionSpans"/> explain every difference between the proposed text and
    /// <see cref="CorrectedChanges"/> (<see cref="CollateralChangeGuard.Check"/> with the proposed text as the pre-image, since the spans are in proposed-text
    /// positions). False when a file of <paramref name="proposed"/> has no corrected text or a difference is not explained.
    /// The one collateral check of a near-miss correction, shared by <see cref="ValidateAndApplyHelper"/> and the engine's whole-file auto-correct.
    /// </summary>
    public bool SpansExplainCorrectedText(IReadOnlyDictionary<FilePathWrapper, string> proposed)
    {
        foreach (var (key, proposedText) in proposed)
        {
            if (!CorrectedChanges.TryGetValue(key, out var correctedText))
            {
                return false;
            }

            IReadOnlyList<TextChange> spans = CorrectionSpans.TryGetValue(key, out var declared) ? declared : [];
            if (CollateralChangeGuard.Check((string)key, proposedText, correctedText, spans) != null)
            {
                return false;
            }
        }

        return true;
    }
}
