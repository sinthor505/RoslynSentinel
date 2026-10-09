namespace RoslynSentinel.Common;

/// <summary>
/// Describes why the session (or part of its tool surface) is currently halted, in one place, so a
/// response stamp can tell the caller the exact recovery call without a discovery round trip.
/// </summary>
/// <param name="Kind">One of "unrecoverable", "externalDrift", "orientation", "mutation".</param>
/// <param name="Reason">What tripped.</param>
/// <param name="Recovery">What the caller should do next.</param>
public sealed record HaltInfo(string Kind, string Reason, string Recovery)
{
    /// <summary>Kind value for a server-integrity fault that no agent action can clear.</summary>
    public const string KindUnrecoverable = "unrecoverable";
    /// <summary>Kind value for the external-file-drift session halt latch.</summary>
    public const string KindExternalDrift = "externalDrift";
    /// <summary>Kind value for the orientation (zero-match search) breaker.</summary>
    public const string KindOrientation = "orientation";
    /// <summary>Kind value for the batch-mutation breaker.</summary>
    public const string KindMutation = "mutation";

    private const string UnrecoverableRecovery =
        "No reset exists for this halt; stop and report it to the operator. " +
        "Read-only tools (ReadFile, GetFileOutline, Search, GetMethodSource, Git status/log/diff) still work.";

    private const string OrientationReason =
        "The orientation breaker is tripped: repeated Search(mode: text) calls returned no matches.";

    private const string MutationReason =
        "The batch-mutation breaker is tripped after repeated failed batches; " +
        "Asyncify/BulkComment-style batch tools are disabled.";

    private const string MutationRecoveryWithReset =
        "Review why the batches failed, fix the cause, then call ResetMutationBreaker.";

    private const string MutationRecoveryWithoutReset =
        "Stop and report to the user/operator; the tool that resets this breaker is not available in this tool set.";

    /// <summary>
    /// Returns the highest-precedence active halt, or null when nothing is tripped.
    /// Precedence: unrecoverable, externalDrift, orientation, mutation.
    /// </summary>
    /// <param name="health">Source of the external-drift session halt latch.</param>
    /// <param name="unrecoverable">Server-integrity breaker.</param>
    /// <param name="orientation">Zero-match search breaker.</param>
    /// <param name="mutation">Batch-mutation breaker.</param>
    /// <param name="resetMutationBreakerActive">True when the ResetMutationBreaker tool is exposed in the live tool set.</param>
    public static HaltInfo? From(
        IWorkspaceHealthReporter health,
        IUnrecoverableBreaker unrecoverable,
        IAutomaticCircuitBreaker orientation,
        IManualCircuitBreaker mutation,
        bool resetMutationBreakerActive)
    {
        if (unrecoverable.IsTripped())
        {
            return new HaltInfo(
                KindUnrecoverable,
                unrecoverable.StateMessage() ?? "The server recorded an unrecoverable integrity failure.",
                UnrecoverableRecovery);
        }

        if (health.IsSessionHalted())
        {
            return new HaltInfo(KindExternalDrift, DriftMessages.DriftHaltReason, DriftMessages.DriftHaltRecovery);
        }

        if (orientation.IsTripped())
        {
            return new HaltInfo(
                KindOrientation,
                OrientationReason,
                orientation.StateMessage() ?? string.Empty);
        }

        if (mutation.IsTripped())
        {
            return new HaltInfo(
                KindMutation,
                MutationReason,
                resetMutationBreakerActive ? MutationRecoveryWithReset : MutationRecoveryWithoutReset);
        }

        return null;
    }
}
