namespace RoslynSentinel.Common;

/// <summary>Tracks batch-operation failure rates and halts mutating tools after repeated failures.</summary>
public interface ICircuitBreaker
{
    /// <summary>Returns the current breaker summary, or null if the breaker has not tripped.</summary>
    BatchResultSummary? CheckBreaker();
    /// <summary>Human-readable directive describing what a caller should do given the current breaker state.</summary>
    string GetBreakerDirective();
    /// <summary>Severity tier of the current breaker state (e.g. "Caution", "Tripped").</summary>
    string GetBreakerSeverity();
    /// <summary>Full breaker status report including streak, rate, and rollback-score counters.</summary>
    BreakerStatusReport GetBreakerStatus();
    /// <summary>Records the outcome of a batch operation, feeding the breaker's rate/streak thresholds.</summary>
    void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped);
    /// <summary>Resets the breaker to its initial, untripped state.</summary>
    void ResetBreaker();
}
