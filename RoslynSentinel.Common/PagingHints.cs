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
public sealed record PagingHints(IReadOnlyList<string> RecordsPaths, PagingOrder Order = PagingOrder.Interleaved);
