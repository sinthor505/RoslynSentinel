namespace RoslynSentinel.Common;

/// <summary>
/// The single point where a not-found builder's speculative sentence is produced. Such a sentence is a hedge that the
/// server cannot verify, for example "A file read earlier under this name may since have been renamed or moved.", "If none
/// of these is the symbol you meant, it does not exist in this solution." or a "typo, renamed, moved or removed" style
/// remark. A builder passes the sentence through <see cref="Resolve"/> at its single exit point so that a later step can
/// replace the hedge with a recorded fact (an edit effect of an earlier call in this session) without touching every builder.
/// Step 1 (this version) is the identity function: the produced text is byte-identical to the hedge passed in. Only the
/// hedge SENTENCE goes through it, never a candidate list or other facts and options around it.
/// </summary>
public static class SpeculativeAdvice
{
    /// <summary>
    /// Returns the sentence to render in place of <paramref name="hedge"/>. Currently always <paramref name="hedge"/>
    /// unchanged, as given (an empty or multi-line string included).
    /// </summary>
    public static string Resolve(string hedge) => hedge;
}
