namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Selects the <see cref="ToolResultFormat"/> the agent loop shows tool results in. Default
/// <see cref="ToolResultFormat.Plain"/> (key: value text, no escaping); <c>json</c> selects the wire text unchanged.
/// </summary>
[StartupSettingGroup("Tool result format (ModelEval)")]
public static class ToolResultFormatOptions
{
    public const string FormatVariable = "ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT";

    /// <summary>Tool result format (<see cref="FormatVariable"/>), re-read on every access.</summary>
    public static readonly StartupSetting<ToolResultFormat> FormatSetting = StartupSetting.Custom<ToolResultFormat>(
        null,
        FormatVariable,
        ToolResultFormat.Plain,
        "plain",
        (string raw, out ToolResultFormat value) =>
        {
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "json", StringComparison.OrdinalIgnoreCase))
            {
                value = ToolResultFormat.Json;
                return true;
            }

            if (string.Equals(trimmed, "plain", StringComparison.OrdinalIgnoreCase))
            {
                value = ToolResultFormat.Plain;
                return true;
            }

            if (string.Equals(trimmed, "xml", StringComparison.OrdinalIgnoreCase))
            {
                value = ToolResultFormat.Xml;
                return true;
            }

            value = ToolResultFormat.Json;
            return false;
        },
        "json|plain|xml",
        "Format of tool results shown to the model in the agent loop: json (the wire text), plain (key: value text) or xml (raw tags); no escaping inside values in plain and xml.",
        SettingScope.AgentLoop,
        SettingKind.Live);

    /// <param name="readVariable">Environment reader; defaults to the process environment (a seam for tests).</param>
    /// <exception cref="InvalidOperationException">The variable holds a value that is not json, plain or xml.</exception>
    public static ToolResultFormat FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= StartupSettings.EnvironmentReader;
        var read = FormatSetting.Read(StartupArgs.Empty, readVariable);
        if (read.Problem is { } problem)
        {
            throw new InvalidOperationException(
                $"{FormatVariable} has the invalid value '{problem.RawValue}'; allowed values: {problem.Expected} (case-insensitive).");
        }

        return read.Value;
    }
}
