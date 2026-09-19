using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Server-integrity faults - currently a failed operation-blob write, which means a change landed on disk with no undo record - that no agent action can clear. Cleared only by restarting the server, after an operator investigates the logged diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has <em>no</em> Reset(), and cannot inherit one now that <see cref="ICircuitBreaker"/>
/// declares none: unresettability is a compile-time guarantee, not a convention. A warning alone
/// was judged insufficient - run 20260910-013550-398 shows an agent reading warnings and carrying
/// on for 60 turns - and reusing <see cref="IManualCircuitBreaker"/> was rejected because the
/// exposed <c>ResetMutationBreaker</c> tool would hand the agent a reset button for a server defect,
/// and because that breaker's semantics are batch-failure rate, not server integrity.
/// </para>
/// <para>
/// Derives from <see cref="ICircuitBreaker"/> so the call-tool filter can consult any breaker
/// through one type. No MCP tool reads or clears this one.
/// </para>
/// </remarks>
public class UnrecoverableCircuitBreaker
: IUnrecoverableBreaker
{
    private readonly ILogger _logger;

    public UnrecoverableCircuitBreaker(ILogger logger)
    {
        _logger = logger;
    }

    // Added by AddMember (expected - used for diagnostics)
    private readonly Lock _unrecoverableBreakerLock = new();


    // Added by AddMember (expected - used for diagnostics)
    private string? _unrecoverableHaltMessage;


    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Records an unrecoverable blob-integrity fault. Not reversible: see
    /// <see cref="IUnrecoverableBreaker"/> for why no reset exists.
    /// </summary>
    public void Trip(string toolName, string changeId, string diagnostic)
    {
        lock (_unrecoverableBreakerLock)
        {
            if (_unrecoverableHaltMessage is not null)
            {
                // Keep the first trip. A later fault is almost certainly downstream of this one,
                // and the earliest diagnostic is the one closest to the root cause.
                return;
            }

            _unrecoverableHaltMessage =
                $"The server recorded an unrecoverable operation-blob integrity failure while applying '{toolName}' " +
                $"(changeId {changeId}): {diagnostic}. Changes may have been written to disk without an undo record, " +
                "so UndoLastApply cannot reverse them. All mutating tools are disabled for the remainder of this " +
                "session; there is no way to clear this from here. Stop, restart the server, and have an operator " +
                "review the server log before making further changes.";
        }

        _logger.LogError(
            "UNRECOVERABLE breaker TRIPPED by {ToolName} (changeId {ChangeId}): {Diagnostic}. " +
            "A change may have applied with no undo record. All mutating tools are now disabled for this process.",
            toolName, changeId, diagnostic);
    }


    // Added by AddMember (expected - used for diagnostics)
    public bool IsTripped()
    {
        lock (_unrecoverableBreakerLock)
        {
            return _unrecoverableHaltMessage is not null;
        }
    }


    // Added by AddMember (expected - used for diagnostics)
    public string? StateMessage()
    {
        lock (_unrecoverableBreakerLock)
        {
            return _unrecoverableHaltMessage;
        }
    }
}
