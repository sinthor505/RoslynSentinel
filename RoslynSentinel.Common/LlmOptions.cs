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

    /// <summary>
    /// When true, model-eval test hosts that support it narrow the MCP <c>tools/list</c> schema
    /// down to a small hand-picked allow-list instead of exposing every tool the active modes
    /// would otherwise register — see <c>project_granite42_8b_tool_schema_size_isolated</c>: a
    /// 48-tool schema alone (independent of context growth, task ambiguity, or temperature) costs
    /// ~15x the latency of a 2-tool schema on the same trivial prompt for granite-4.2-8b. Opt-in
    /// per run via <c>--llm-minimal-tools</c>/<c>ROSLYNSENTINEL_LLM_MINIMAL_TOOLS</c> rather than
    /// a permanent mode change, since the allow-list is derived from observed real tool usage on
    /// qwen3.5-9b-coder transcripts and hasn't been validated as sufficient for every model/task.
    /// </summary>
    public static bool MinimalToolSchema { get; private set; }

    /// <summary>
    /// Sampling temperature sent on every <c>/v1/responses</c> request, or null to omit the field
    /// entirely (LM Studio then applies its own default — confirmed 1.0 via the response's own
    /// echoed value, not necessarily whatever's shown in LM Studio's UI sampling panel for the
    /// loaded model). Confirmed real (not a silent no-op) 2026-09-05: LM Studio echoes back
    /// whatever <c>temperature</c> value is sent in the same response body field, and a same-prompt
    /// A/B test showed genuinely different generations at 0.0 vs default. See
    /// <see cref="TopK"/>/<see cref="RepeatPenalty"/>/<see cref="MinP"/> for the three sampling
    /// params that do NOT work this way.
    /// </summary>
    public static double? Temperature { get; private set; }

    /// <summary>Nucleus sampling top-p, or null to omit. Confirmed real the same way as <see cref="Temperature"/> (echoed back, and is a standard OpenAI-compatible field).</summary>
    public static double? TopP { get; private set; }

    /// <summary>
    /// Top-k sampling. UI-ONLY as far as this harness can tell: LM Studio's own docs list
    /// <c>top_k</c> as a valid <c>/v1/chat/completions</c> field, but a 2026-09-05 test against
    /// this harness's actual endpoint, <c>/v1/responses</c>, showed the field is silently absent
    /// from the echoed response body, and a same-prompt/same-temperature A/B test at very different
    /// <c>repeat_penalty</c> values (see <see cref="RepeatPenalty"/>) produced byte-identical
    /// generations — strong evidence <c>/v1/responses</c> drops these three fields rather than
    /// forwarding them to the sampler. NOT wired into <see cref="LmStudioAgentClient"/>'s request
    /// body for that reason (a param that looks configured but silently does nothing is worse than
    /// no param at all) — exposed here only so the modeleval script can print a reminder to set it
    /// by hand in LM Studio's sampling panel instead.
    /// </summary>
    public static double? TopK { get; private set; }

    /// <summary>UI-ONLY — see <see cref="TopK"/>'s remarks; this is the param the 2026-09-05 A/B test (1.0 vs 1.8, byte-identical output) was run against directly.</summary>
    public static double? RepeatPenalty { get; private set; }

    /// <summary>UI-ONLY — see <see cref="TopK"/>'s remarks. Also not in LM Studio's own documented parameter list for either endpoint, unlike top_k/repeat_penalty.</summary>
    public static double? MinP { get; private set; }

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

        var minimalToolsRaw = GetArgValue(args, "--llm-minimal-tools")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_MINIMAL_TOOLS");
        MinimalToolSchema = string.Equals(minimalToolsRaw, "true", StringComparison.OrdinalIgnoreCase)
            || minimalToolsRaw == "1";

        Temperature = ParseNullableDouble(GetArgValue(args, "--llm-temperature")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_TEMPERATURE"));
        TopP = ParseNullableDouble(GetArgValue(args, "--llm-top-p")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_TOP_P"));
        TopK = ParseNullableDouble(GetArgValue(args, "--llm-top-k")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_TOP_K"));
        RepeatPenalty = ParseNullableDouble(GetArgValue(args, "--llm-repeat-penalty")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_REPEAT_PENALTY"));
        MinP = ParseNullableDouble(GetArgValue(args, "--llm-min-p")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LLM_MIN_P"));
    }

    private static double? ParseNullableDouble(string? raw) =>
        double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

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
