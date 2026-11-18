using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// EVALUATION data (see <see cref="NearMissOptions"/>): a near-miss correction that was applied to the workspace by
/// <see cref="NearMissAutoCorrector.TryAutoCorrectAsync"/>. The caller uses <see cref="ApplyResult"/> where it used its own apply result, and
/// <see cref="NearMissCorrection.CorrectedChanges"/> where it used its proposed change set (for <c>ForgetFileMemories</c>, diffs, blob writes).
/// </summary>
/// <param name="ApplyResult">The workspace manager's result for the corrected change set; never an error.</param>
/// <param name="Correction">The correction that was written: corrected text per file, spans, substitutions and findings.</param>
public sealed record NearMissAutoCorrectResult(ApplyChangesResult ApplyResult, NearMissCorrection Correction)
{
    /// <summary>The corrected change set that was written (the same files as the proposed change set).</summary>
    public Dictionary<FilePathWrapper, string> CorrectedChanges => Correction.CorrectedChanges;

    /// <summary>One finding per replaced name, for the success result (worded by <see cref="NearMissOptions.NameDisplay"/>).</summary>
    public IReadOnlyList<Finding> Findings => Correction.Findings;
}

/// <summary>
/// EVALUATION feature (see <see cref="NearMissOptions"/>): the shared auto-correct step for the sites that write through
/// <see cref="IWorkspaceMutator.ApplyProposedChangesAsync"/> directly instead of <c>ValidateAndApplyHelper</c> (ReplaceSnippet, WriteFile, ApplyDiff,
/// ApplyUnifiedDiff). Static and stateless, no MCP types.
/// </summary>
public static class NearMissAutoCorrector
{
    /// <summary>
    /// For a proposed change set the compile gate rejected: when <see cref="NearMissOptions.AutoCorrect"/> is on and the report has a
    /// CS0117, CS1061 or CS0103 error, detects the near-miss names, builds the strict-rule correction
    /// (<see cref="NearMissDetector.BuildCorrection"/>), checks that the correction spans explain every difference from the proposed text
    /// (<see cref="NearMissCorrection.SpansExplainCorrectedText"/>, the check <c>ValidateAndApplyHelper</c> uses) and writes the corrected change set through
    /// <c>ApplyProposedChangesAsync(validateChanges: true)</c>, which compile-validates it first and writes nothing when it fails.
    /// Returns the applied correction, or null (not applicable) when the option is off (no solution access, no detector call), the report has no such error,
    /// no rule holds, or the corrected apply is refused or fails. On a null return after near-miss items were detected, each item is recorded once through
    /// <see cref="NearMissDetector.RecordAmbiguous"/>; the caller then builds the normal rejection from its ORIGINAL apply result.
    /// Substitutions are recorded through <see cref="NearMissRecorder"/> only after the write succeeded. A detector, builder or recorder failure
    /// (anything but cancellation) yields null, because it must never break the rejection it decorates.
    /// </summary>
    /// <param name="workspaceManager">Supplies the committed solution for detection and applies the corrected change set.</param>
    /// <param name="proposedChanges">The change set the compile gate rejected (the caller's own text per file). Not modified.</param>
    /// <param name="rejectedReport">The compile gate's diagnostics for <paramref name="proposedChanges"/> (<c>ApplyChangesResult.ValidationResult</c>).</param>
    /// <param name="toolName">The tool name written into each recorded substitution.</param>
    /// <param name="staleReadPolicy">The policy the caller's own apply passed; the corrected apply uses the same one.</param>
    public static async Task<NearMissAutoCorrectResult?> TryAutoCorrectAsync(
        IWorkspaceManager workspaceManager,
        IReadOnlyDictionary<FilePathWrapper, string> proposedChanges,
        DiagnosticReport rejectedReport,
        string toolName,
        StaleReadPolicy staleReadPolicy,
        CancellationToken cancellationToken = default)
    {
        if (!NearMissOptions.AutoCorrect || !rejectedReport.Diagnostics.Any(d => d.Severity == "Error" && d.Id is "CS0117" or "CS1061" or "CS0103"))
        {
            return null;
        }

        IReadOnlyList<NearMissName>? nearMisses = null;
        try
        {
            nearMisses = await EditImpactGate.DetectNearMissesAsync(workspaceManager, proposedChanges, rejectedReport.Diagnostics, cancellationToken);
            if (nearMisses is not { Count: > 0 })
            {
                return null;
            }

            var correction = NearMissDetector.BuildCorrection(proposedChanges, rejectedReport.Diagnostics, nearMisses, toolName);
            if (correction is not null && correction.SpansExplainCorrectedText(proposedChanges))
            {
                var applied = await workspaceManager.ApplyProposedChangesAsync(
                    correction.CorrectedChanges, validateChanges: true, cancellationToken: cancellationToken, staleReadPolicy: staleReadPolicy);
                if (!applied.IsError)
                {
                    foreach (var substitution in correction.Substitutions)
                    {
                        NearMissRecorder.Record(substitution);
                    }

                    return new NearMissAutoCorrectResult(applied, correction);
                }
            }
        }
        // The correction is an optional extra on a rejection that is about to be reported: a failure of it must not replace that rejection.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (nearMisses is { Count: > 0 })
        {
            try
            {
                NearMissDetector.RecordAmbiguous(proposedChanges, nearMisses, toolName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return null;
    }
}
