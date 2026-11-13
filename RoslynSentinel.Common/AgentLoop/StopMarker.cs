using System.Text.RegularExpressions;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Detects the BLOCKED: stop marker in a model's final message. Pure functions, no state.
/// </summary>
public static class StopMarker
{
    // An optional markdown heading prefix (e.g. "# " or "## ") and markdown emphasis (*, _, backtick) may precede and wrap the marker, e.g. "## **BLOCKED:**" or "`BLOCKED:`".
    private static readonly Regex LeadingMarkerRegex = new(
        @"^\s*(?:#+\s*)?[*_`]*BLOCKED[*_`]*:",
        RegexOptions.CultureInvariant);

    /// <summary>True when the BLOCKED: marker starts the message (leading whitespace, optional markdown heading prefix and emphasis ignored). Without RegexOptions.Multiline, the anchor is the start of the whole string, so a marker on a later line does not count.</summary>
    /// <param name="message">The message to test.</param>
    public static bool IsLeading(string message) => LeadingMarkerRegex.IsMatch(message);

    /// <summary>True when the BLOCKED: marker appears anywhere in the message, not only as its first line. Case sensitive.</summary>
    /// <param name="message">The message to test.</param>
    public static bool ContainsAnywhere(string message) => message.Contains("BLOCKED:", StringComparison.Ordinal);
}
