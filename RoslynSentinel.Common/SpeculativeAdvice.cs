namespace RoslynSentinel.Common;

/// <summary>
/// The single point where a not-found builder's speculative sentence is produced. Such a sentence is a hedge that the
/// server cannot verify, for example "A file read earlier under this name may since have been renamed or moved.", "If none
/// of these is the symbol you meant, it does not exist in this solution." or a "typo, renamed, moved or removed" style
/// remark. A builder passes the sentence through <see cref="Resolve"/> at its single exit point so that a later step can
/// replace the hedge with a recorded fact (an edit effect of an earlier call in this session) without touching every builder.
/// Without an effect fact for the current call (no context, no term, no index, no matching effect) the produced text is
/// byte-identical to the hedge passed in. Only the hedge SENTENCE goes through it, never a candidate list or other facts and
/// options around it.
/// </summary>
public static class SpeculativeAdvice
{
    /// <summary>
    /// Returns the sentence to render in place of <paramref name="hedge"/>: the fact sentence of a recorded edit effect for the current
    /// call's missing term (<see cref="EditEffectEvidence.FactForCurrentCall"/>) when there is one, otherwise <paramref name="hedge"/>
    /// unchanged, as given (an empty or multi-line string included). Never throws.
    /// </summary>
    public static string Resolve(string hedge)
    {
        // TODO slice A: match FileDeleted/file-move effects by TargetFilePath
        // TODO Resolve keys on ToolCallContext.MissingTerm only, so a builder whose hedge is about a different name (an insert anchor, a member list) could get a fact about the call's term.
        // TODO slice E step 3: set ToolCallContext.Current.HasEditEffectFact when the fact replaces the hedge.
        try
        {
            return EditEffectEvidence.FactForCurrentCall() ?? hedge;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Speculative advice lookup failed: {ex}");
            return hedge;
        }
    }
}
