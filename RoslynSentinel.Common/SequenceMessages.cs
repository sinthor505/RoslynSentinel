using System.Text.Json.Nodes;

namespace RoslynSentinel.Common;

/// <summary>The model-facing text of a sequence page (proposal_sequence_pager.md, Decided 5), one wording for the marshaller and GetLargeResult.</summary>
public static class SequenceMessages
{
    /// <summary>
    /// The model-facing text of one page of a sequence: what came back, out of how many, and the full next call. A line
    /// sequence names the line range, and the file line range when <c>successData.startLine</c> (or, absent that, <c>successData.envelope.returnedFromLine</c>) is present and no
    /// overlong line was chunked (a chunk is a unit, not a file line).
    /// </summary>
    /// <param name="full">The full wire envelope the sequence was resolved on.</param>
    /// <param name="sequence">The resolved sequence.</param>
    /// <param name="page">The page the message describes.</param>
    /// <param name="nextFetchCall">The complete call for the next page; null on the last page, which gets the "sequence complete" wording.</param>
    public static string ForPage(JsonObject full, SequenceDescriptor sequence, SequencePage page, string? nextFetchCall)
    {
        if (page.Returned == 0)
        {
            string units = sequence.Kind == SequenceKind.Lines ? "lines" : "records";
            return $"COMPLETE: the requested offset is at or past the end of the sequence ({page.Total} {units}); nothing is returned and there is nothing more to fetch.";
        }

        string prefix = nextFetchCall is null ? "COMPLETE:" : "INCOMPLETE:";
        string suffix = nextFetchCall is null
            ? " That is the end of the sequence; there is nothing more to fetch."
            : $" Call {nextFetchCall} for the next page.";
        int first = page.Offset + 1;
        int last = page.Offset + page.Returned;

        if (sequence.Kind != SequenceKind.Lines)
        {
            string what = page.Offset == 0 && nextFetchCall is not null
                ? $"{page.Returned} of {page.Total} records returned."
                : $"records {first}-{last} of {page.Total} returned.";
            return $"{prefix} {what}{suffix}";
        }

        string text = ResultPager.SelectNode(full, sequence.Paths[0]) is JsonValue value && value.TryGetValue(out string? s) ? s : string.Empty;
        bool chunked = ResultPager.SplitLines(text).Count != page.Total;
        string fileRange = string.Empty;
        // ReadFile reports startLine; GetMethodSource reports the same thing as envelope.returnedFromLine (the line its source begins on).
        var dataObject = full["successData"] as JsonObject;
        JsonNode? startNode = dataObject?["startLine"] ?? (dataObject?["envelope"] as JsonObject)?["returnedFromLine"];
        if (!chunked && page.Returned > 0 && startNode is JsonValue start && start.TryGetValue(out int startLine))
        {
            fileRange = $" (file lines {startLine + page.Offset}-{startLine + page.Offset + page.Returned - 1})";
        }

        string continues = chunked && nextFetchCall is not null
            ? $" A line longer than {sequence.MaxUnitChars} characters is split into several units, so a line can continue in the next page."
            : string.Empty;
        return $"{prefix} lines {first}-{last} of {page.Total} returned{fileRange}.{continues}{suffix}";
    }

    /// <summary>
    /// Removes the stored tool's own paging fields from the envelope copy of a sequence page that was cut, so the only paging
    /// instruction left is the sequence pager's (<c>largeResult</c> on the first response, the page record on later ones):
    /// the envelope-level <c>hasMoreData</c> and <c>nextOffset</c>, and the <c>statusMessage</c> when it carries a tool-call
    /// hint (for example ReadFile's "call ReadFile(startLine, endLine) for the rest"). A <c>statusMessage</c> without a call
    /// hint is information about the result and stays, as does <c>totalRecords</c> (the tool's own count, not an instruction);
    /// data, findings and warnings are never touched.
    /// </summary>
    public static void StripToolPaging(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        foreach (string key in envelope.Select(kv => kv.Key).ToList())
        {
            bool tool = key.Equals("hasMoreData", StringComparison.OrdinalIgnoreCase)
                || key.Equals("nextOffset", StringComparison.OrdinalIgnoreCase);
            bool hint = key.Equals("statusMessage", StringComparison.OrdinalIgnoreCase)
                && envelope[key] is JsonValue value && value.TryGetValue(out string? text) && System.Text.RegularExpressions.Regex.IsMatch(text, @"\w+\(reason:");
            if (tool || hint)
            {
                envelope.Remove(key);
            }
        }
    }
}
