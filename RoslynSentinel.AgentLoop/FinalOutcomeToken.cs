using System.Text.RegularExpressions;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>The outcome token on the first or last non-blank line of a model's final message: DONE:, BLOCKED: or neither.</summary>
public enum FinalOutcome
{
    None,
    Done,
    Blocked
}

/// <summary>Reads the DONE:/BLOCKED: token that starts the first or last non-blank line of a final message. Pure functions, no state.</summary>
public static class FinalOutcomeToken
{
    // Mirrors StopMarker.LeadingMarkerRegex (StopMarker keeps its regex private; BLOCKED: itself is read through StopMarker.IsLeading):
    // an optional markdown heading prefix and *, _, backtick emphasis may precede and wrap the token.
    private static readonly Regex LeadingDoneRegex = new(
        @"^\s*(?:#+\s*)?[*_`]*DONE[*_`]*:",
        RegexOptions.CultureInvariant);

    /// <summary>Done or Blocked when a line starts with that token: the first non-blank line wins, otherwise the last line that starts with a token (report lines may follow it). A token inside a sentence does not count. Otherwise None (a null or blank message is None).</summary>
    public static FinalOutcome Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FinalOutcome.None;
        }

        if (StopMarker.IsLeading(message))
        {
            return FinalOutcome.Blocked;
        }

        if (LeadingDoneRegex.IsMatch(message))
        {
            return FinalOutcome.Done;
        }

        // Scan from the end so the last token line wins; models sometimes write the token line and then a few report lines after it.
        string[] lines = message.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (StopMarker.IsLeading(line))
            {
                return FinalOutcome.Blocked;
            }

            if (LeadingDoneRegex.IsMatch(line))
            {
                return FinalOutcome.Done;
            }
        }

        return FinalOutcome.None;
    }
}
