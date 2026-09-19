using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Tracks batch-operation failure rates and halts mutating tools after repeated failures. Manual
/// reset only - never auto-reset by design.
/// </summary>
public class MutationCircuitBreaker : IManualCircuitBreaker
{
    private readonly ILogger _logger;

    public MutationCircuitBreaker(ILogger logger)
    {
        _logger = logger;
    }

    // Thresholds -> start generous; tighten on observed session data.
    private const int BreakerStreakThreshold = 8;     // consecutive batches with zero successes
    private const int BreakerRateMinAttempts = 20;    // min attempts before rate-trip fires
    private const double BreakerRateThreshold = 0.30;  // >30% failure rate -> halt
    private const int BreakerRollbackScoreThreshold = 20;    // weighted score (rollback=2, fail=1)
    private const int CautionStreakThreshold = 4;
    private const int CautionRateMinAttempts = 10;
    private const double CautionRateThreshold = 0.15;
    private const int CautionRollbackScoreThreshold = 10;

    private readonly Lock _breakerLock = new();
    private bool _breakerOpen;
    private int _consecutiveFailureStreak;
    private int _totalAttempts;
    private int _totalFailures;
    private int _weightedRollbackScore;

    /// <summary>
    /// Records the outcome of a batch operation and advances the circuit breaker state.
    /// Call once per batch-first mutation tool after work completes.
    /// Rollbacks are weighted 2x against plain failures (skips are benign and do not advance the streak).
    /// </summary>
    public void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped)
    {
        lock (_breakerLock)
        {
            _totalAttempts += succeeded + failed + rolledBack + skipped;
            _totalFailures += failed + rolledBack;
            _weightedRollbackScore += (rolledBack * 2) + failed;

            if (succeeded > 0)
            {
                _consecutiveFailureStreak = 0;
            }
            else if (failed + rolledBack > 0)
            {
                _consecutiveFailureStreak++;
            }
            // skips only -> streak unchanged

            if (!_breakerOpen)
            {
                double failureRate = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts : 0;
                bool streakTrip = _consecutiveFailureStreak >= BreakerStreakThreshold;
                bool rateTrip = _totalAttempts >= BreakerRateMinAttempts && failureRate > BreakerRateThreshold;
                bool rollbackTrip = _weightedRollbackScore > BreakerRollbackScoreThreshold;

                if (streakTrip || rateTrip || rollbackTrip)
                {
                    _breakerOpen = true;
                    _logger.LogWarning(
                        "Circuit breaker TRIPPED. streak={Streak}, attempts={Attempts}, " +
                        "failureRate={Rate:P1}, rollbackScore={Score}",
                        _consecutiveFailureStreak, _totalAttempts, failureRate, _weightedRollbackScore);
                }
            }
        }
    }

    /// <summary>
    /// Returns a halt BatchResultSummary if the breaker is open (call at the top of every mutating tool).
    /// Returns null when tools may proceed.
    /// </summary>
    public BatchResultSummary? CheckBreaker()
    {
        lock (_breakerLock)
        {
            if (!_breakerOpen)
            {
                return null;
            }

            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;

            return new BatchResultSummary
            {
                ChangeId = "",
                BlobName = "",
                Severity = "halt",
                BreakerOpen = true,
                Directive = $"Circuit breaker open. All mutating tools disabled until reset_breaker is called by the user. " +
                              $"(streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                              $"attempts={_totalAttempts}, " +
                              $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                              $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold})",
            };
        }
    }

    /// <summary>
    /// Clears all circuit breaker state and re-enables mutating tools.
    /// Manual only -> never auto-reset by design.
    /// </summary>
    public void Reset()
    {
        lock (_breakerLock)
        {
            _breakerOpen = false;
            _consecutiveFailureStreak = 0;
            _totalAttempts = 0;
            _totalFailures = 0;
            _weightedRollbackScore = 0;
        }

        _logger.LogInformation("Circuit breaker manually reset.");
    }

    /// <summary>True when the mutating-tools breaker is currently open.</summary>
    public bool IsTripped()
    {
        lock (_breakerLock)
        {
            return _breakerOpen;
        }
    }

    /// <summary>Same directive text as CheckBreaker()/GetBreakerStatus(); null when not tripped.</summary>
    public string? StateMessage()
    {
        lock (_breakerLock)
        {
            if (!_breakerOpen)
            {
                return null;
            }

            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            return ComputeDirectiveUnlocked("halt", failureRatePct);
        }
    }

    /// <summary>Returns the current severity tier for inclusion in BatchResultSummary.</summary>
    public string GetBreakerSeverity()
    {
        lock (_breakerLock)
        {
            return ComputeSeverityUnlocked();
        }
    }

    /// <summary>Returns the human-readable directive for inclusion in BatchResultSummary.</summary>
    public string GetBreakerDirective()
    {
        lock (_breakerLock)
        {
            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            return ComputeDirectiveUnlocked(ComputeSeverityUnlocked(), failureRatePct);
        }
    }

    /// <summary>Returns a full snapshot of circuit breaker state for the get_breaker_status tool.</summary>
    public BreakerStatusReport GetBreakerStatus()
    {
        lock (_breakerLock)
        {
            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            string severity = ComputeSeverityUnlocked();
            string directive = ComputeDirectiveUnlocked(severity, failureRatePct);

            return new BreakerStatusReport(
                Open: _breakerOpen,
                Severity: severity,
                Directive: directive,
                DirectiveKind: _breakerOpen ? DirectiveKind.ReviewRequired : DirectiveKind.Proceed,
                ConsecutiveFailureStreak: _consecutiveFailureStreak,
                TotalAttempts: _totalAttempts,
                TotalFailures: _totalFailures,
                FailureRatePct: Math.Round(failureRatePct, 1),
                WeightedRollbackScore: _weightedRollbackScore,
                StreakTripThreshold: BreakerStreakThreshold,
                RollbackScoreTripThreshold: BreakerRollbackScoreThreshold,
                RateTripThresholdPct: BreakerRateThreshold * 100,
                RateMinAttempts: BreakerRateMinAttempts
            );
        }
    }

    private string ComputeSeverityUnlocked()
    {
        if (_breakerOpen)
        {
            return "halt";
        }

        double failureRate = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts : 0;
        bool caution = _consecutiveFailureStreak >= CautionStreakThreshold
                          || (_totalAttempts >= CautionRateMinAttempts && failureRate >= CautionRateThreshold)
                          || _weightedRollbackScore >= CautionRollbackScoreThreshold;

        return caution ? "caution" : "ok";
    }

    private string ComputeDirectiveUnlocked(string severity, double failureRatePct)
    {
        return severity switch
        {
            "halt" => $"Circuit breaker open. All mutating tools disabled until reset_breaker is called by the user. " +
                      $"(streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                      $"attempts={_totalAttempts}, " +
                      $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                      $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold})",
            "caution" => $"Elevated failure indicators - proceeding but monitor for trip. " +
                         $"streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                         $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                         $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold}.",
            _ => "Operating within normal failure tolerance.",
        };
    }
}
