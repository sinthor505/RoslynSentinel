namespace RoslynSentinel.Common;

/// <summary>Per-tool call-rate limiting.</summary>
public interface IRateLimiter
{
    /// <summary>Returns a rate-limit rejection message if toolName has exceeded defaultLimit, otherwise null.</summary>
    string? CheckRateLimit(string toolName, int defaultLimit);
}
