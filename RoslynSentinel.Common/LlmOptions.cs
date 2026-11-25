namespace RoslynSentinel.Common;

/// <summary>
/// Resolved LLM configuration for <see cref="LmStudioClient"/> and <c>CommentingEngine</c>.
/// Populated once at startup via <see cref="Configure"/>: each setting prefers its
/// <c>--llm-*</c> command-line argument, falling back to the matching
/// <c>ROSLYNSENTINEL_LLM_*</c> environment variable, then a built-in default (or null/throw
/// for the model, which has no sensible default). Read-only after startup.
/// </summary>
[StartupSettingGroup("LLM", PostConfigure = "ApplyHostSamplerFold")]
public static class LlmOptions
{
    public static readonly StartupSetting<string?> BaseUrlSetting = StartupSetting.String("--llm-base-url", "ROSLYNSENTINEL_LLM_BASE_URL", "http://localhost:1234/v1", "LM Studio base URL (OpenAI-compatible /v1 root).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<string?> ModelSetting = StartupSetting.String("--llm-model", "ROSLYNSENTINEL_LLM_MODEL", null, "Model id sent to the LLM host; no default.", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<int> TimeoutSecondsSetting = StartupSetting.Int("--llm-timeout-seconds", "ROSLYNSENTINEL_LLM_TIMEOUT_SECONDS", 30, "HTTP timeout for LLM calls, in seconds.", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<int> ParallelismSetting = StartupSetting.Int("--llm-parallelism", "ROSLYNSENTINEL_LLM_PARALLELISM", 2, "Maximum concurrent LLM calls (minimum 1).", SettingScope.Server | SettingScope.PlanStepRunner, minValue: 1);

    public static readonly StartupSetting<int> StreamIdleTimeoutSecondsSetting = StartupSetting.Int("--llm-stream-idle-timeout-seconds", "ROSLYNSENTINEL_LLM_STREAM_IDLE_TIMEOUT_SECONDS", 120, "Maximum idle gap between streamed SSE events, in seconds (minimum 1).", SettingScope.Server | SettingScope.PlanStepRunner, minValue: 1);

    public static readonly StartupSetting<bool> MinimalToolSchemaSetting = StartupSetting.Bool("--llm-minimal-tools", "ROSLYNSENTINEL_LLM_MINIMAL_TOOLS", false, "Narrow the MCP tools/list schema for model-eval hosts that support it.", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<double?> TemperatureSetting = StartupSetting.NullableDouble("--llm-temperature", "ROSLYNSENTINEL_LLM_TEMPERATURE", "Sampling temperature sent per request (omitted when unset).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<double?> TopPSetting = StartupSetting.NullableDouble("--llm-top-p", "ROSLYNSENTINEL_LLM_TOP_P", "Nucleus sampling top-p (omitted when unset).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<double?> TopKSetting = StartupSetting.NullableDouble("--llm-top-k", "ROSLYNSENTINEL_LLM_TOP_K", "Top-k sampling value (UI-only; see TopK remarks).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<double?> RepeatPenaltySetting = StartupSetting.NullableDouble("--llm-repeat-penalty", "ROSLYNSENTINEL_LLM_REPEAT_PENALTY", "Repeat penalty value (UI-only; see TopK remarks).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<double?> MinPSetting = StartupSetting.NullableDouble("--llm-min-p", "ROSLYNSENTINEL_LLM_MIN_P", "Min-p sampling value (UI-only; see TopK remarks).", SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<IReadOnlyDictionary<string, double>> HostSamplerSetting = StartupSetting.Custom<IReadOnlyDictionary<string, double>>(
        "--llm-host-sampler",
        "ROSLYNSENTINEL_LLM_HOST_SAMPLER",
        new Dictionary<string, double>(),
        "(none)",
        (string raw, out IReadOnlyDictionary<string, double> value) =>
        {
            value = ParseHostSampler(raw);
            return true;
        },
        "name=value pairs separated by ';'",
        "Declared host sampler values as name=value pairs.",
        SettingScope.Server | SettingScope.PlanStepRunner);

    public static readonly StartupSetting<bool> AcceptUnverifiedSamplerSetting = StartupSetting.Bool("--llm-accept-unverified-sampler", "ROSLYNSENTINEL_LLM_ACCEPT_UNVERIFIED_SAMPLER", false, "Accept that declared sampler values cannot be verified from host responses.", SettingScope.Server | SettingScope.PlanStepRunner);

    public static string BaseUrl => BaseUrlSetting.Value ?? "http://localhost:1234/v1";
    public static string? Model => ModelSetting.Value;
    public static int TimeoutSeconds => TimeoutSecondsSetting.Value;
    public static int Parallelism => ParallelismSetting.Value;

    /// <summary>
    /// Longest gap allowed between consecutive SSE events on a streaming <c>/v1/responses</c> call
    /// before <c>LmStudioAgentClient</c> treats the connection as dead and aborts it (see
    /// <c>project_lmstudio_streaming_json_truncation_investigation</c> and the two observed cases
    /// of LM Studio going silent mid-stream without closing the connection or logging an error).
    /// This measures idle time between bytes, not total call duration, so a model that is genuinely
    /// still emitting slowly (e.g. a long reasoning burst) is not penalized -> only a stream that
    /// stops producing anything at all trips it. Deliberately separate from
    /// <see cref="TimeoutSeconds"/>/<c>HttpClient.Timeout</c>, which -> once headers are read for a
    /// streamed response -> no longer bounds the time spent reading the body.
    /// </summary>
    public static int StreamIdleTimeoutSeconds => StreamIdleTimeoutSecondsSetting.Value;

    /// <summary>
    /// When true, model-eval test hosts that support it narrow the MCP <c>tools/list</c> schema
    /// down to a small hand-picked allow-list instead of exposing every tool the active modes
    /// would otherwise register -> see <c>project_granite42_8b_tool_schema_size_isolated</c>: a
    /// 48-tool schema alone (independent of context growth, task ambiguity, or temperature) costs
    /// ~15x the latency of a 2-tool schema on the same trivial prompt for granite-4.2-8b. Opt-in
    /// per run via <c>--llm-minimal-tools</c>/<c>ROSLYNSENTINEL_LLM_MINIMAL_TOOLS</c> rather than
    /// a permanent mode change, since the allow-list is derived from observed real tool usage on
    /// qwen3.5-9b-coder transcripts and hasn't been validated as sufficient for every model/task.
    /// </summary>
    public static bool MinimalToolSchema => MinimalToolSchemaSetting.Value;

    /// <summary>
    /// Sampling temperature sent on every <c>/v1/responses</c> request, or null to omit the field
    /// entirely (LM Studio then applies its own default -> confirmed 1.0 via the response's own
    /// echoed value, not necessarily whatever's shown in LM Studio's UI sampling panel for the
    /// loaded model). Confirmed real (not a silent no-op) 2026-09-05: LM Studio echoes back
    /// whatever <c>temperature</c> value is sent in the same response body field, and a same-prompt
    /// A/B test showed genuinely different generations at 0.0 vs default. See
    /// <see cref="TopK"/>/<see cref="RepeatPenalty"/>/<see cref="MinP"/> for the three sampling
    /// params that do NOT work this way. Temperature and <see cref="TopP"/> are the only sampler values sent per
    /// request; no test sets them itself, so a model-eval run uses what the env vars (or LM Studio) give it.
    /// </summary>
    public static double? Temperature => TemperatureSetting.Value;

    /// <summary>Nucleus sampling top-p, or null to omit. Confirmed real the same way as <see cref="Temperature"/> (echoed back, and is a standard OpenAI-compatible field).</summary>
    public static double? TopP => TopPSetting.Value;

    /// <summary>
    /// Top-k sampling. UI-ONLY as far as this harness can tell: LM Studio's own docs list
    /// <c>top_k</c> as a valid <c>/v1/chat/completions</c> field, but a 2026-09-05 test against
    /// this harness's actual endpoint, <c>/v1/responses</c>, showed the field is silently absent
    /// from the echoed response body, and a same-prompt/same-temperature A/B test at very different
    /// <c>repeat_penalty</c> values (see <see cref="RepeatPenalty"/>) produced byte-identical
    /// generations -> strong evidence <c>/v1/responses</c> drops these three fields rather than
    /// forwarding them to the sampler. NOT wired into <c>LmStudioAgentClient</c>'s request
    /// body for that reason (a param that looks configured but silently does nothing is worse than
    /// no param at all) -> exposed here only so the modeleval script can print a reminder to set it
    /// by hand in LM Studio's sampling panel instead.
    /// </summary>
    public static double? TopK => TopKSetting.Value;

    /// <summary>UI-ONLY -> see <see cref="TopK"/>'s remarks; this is the param the 2026-09-05 A/B test (1.0 vs 1.8, byte-identical output) was run against directly.</summary>
    public static double? RepeatPenalty => RepeatPenaltySetting.Value;

    /// <summary>UI-ONLY -> see <see cref="TopK"/>'s remarks. Also not in LM Studio's own documented parameter list for either endpoint, unlike top_k/repeat_penalty.</summary>
    public static double? MinP => MinPSetting.Value;

    /// <summary>
    /// Sampler values the operator declares the model host applies but cannot be verified from its responses, read from
    /// --llm-host-sampler / ROSLYNSENTINEL_LLM_HOST_SAMPLER as "topK=20;minP=0.1;repeatPenalty=1.0;presencePenalty=0"; keys are canonical camelCase names.
    /// </summary>
    public static IReadOnlyDictionary<string, double> HostSampler { get; private set; } = new Dictionary<string, double>();

    /// <summary>
    /// True when the operator accepted that the declared sampler values (HostSampler, TopK, RepeatPenalty, MinP) cannot be verified from the host's responses; read from --llm-accept-unverified-sampler / ROSLYNSENTINEL_LLM_ACCEPT_UNVERIFIED_SAMPLER ('true' or '1'); recorded in the transcript's Settings block.
    /// </summary>
    public static bool AcceptUnverifiedSampler => AcceptUnverifiedSamplerSetting.Value;

    /// <summary>
    /// Parses the --llm-host-sampler format into canonical keys (topK, minP, repeatPenalty, presencePenalty, frequencyPenalty).
    /// Unknown keys and malformed pairs are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ParseHostSampler(string? raw)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var segment in raw.Split(';'))
        {
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = segment[..equals].Trim();
            string? canonical = null;
            foreach (var name in new[] { "topK", "minP", "repeatPenalty", "presencePenalty", "frequencyPenalty" })
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = name;
                    break;
                }
            }

            if (canonical is null
                || !double.TryParse(segment[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result[canonical] = value;
        }

        return result;
    }

    /// <summary>Parses --llm-* args (falling back to ROSLYNSENTINEL_LLM_* env vars) into the static properties above. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(LlmOptions), args);
    }

    /// <summary>PostConfigure hook for the LLM group: folds the older single-value sampler options into HostSampler. An explicit host-sampler entry wins on a clash.</summary>
    private static IReadOnlyList<ConfigurationProblem> ApplyHostSamplerFold()
    {
        var hostSampler = new Dictionary<string, double>(HostSamplerSetting.Value);
        // The older single-value options declare the same host-side values; fold them in so the transcript records one
        // declared set. An explicit host-sampler entry wins on a clash.
        if (TopK is { } topK) hostSampler.TryAdd("topK", topK);
        if (MinP is { } minP) hostSampler.TryAdd("minP", minP);
        if (RepeatPenalty is { } repeatPenalty) hostSampler.TryAdd("repeatPenalty", repeatPenalty);
        HostSampler = hostSampler;
        return Array.Empty<ConfigurationProblem>();
    }
}
