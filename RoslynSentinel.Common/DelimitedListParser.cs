using System.Text.Json;

namespace RoslynSentinel.Common;

public static class DelimitedListParser
{
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Parses a caller-supplied "list of strings" parameter that may arrive as either a plain
    /// comma-separated string ("a.cs,b.cs") or a JSON array literal textified into the same string
    /// ("[\"a.cs\", \"b.cs\"]") - the shape a caller reaches for out of habit when other params on
    /// the same tool surface use real JSON arrays for multi-item values. Shape is detected purely
    /// by whether the trimmed input starts with '[' and ends with ']'; once committed to that
    /// shape, malformed JSON is reported via <paramref name="error"/> instead of silently falling
    /// through to a comma split that would mangle it into one bogus token (see
    /// blocking_error_git_stage_jsonarray_string_pathspec_failure.md).
    /// </summary>
    /// <returns>
    /// The parsed, trimmed, non-empty items (possibly empty if <paramref name="input"/> is
    /// null/whitespace), or null if <paramref name="error"/> is set.
    /// </returns>
    public static string[]? ParseStringOrJsonArrayToList(string? input, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var trimmed = input.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        try
        {
            var items = JsonSerializer.Deserialize<string[]>(trimmed);
            return items?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray() ?? [];
        }
        catch (JsonException ex)
        {
            error = $"'{input}' looks like a JSON array but failed to parse as one ({ex.Message}). " +
                    "Use a JSON array of strings (e.g. [\"a.cs\",\"b.cs\"]) or a plain comma-separated string (e.g. \"a.cs,b.cs\").";
            return null;
        }
    }
}
