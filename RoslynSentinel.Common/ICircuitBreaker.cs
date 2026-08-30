namespace RoslynSentinel.Common;

/// <summary>Minimal shape shared by any breaker: whether it's currently blocking callers, why, and how to clear it.</summary>
public interface ICircuitBreaker
{
    /// <summary>True when this breaker is currently blocking the behavior it guards.</summary>
    bool IsTripped();
    /// <summary>Human-readable detail about the current state (counters, directive, etc.). Null/empty when not tripped.</summary>
    string? StateMessage();
    /// <summary>Resets this breaker to its initial, untripped state.</summary>
    void Reset();
}

/// <summary>Tracks batch-operation failure rates and halts mutating tools after repeated failures. Manual reset only — never auto-reset by design.</summary>
public interface IManualCircuitBreaker : ICircuitBreaker
{
    // Redeclared (not just inherited) so a class implementing both IManualCircuitBreaker and
    // IAutomaticCircuitBreaker can give each its own explicit-interface-implementation body —
    // without this redeclaration, ICircuitBreaker.IsTripped()/StateMessage()/Reset() would be a
    // single shared member slot both derived interfaces point at, and one implementation
    // couldn't mean two different things (mutating-breaker state vs. orientation-breaker state)
    // at once. See ICircuitBreaker's XML doc on each of these for the general contract.
    new bool IsTripped();
    new string? StateMessage();
    new void Reset();
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
    // See IManualCircuitBreaker's redeclaration of the same three members for why this is needed.
    new bool IsTripped();
    new string? StateMessage();
    new void Reset();
    /// <summary>Records a SearchSolutionText outcome. matchCount is the tool's totalRecords.</summary>
    void RecordSearchOutcome(int matchCount);
}
