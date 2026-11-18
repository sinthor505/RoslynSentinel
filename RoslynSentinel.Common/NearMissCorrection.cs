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
    IReadOnlyList<Finding> Findings);
