namespace RoslynSentinel.Common;

/// <summary>
/// Per-call slot written by <see cref="ToolResultMarshaller"/> with the offload threshold (in characters) it resolved for
/// the response, read by the offload filter after <c>next</c> so the filter offloads against the same budget the marshaller
/// used. Not the same as <see cref="OversizeUnitSignal"/>, which records a deliberate over-budget response.
/// <para>
/// <b>No signal open = strict no-op</b>: <see cref="SetThreshold"/> does nothing and <see cref="ThresholdChars"/> stays null.
/// Same async-local scheme as <see cref="OversizeUnitSignal"/>: the reference flows into awaits, so a value set on any
/// continuation of the call is seen by the filter that opened it.
/// </para>
/// </summary>
public sealed class InlineBudgetSignal : IDisposable
{
    private static readonly AsyncLocal<InlineBudgetSignal?> Slot = new();

    private readonly InlineBudgetSignal? _previous;
    private volatile bool _closed;

    private InlineBudgetSignal(InlineBudgetSignal? previous) => _previous = previous;

    /// <summary>The offload threshold in characters set by the marshaller for this call, or null when none was set.</summary>
    public int? ThresholdChars { get; private set; }

    /// <summary>Opens a signal for the current async flow (nesting restores the outer one on dispose). Dispose it in a <c>finally</c>.</summary>
    public static InlineBudgetSignal Open()
    {
        var signal = new InlineBudgetSignal(Slot.Value);
        Slot.Value = signal;
        return signal;
    }

    /// <summary>Records the offload threshold on the open signal; a strict no-op when none is open.</summary>
    public static void SetThreshold(int thresholdChars)
    {
        if (Slot.Value is { _closed: false } open)
        {
            open.ThresholdChars = thresholdChars;
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
