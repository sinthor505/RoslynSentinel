using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

public class OrientationCircuitBreaker : IAutomaticCircuitBreaker
{
    private readonly ILogger _logger;

    public OrientationCircuitBreaker(ILogger logger, int tripThreshold = 10)
    {
        _logger = logger;
        _orientationBreakerTripThreshold = tripThreshold;
    }

    private readonly int _orientationBreakerTripThreshold;
    private readonly Lock _orientationBreakerLock = new();
    private bool _orientationBreakerOpen;
    private int _consecutiveZeroMatchSearches;

    /// <summary>
    /// Records a SearchSolutionText outcome; trips after the configured trip threshold
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
            if (_consecutiveZeroMatchSearches >= _orientationBreakerTripThreshold && !_orientationBreakerOpen)
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
    public string? StateMessage() => StateMessage(null);

    /// <summary>
    /// Same directive, naming only tools the server can actually serve: <paramref name="isCallable"/> says whether a
    /// tool name is callable (null = every tool is, the text of a full surface). With neither ListAll nor
    /// ListSolutionItems callable the text carries no tool call to make, only the instruction to reuse a path
    /// already seen.
    /// </summary>
    public string? StateMessage(Func<string, bool>? isCallable)
    {
        lock (_orientationBreakerLock)
        {
            if (!_orientationBreakerOpen)
            {
                return null;
            }

            bool Callable(string tool) => isCallable is null || isCallable(tool);
            var browse = new List<string>();
            if (Callable("ListAll"))
            {
                browse.Add(ToolCallHint.Call("ListAll", ("kind", "all")));
            }

            if (Callable("ListSolutionItems"))
            {
                browse.Add(ToolCallHint.Call("ListSolutionItems", ("kind", "all")));
            }

            var follow = new List<string>();
            if (Callable("GetFileOutline"))
            {
                follow.Add("GetFileOutline");
            }

            if (Callable("ReadFile"))
            {
                follow.Add(ToolCallHint.Call("ReadFile"));
            }

            // hint-ok: names the disabled mode as a label, not a call to make
            var text = $"Search(mode: text) is DISABLED after {_consecutiveZeroMatchSearches} consecutive calls returned " +
                       "no matches. It will not run again until one of the tools below succeeds. ";
            text += browse.Count > 0
                ? $"You MUST call {string.Join(" or ", browse)} now - browse the returned list for what you're looking for. "
                : "No listing tool is available here: use a path you have already seen in an earlier result. ";
            if (follow.Count > 0)
            {
                text += $"{string.Join(" and ", follow)} {(follow.Count == 1 ? "is" : "are")} also available once you have a real path" +
                        (browse.Count > 0 ? " from that list." : ".");
            }

            return text.TrimEnd();
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
