namespace RoslynSentinel.Common;

/// <summary>
/// Resolved LLM configuration for <see cref="LmStudioClient"/> and <c>CommentingEngine</c>.
/// Populated once at startup via <see cref="Configure"/>: each setting prefers its
/// <c>--llm-*</c> command-line argument, falling back to the matching
/// <c>ROSLYNSENTINEL_LLM_*</c> environment variable, then a built-in default (or null/throw
/// for the model, which has no sensible default). Read-only after startup.
/// </summary>
public static class LlmOptions
{
    public static string BaseUrl { get; private set; } = "http://localhost:1234/v1";
    public static string? Model { get; private set; }
    public static int TimeoutSeconds { get; private set; } = 30;
    public static int Parallelism { get; private set; } = 2;

    /// <summary>Parses --llm-* args (falling back to ROSLYNSENTINEL_LLM_* env vars) into the static properties above. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        BaseUrl = GetArgValue(args, "--llm-base-url")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_BASE_URL")
            ?? "http://localhost:1234/v1";

        Model = GetArgValue(args, "--llm-model")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_MODEL");

        var timeoutRaw = GetArgValue(args, "--llm-timeout-seconds")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_TIMEOUT_SECONDS");
        TimeoutSeconds = int.TryParse(timeoutRaw, out var parsedTimeout) ? parsedTimeout : 30;

        var parallelismRaw = GetArgValue(args, "--llm-parallelism")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_PARALLELISM");
        Parallelism = int.TryParse(parallelismRaw, out var parsedParallelism) && parsedParallelism > 0 ? parsedParallelism : 2;
    }

    /// <summary>Reads a command-line flag's value, accepting both "--flag=value" and "--flag value" forms.</summary>
    private static string? GetArgValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
