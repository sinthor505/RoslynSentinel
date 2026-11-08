namespace RoslynSentinel.Common;

/// <summary>
/// How the lists of a <see cref="PagingHints"/> sequence are walked into one ordered sequence of units.
/// </summary>
public enum PagingOrder
{
    /// <summary>Round-robin over the lists in declared order; an exhausted list is skipped. The default.</summary>
    Interleaved,

    /// <summary>The first list in full, then the second, and so on.</summary>
    Linear,
}

/// <summary>
/// Server-side declaration of which lists of a result form its one pageable record sequence
/// (proposal_sequence_pager.md, Decided 6). Set on <see cref="SentinelCallToolResult{TSuccess, TError}.Paging"/>;
/// never serialized. Precedence: these hints, then <see cref="ResultRecordsAttribute"/> on the type, then the default
/// sequence (every array directly under <c>successData</c>).
/// </summary>
/// <param name="RecordsPaths">
/// Paths of the arrays that form the sequence, in declared order. A bare property name (<c>nameof(data.callers)</c>)
/// is taken relative to <c>successData</c>; a path starting with <c>successData</c> is taken as is. A path that does
/// not resolve to an array in the serialized result is ignored.
/// </param>
/// <param name="Order">How the lists are walked into one sequence.</param>
/// <param name="AlreadyPaged">
/// True on a page that <c>GetLargeResult</c> built from a stored sequence: the page is already cut to the budget by
/// <see cref="SequencePager"/>, so the marshaller returns it unchanged even when one oversize unit leaves it over budget
/// (never a new <c>resultId</c> and never a second cut). <see cref="RecordsPaths"/> is empty on such a page.
/// </param>
public sealed record PagingHints(IReadOnlyList<string> RecordsPaths, PagingOrder Order = PagingOrder.Interleaved, bool AlreadyPaged = false)
{
    /// <summary>
    /// The hints for a page the tool already cut to the budget (<see cref="AlreadyPaged"/>), and the one place that flag is
    /// produced. It also raises <see cref="OversizeUnitSignal"/> for the current call, because the request-filter backstop
    /// (<c>AddLargeResultOffloadFilter</c>) must not store such a page under a new <c>resultId</c> when one oversize unit leaves
    /// it over budget, in EVERY marshaller mode: with the switch off no marshaller is registered, so the marshaller cannot
    /// raise it. The signal is per call (async-local, opened by the filter around one tool call) and a strict no-op when
    /// none is open, so no other tool's oversize response is ever skipped. Call it while building the page, on the tool's
    /// own async flow.
    /// </summary>
    public static PagingHints ForAlreadyPagedPage()
    {
        OversizeUnitSignal.Raise();
        return new PagingHints([], PagingOrder.Interleaved, AlreadyPaged: true);
    }
}
