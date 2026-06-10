namespace RoslynSentinel.Common;

/// <summary>Snapshot of circuit breaker state returned by get_breaker_status.</summary>
public record BreakerStatusReport(
    bool Open,
    string Severity,
    string Directive,
    int ConsecutiveFailureStreak,
    int TotalAttempts,
    int TotalFailures,
    double FailureRatePct,
    int WeightedRollbackScore,
    int StreakTripThreshold,
    int RollbackScoreTripThreshold,
    double RateTripThresholdPct,
    int RateMinAttempts
);

