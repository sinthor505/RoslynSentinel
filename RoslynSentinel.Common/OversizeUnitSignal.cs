namespace RoslynSentinel.Common;
/// <summary>
/// Per-call signal from <see cref="ToolResultMarshaller"/> to the generic large-result offload filter: "this response is over
/// the inline budget on purpose" (proposal_sequence_pager.md Decided 4: one unit larger than a page is returned alone, and a
/// <c>GetLargeResult</c> page is never re-paged). The filter opens one before running the tool and, when it was raised, leaves
/// the text alone instead of re-wrapping it under a second <c>resultId</c> (a fetch / still-too-big / re-offload loop).
/// <para>
/// <b>No signal open = strict no-op</b> (engines, tests, hosts without the filter). Same async-local scheme as
/// <see cref="ToolGuidanceCollector"/>: the reference flows into awaits, so a raise on any continuation of the call is seen
/// by the filter that opened it.
/// </para>
/// </summary>
public sealed class OversizeUnitSignal : IDisposable
{
    private static readonly AsyncLocal<OversizeUnitSignal?> Slot = new();

    private readonly OversizeUnitSignal? _previous;
    private volatile bool _raised;
    private volatile bool _closed;

    private OversizeUnitSignal(OversizeUnitSignal? previous) => _previous = previous;

    /// <summary>True once the marshaller returned an over-budget response on purpose during this call.</summary>
    public bool Raised => _raised;

    /// <summary>Opens a signal for the current async flow (nesting restores the outer one on dispose). Dispose it in a <c>finally</c>.</summary>
    public static OversizeUnitSignal Open()
    {
        var signal = new OversizeUnitSignal(Slot.Value);
        Slot.Value = signal;
        return signal;
    }

    /// <summary>Raises the open signal; a strict no-op when none is open.</summary>
    public static void Raise()
    {
        if (Slot.Value is { _closed: false } open)
        {
            open._raised = true;
        }
    }

    /// <summary>Closes the signal and restores the outer one.</summary>
    public void Dispose()
    {
        _closed = true;
        if (ReferenceEquals(Slot.Value, this))
        {
            Slot.Value = _previous;
        }
    }
}
