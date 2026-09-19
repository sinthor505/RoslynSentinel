using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

public class OrientationCircuitBreaker : IAutomaticCircuitBreaker
{
    private readonly ILogger _logger;

    public OrientationCircuitBreaker(ILogger logger)
    {
        _logger = logger;
    }

    private const int OrientationBreakerTripThreshold = 3;
    private readonly Lock _orientationBreakerLock = new();
    private bool _orientationBreakerOpen;
    private int _consecutiveZeroMatchSearches;

    /// <summary>
    /// Records a SearchSolutionText outcome; trips after OrientationBreakerTripThreshold
    /// consecutive zero-match calls. Returns true only on the call that flips the breaker open.
    /// </summary>
    public bool RecordSearchOutcome(int matchCount)
    {
        lock (_orientationBreakerLock)
        {
            if (matchCount > 0)
            {
                _consecutiveZeroMatchSearches = 0;
                return false;
            }

            _consecutiveZeroMatchSearches++;
            if (_consecutiveZeroMatchSearches >= OrientationBreakerTripThreshold && !_orientationBreakerOpen)
            {
                _orientationBreakerOpen = true;
                _logger.LogWarning(
                    "Orientation breaker TRIPPED after {Count} consecutive zero-match SearchSolutionText calls.",
                    _consecutiveZeroMatchSearches);
                return true;
            }

            return false;
        }
    }

    /// <summary>True when the orientation breaker is currently restricting tool calls to the orienting allowlist.</summary>
    public bool IsTripped()
    {
        lock (_orientationBreakerLock)
        {
            return _orientationBreakerOpen;
        }
    }

    /// <summary>Directive describing the orientation breaker's tripped state; null when not tripped.</summary>
    public string? StateMessage()
    {
        lock (_orientationBreakerLock)
        {
            if (!_orientationBreakerOpen)
            {
                return null;
            }

            return $"SearchSolutionText is DISABLED after {_consecutiveZeroMatchSearches} consecutive calls returned " +
                   "no matches. It will not run again until one of the tools below succeeds. You MUST call " +
                   "ListAll(kind: all) or ListSolutionItems(kind: all) now - browse the returned list for what " +
                   "you're looking for. GetFileOutline and ReadFile are also available once you have a real path " +
                   "from that list.";
        }
    }

    /// <summary>Clears the orientation breaker and its zero-match streak. Called automatically by the request filter -> no manual reset tool.</summary>
    public void Reset()
    {
        lock (_orientationBreakerLock)
        {
            _orientationBreakerOpen = false;
            _consecutiveZeroMatchSearches = 0;
        }
    }
}
