namespace RoslynSentinel.Common;

/// <summary>Minimal shape shared by any breaker: whether it's currently blocking callers, and why.</summary>
/// <remarks>
/// Deliberately declares no Reset(). Resettability is a property of <em>recoverable</em> breakers,
/// not of breakers in general — see <see cref="IUnrecoverableBreaker"/>, which must have no reset
/// path at all and would otherwise inherit one. Each recoverable breaker declares its own Reset();
/// no caller ever reset through this interface, so moving it down cost nothing.
/// </remarks>
public interface ICircuitBreaker
{
    /// <summary>True when this breaker is currently blocking the behavior it guards.</summary>
    bool IsTripped();
    /// <summary>Human-readable detail about the current state (counters, directive, etc.). Null/empty when not tripped.</summary>
    string? StateMessage();
}

/// <summary>Tracks batch-operation failure rates and halts mutating tools after repeated failures. Manual reset only — never auto-reset by design.</summary>
public interface IManualCircuitBreaker : ICircuitBreaker
{
    // Redeclared (not just inherited) so a class implementing both IManualCircuitBreaker and
    // IAutomaticCircuitBreaker can give each its own explicit-interface-implementation body —
    // without this redeclaration, ICircuitBreaker.IsTripped()/StateMessage() would be a
    // single shared member slot both derived interfaces point at, and one implementation
    // couldn't mean two different things (mutating-breaker state vs. orientation-breaker state)
    // at once. See ICircuitBreaker's XML doc on each of these for the general contract.
    new bool IsTripped();
    new string? StateMessage();
    /// <summary>Resets this breaker to its initial, untripped state.</summary>
    /// <remarks>Declared here rather than on <see cref="ICircuitBreaker"/> — resettability is
    /// specific to recoverable breakers. See <see cref="IUnrecoverableBreaker"/>.</remarks>
    void Reset();
    /// <summary>Returns the current breaker summary, or null if the breaker has not tripped.</summary>
    BatchResultSummary? CheckBreaker();
    /// <summary>Human-readable directive describing what a caller should do given the current breaker state.</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    string GetBreakerDirective();
    /// <summary>Severity tier of the current breaker state (e.g. "Caution", "Tripped").</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    string GetBreakerSeverity();
    /// <summary>Full breaker status report including streak, rate, and rollback-score counters.</summary>
    BreakerStatusReport GetBreakerStatus();
    /// <summary>Records the outcome of a batch operation, feeding the breaker's rate/streak thresholds.</summary>
    void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped);
}

/// <summary>Orientation breaker: trips after repeated zero-match SearchSolutionText calls, restricting the agent to a small orienting-tool allowlist. Auto-resets on a successful allowlisted call — no manual reset tool.</summary>
public interface IAutomaticCircuitBreaker : ICircuitBreaker
{
    // See IManualCircuitBreaker's redeclaration of the same two members for why this is needed.
    new bool IsTripped();
    new string? StateMessage();
    /// <summary>Resets this breaker to its initial, untripped state.</summary>
    void Reset();
    /// <summary>Records a SearchSolutionText outcome. matchCount is the tool's totalRecords.</summary>
    void RecordSearchOutcome(int matchCount);
}

/// <summary>
/// Server-integrity faults — currently a failed operation-blob write, which means a change landed
/// on disk with no undo record — that no agent action can clear. Cleared only by restarting the
/// server, after an operator investigates the logged diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has <em>no</em> Reset(), and cannot inherit one now that <see cref="ICircuitBreaker"/>
/// declares none: unresettability is a compile-time guarantee, not a convention. A warning alone
/// was judged insufficient — run 20260910-013550-398 shows an agent reading warnings and carrying
/// on for 60 turns — and reusing <see cref="IManualCircuitBreaker"/> was rejected because the
/// exposed <c>ResetMutationBreaker</c> tool would hand the agent a reset button for a server defect,
/// and because that breaker's semantics are batch-failure rate, not server integrity.
/// </para>
/// <para>
/// Derives from <see cref="ICircuitBreaker"/> so the call-tool filter can consult any breaker
/// through one type. No MCP tool reads or clears this one.
/// </para>
/// </remarks>
public interface IUnrecoverableBreaker : ICircuitBreaker
{
    // See IManualCircuitBreaker's redeclaration for why these are re-declared rather than inherited.
    new bool IsTripped();
    new string? StateMessage();

    /// <summary>
    /// Records an unrecoverable server-integrity fault and halts all further mutating tools for the
    /// life of the process. Idempotent: the first trip's detail is kept, since it is the one closest
    /// to the root cause.
    /// </summary>
    /// <param name="toolName">The tool whose apply produced the fault.</param>
    /// <param name="changeId">The changeId that was returned but has no resolvable blob.</param>
    /// <param name="diagnostic">Why the blob write failed. Included verbatim in the halt message.</param>
    void Trip(string toolName, string changeId, string diagnostic);
}
