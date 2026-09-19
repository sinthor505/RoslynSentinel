using System.Collections.Concurrent;
using System.Text.Json;

namespace RoslynSentinel.Common;

public class ToolCallRateLimiter : IRateLimiter
{
    // Per-tool sliding-window rate limiter: maps tool name -> timestamps of recent calls.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _rateLimitWindows = new();
    private static readonly Dictionary<string, int> DefaultRateLimits = LoadRateLimits();

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Sliding-window rate limiter for MCP tool calls.
    /// Returns <c>null</c> if the call is within the allowed rate, or a diagnostic error
    /// message if the limit is exceeded. The caller should return that message as an error.
    /// </summary>
    /// <param name="toolName">The MCP tool name (used as the per-tool counter key).</param>
    /// <param name="defaultLimit">Calls-per-minute limit to use when no override is configured.</param>
    public string? CheckRateLimit(string toolName, int defaultLimit)
    {
        const int WindowSeconds = 60;
        long windowTicks = TimeSpan.FromSeconds(WindowSeconds).Ticks;
        long now = DateTime.UtcNow.Ticks;
        long cutoff = now - windowTicks;

        int limit = DefaultRateLimits.TryGetValue(toolName, out int configured)
            ? configured
            : defaultLimit;

        var queue = _rateLimitWindows.GetOrAdd(toolName, _ => new ConcurrentQueue<long>());

        // Drain expired entries from the front.
        while (queue.TryPeek(out long oldest) && oldest < cutoff)
        {
            queue.TryDequeue(out _);
        }

        int count = queue.Count;
        if (count >= limit)
        {
            return $"Rate limit: '{toolName}' called {count} times in {WindowSeconds}s (limit {limit}). "
                 + "This usually indicates a retry loop or thrashing. Stop, assess what is failing, "
                 + "and either fix the root cause or - if this is legitimate high-volume work - "
                 + "propose a batch tool that accomplishes it in fewer calls.";
        }

        queue.Enqueue(now);
        return null;
    }

    private static Dictionary<string, int> LoadRateLimits()
    {
        // Defaults from spec (calls per 60-second sliding window).
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["list_project_documentation"] = 20,
            ["read_project_documentation"] = 30,
            ["update_project_documentation"] = 10,
            ["read_plan"] = 30,
            ["update_plan"] = 10,
            ["read_handoff"] = 30,
            ["write_handoff"] = 10,
            ["read_completed_work"] = 30,
            ["append_completed_work"] = 15,
            ["read_current_state"] = 30,
            ["update_current_state"] = 5,
            ["run_bridge_batch"] = 5,
            ["run_uplift_batch"] = 5,
            ["propagate_cancellation_token_batch"] = 5,
        };

        // Optional override file: rate-limits.json next to the server binary.
        try
        {
            var overridePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rate-limits.json");
            if (File.Exists(overridePath))
            {
                var json = File.ReadAllText(overridePath);
                var overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(json,
                    _jsonOptions);
                if (overrides is not null)
                {
                    foreach (var (key, value) in overrides)
                    {
                        defaults[key] = value;
                    }
                }
            }
        }
        catch { /* best effort - bad JSON in the override file does not crash the server */ }

        return defaults;
    }
}
