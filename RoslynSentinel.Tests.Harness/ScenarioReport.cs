using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Parses the Report block of a final message: lines after the DONE:/BLOCKED: line, form "label: value".</summary>
public static class ScenarioReport
{
    private static readonly char[] LeadingNoise = { '-', '*', '>', '`', '|', ' ', '\t' };
    private static readonly char[] LabelNoise = { '*', '_', '`', '|', ' ', '\t' };
    private static readonly char[] ValueNoise = { '`', '|', ' ', '\t' };

    /// <summary>
    /// Label to value (labels case-insensitive, first occurrence wins; split on the first ':' only, so a value keeps later colons).
    /// Reads the lines after the DONE:/BLOCKED: line; a message with no such token line is read from its first line (a missing token
    /// is graded by the token check, not here). Empty when there is no report.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? finalMessage)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(finalMessage))
        {
            return fields;
        }

        var lines = finalMessage.Split('\n');
        var start = 0;
        if (FinalOutcomeToken.Parse(finalMessage) != FinalOutcome.None)
        {
            start = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line)) + 1;
        }

        for (var index = start; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r').TrimStart(LeadingNoise);
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var label = line[..colon].Trim(LabelNoise);
            if (label.Length == 0)
            {
                continue;
            }

            fields.TryAdd(label, line[(colon + 1)..].Trim(ValueNoise));
        }

        return fields;
    }
}
