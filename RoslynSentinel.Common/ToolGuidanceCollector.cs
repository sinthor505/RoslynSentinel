namespace RoslynSentinel.Common;

/// <summary>
/// Per-call collector of the tool names that hints suggest next ("Next: ReadFile(...)"). The call-tool telemetry filter
/// (<c>ToolTelemetryRecorder.RecordAsync</c>) opens one before running the tool; <see cref="ToolCallHint"/> then records
/// every call it builds through <see cref="Record"/>, and the filter reads <see cref="Suggestions"/> after the tool
/// returns to feed the guidance-adherence counters on <see cref="ToolTelemetry"/>.
/// <para>
/// <b>No collector open = strict no-op.</b> Engines, tests and other hosts that build hints outside a filter change
/// nothing and never throw. The collector is a mutable, thread-safe object stored in an <see cref="AsyncLocal{T}"/>
/// (the reference, not a copy, flows into awaits and <c>Task.Run</c> children), so suggestions recorded on any
/// continuation of the call land in the same list.
/// </para>
/// <para>
/// <b>Approximation:</b> a hint that is BUILT but then discarded (not emitted in the final result, for example a
/// message composed and replaced, or a hint string built eagerly for a branch that is not taken) is still recorded,
/// so it over-counts "issued". Counts are also approximate with concurrent subagents: stdio carries no session id, so
/// interleaved agents can consume each other's suggestions.
/// </para>
/// </summary>
public sealed class ToolGuidanceCollector : IDisposable
{
    /// <summary>Suggestions kept per call; further ones are dropped (a hint-heavy error message must not grow without bound).</summary>
    public const int MaxSuggestions = 64;

    private static readonly AsyncLocal<ToolGuidanceCollector?> Slot = new();

    private readonly object _gate = new();
    private readonly List<(string ToolName, string? Operation)> _items = [];
    private readonly ToolGuidanceCollector? _previous;
    private bool _closed;

    private ToolGuidanceCollector(ToolGuidanceCollector? previous) => _previous = previous;

    /// <summary>True when a collector is open on the current async flow.</summary>
    public static bool IsOpen => Slot.Value is { _closed: false };

    /// <summary>
    /// Opens a collector for the current async flow (nesting restores the outer one on dispose). Dispose it in a
    /// <c>finally</c>; after that, recording into it is a no-op.
    /// </summary>
    public static ToolGuidanceCollector Open()
    {
        var collector = new ToolGuidanceCollector(Slot.Value);
        Slot.Value = collector;
        return collector;
    }

    /// <summary>
    /// Records a suggested tool (and its operation/mode argument when the hint supplied one - stored for later analysis,
    /// not used for matching yet) into the open collector. A strict no-op when none is open; never throws.
    /// </summary>
    public static void Record(string? toolName, string? operation = null)
    {
        try
        {
            Slot.Value?.Add(toolName, operation);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tool guidance record failed: {ex}");
        }
    }

    /// <summary>The suggestions recorded so far, in order (a copy; safe to enumerate while recording continues).</summary>
    public IReadOnlyList<(string ToolName, string? Operation)> Suggestions
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>Closes the collector: later records into it are ignored and the previous (outer) collector is restored.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        if (ReferenceEquals(Slot.Value, this))
        {
            Slot.Value = _previous;
        }
    }

    private void Add(string? toolName, string? operation)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        lock (_gate)
        {
            if (!_closed && _items.Count < MaxSuggestions)
            {
                _items.Add((toolName, string.IsNullOrWhiteSpace(operation) ? null : operation));
            }
        }
    }
}
