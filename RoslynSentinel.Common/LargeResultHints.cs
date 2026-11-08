using System.Globalization;

namespace RoslynSentinel.Common;

/// <summary>
/// Hint text that points a model at GetLargeResult. Hints are built by <see cref="ToolCallHint"/> from the live tool
/// signature, so the call stays schema-valid. Every hint naming GetLargeResult must use these helpers.
/// </summary>
public static class LargeResultHints
{
    /// <summary>Builds the complete GetLargeResult call for hints; every hint naming GetLargeResult must use this so required parameters stay in one place.</summary>
    public static string FetchCall(string resultId, int? offset = null, string? path = null) =>
        ToolCallHint.Call("GetLargeResult", BuildPairs("resultId", resultId, offset, path));

    /// <summary>FilePath form of FetchCall, for a largeresult_*.json path instead of a resultId; same rule: every hint naming GetLargeResult uses these helpers.</summary>
    public static string FetchCallForFile(string filePath, int? offset = null, string? path = null) =>
        ToolCallHint.Call("GetLargeResult", BuildPairs("filePath", filePath, offset, path));

    private static (string Name, string Value)[] BuildPairs(string idName, string idValue, int? offset, string? path)
    {
        var pairs = new List<(string Name, string Value)> { ("reason", "page through the stored result"), (idName, idValue) };
        if (offset is not null)
        {
            pairs.Add(("offset", offset.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrEmpty(path))
        {
            pairs.Add(("path", path));
        }

        return pairs.ToArray();
    }
}
