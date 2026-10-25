namespace RoslynSentinel.Common;

public static class LargeResultHints
{
    /// <summary>Builds the complete GetLargeResult call for hints; every hint naming GetLargeResult must use this so required parameters stay in one place.</summary>
    public static string FetchCall(string resultId, int? offset = null, int? charLimit = null)
    {
        var call = $"GetLargeResult(reason: \"page through the stored result\", resultId: \"{resultId}\"";
        if (offset is not null)
        {
            call += $", offset: {offset.Value}";
        }

        if (charLimit is not null)
        {
            call += $", charLimit: {charLimit.Value}";
        }

        return call + ")";
    }

    /// <summary>FilePath form of FetchCall, for a largeresult_*.json path instead of a resultId; same rule: every hint naming GetLargeResult uses these helpers.</summary>
    public static string FetchCallForFile(string filePath, int? offset = null, int? charLimit = null)
    {
        var call = $"GetLargeResult(reason: \"page through the stored result\", filePath: \"{filePath}\"";
        if (offset is not null)
        {
            call += $", offset: {offset.Value}";
        }

        if (charLimit is not null)
        {
            call += $", charLimit: {charLimit.Value}";
        }

        return call + ")";
    }
}
