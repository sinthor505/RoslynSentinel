using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Heuristic for "the final message states that the task's target does not exist" (used by the ProseStop
/// failure class). True when the message names at least one of the target names (case-insensitive substring)
/// AND contains a not-found / does-not-exist phrase. Deliberately simple: it is a classifier for a rate
/// table, not a gate; a model that says the target is absent without naming it, or names it without a
/// listed phrase, is not counted.
/// </summary>
public static class StopAbsenceDetector
{
    // Apostrophes: ASCII ' or the right single quotation mark (U+2019).
    private static readonly Regex AbsencePhraseRegex = new(
        @"does(?:\s+not|n['’]t)\s+(?:appear\s+to\s+)?exist"
        + @"|not\s+found"
        + @"|no\s+such\s+(?:file|method|symbol)"
        + @"|(?:cannot|can['’]t|could\s+not|couldn['’]t|unable\s+to)\s+(?:be\s+)?(?:find|found|locate|located)"
        + @"|(?:is\s+not|isn['’]t)\s+(?:present|there)"
        + @"|is\s+missing"
        + @"|no\s+(?:matching|similar)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="finalMessage"/> names a target and states it is absent; false for null/empty inputs.</summary>
    public static bool StatesAbsence(string? finalMessage, IReadOnlyList<string>? targetNames)
    {
        if (string.IsNullOrWhiteSpace(finalMessage) || targetNames is null || targetNames.Count == 0)
        {
            return false;
        }

        var namesTarget = targetNames.Any(name =>
            !string.IsNullOrWhiteSpace(name) && finalMessage.Contains(name, StringComparison.OrdinalIgnoreCase));
        return namesTarget && AbsencePhraseRegex.IsMatch(finalMessage);
    }
}
