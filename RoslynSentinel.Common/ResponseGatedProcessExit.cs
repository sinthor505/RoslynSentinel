using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common;

/// <summary>
/// Exits the server process only after the JSON-RPC response to one named request has been written to the transport, so a
/// caller of <c>McpServerControl(StopServer)</c> gets its acknowledgement instead of "Connection closed". The tool calls
/// <see cref="ExpectResponseTo"/> with its own request id; the outgoing-message filter registered in
/// <c>AddRoslynSentinelToolsBasic</c> passes every outgoing message through <see cref="ObserveOutgoingAsync"/>, which sends
/// it, and when it is the response (or error) for the expected id waits a short grace period and exits. A fallback timer
/// exits anyway if the filter never sees that response (for example a task-augmented call, whose request id is answered
/// by a task handle instead; <c>McpServerControl</c> is not task-eligible today, see <c>TaskEnabledToolsHelper</c>).
/// <para>
/// The exit action is injected so tests never call <see cref="Environment.Exit"/>. <see cref="ProcessExitScheduler"/> stays
/// the blind-timer exit for callers with no response involved (the idle-restart monitor).
/// </para>
/// </summary>
public sealed class ResponseGatedProcessExit
{
    /// <summary>Delay between the response being written and the exit, so the transport's flush reaches the client.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>Exit deadline if the expected response is never seen by the outgoing filter.</summary>
    public static readonly TimeSpan DefaultFallback = TimeSpan.FromSeconds(10);

    private readonly Action _exit;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _fallback;
    private readonly object _gate = new();
    private RequestId? _expected;
    private int _exitStarted;

    /// <param name="exit">The exit action; null means <c>Environment.Exit(0)</c>.</param>
    /// <param name="grace">Delay after the response was written; null means <see cref="DefaultGrace"/>.</param>
    /// <param name="fallback">Exit deadline when no response is seen; null means <see cref="DefaultFallback"/>.</param>
    public ResponseGatedProcessExit(Action? exit = null, TimeSpan? grace = null, TimeSpan? fallback = null)
    {
        _exit = exit ?? (() => Environment.Exit(0));
        _grace = grace ?? DefaultGrace;
        _fallback = fallback ?? DefaultFallback;
    }

    /// <summary>True between <see cref="ExpectResponseTo"/> and the exit being started.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _expected is not null && _exitStarted == 0;
            }
        }
    }

    /// <summary>
    /// Registers that the process must exit after the response to <paramref name="requestId"/> is written, and starts the
    /// fallback timer. Returns immediately. A second call replaces the expected id; the exit still happens once.
    /// </summary>
    public void ExpectResponseTo(RequestId requestId)
    {
        lock (_gate)
        {
            _expected = requestId;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(_fallback).ConfigureAwait(false);
            StartExit(delay: TimeSpan.Zero);
        });
    }

    /// <summary>
    /// Sends <paramref name="message"/> through <paramref name="send"/> (the rest of the outgoing pipeline, which performs the
    /// transport write and flush). When the message is the response or error for the expected request id, the exit is started
    /// after the send completes, even if the send throws.
    /// </summary>
    public async Task ObserveOutgoingAsync(JsonRpcMessage message, Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(send);

        if (!IsAnswerToExpected(message))
        {
            await send().ConfigureAwait(false);
            return;
        }

        try
        {
            await send().ConfigureAwait(false);
        }
        finally
        {
            StartExit(_grace);
        }
    }

    private bool IsAnswerToExpected(JsonRpcMessage message)
    {
        RequestId? id = message switch
        {
            JsonRpcResponse response => response.Id,
            JsonRpcError error => error.Id,
            _ => null,
        };

        if (id is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _expected is { } expected && expected.Equals(id.Value) && _exitStarted == 0;
        }
    }

    private void StartExit(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_expected is null || _exitStarted != 0)
            {
                return;
            }

            _exitStarted = 1;
        }

        _ = Task.Run(async () =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            _exit();
        });
    }
}
