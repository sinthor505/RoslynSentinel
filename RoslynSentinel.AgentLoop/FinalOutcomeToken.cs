using System.Text.RegularExpressions;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>The leading outcome token of a model's final message: DONE:, BLOCKED: or neither.</summary>
public enum FinalOutcome
{
    None,
    Done,
    Blocked
}

/// <summary>Reads the DONE:/BLOCKED: token that starts a final message. Pure functions, no state.</summary>
public static class FinalOutcomeToken
{
    // Mirrors StopMarker.LeadingMarkerRegex (StopMarker keeps its regex private; BLOCKED: itself is read through StopMarker.IsLeading):
    // an optional markdown heading prefix and *, _, backtick emphasis may precede and wrap the token.
    private static readonly Regex LeadingDoneRegex = new(
        @"^\s*(?:#+\s*)?[*_`]*DONE[*_`]*:",
        RegexOptions.CultureInvariant);

    /// <summary>Done or Blocked when the first non-blank line starts with that token; otherwise None (a null or blank message is None).</summary>
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

        return LeadingDoneRegex.IsMatch(message) ? FinalOutcome.Done : FinalOutcome.None;
    }
}
