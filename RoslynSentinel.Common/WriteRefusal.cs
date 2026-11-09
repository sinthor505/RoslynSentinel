namespace RoslynSentinel.Common;

/// <summary>
/// Maps a write-chokepoint refusal (<see cref="ApplyChangesResult.RefusalCode"/>) to the structured
/// <see cref="ResultError"/> a tool returns, so every write tool words the same refusal the same way.
/// </summary>
public static class WriteRefusal
{
    /// <summary>Maps a refused write to its <see cref="ResultError"/>. Returns false, with <paramref name="error"/> null, for any code not mapped here.</summary>
    public static bool TryMap(string toolName, ApplyChangesResult result, out ResultError error)
    {
        error = null!;
        if (!result.IsError || result.RefusalCode is null)
        {
            return false;
        }

        switch (result.RefusalCode)
        {
            case ToolErrorCode.StaleRead:
                error = StaleReadError(toolName, result);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Lists at most this many failed files in the message; the rest go to StructuredDetail.</summary>
    private const int MaxListedFiles = 10;

    private static ResultError StaleReadError(string toolName, ApplyChangesResult result)
    {
        var files = result.FailedFiles;
        var count = files.Count;
        var list = count == 0
            ? "(no file names reported)"
            : string.Join("; ", files.Take(MaxListedFiles).Select(f => $"{f.Key} ({f.Value})"))
              + (count > MaxListedFiles ? $"; and {count - MaxListedFiles} more" : string.Empty);
        var reread = count == 0
            ? $"re-read the file(s) you are editing with {ToolCallHint.Call("ReadFile", ("filePath", "<file you are editing>"))}"
            : $"re-read with {ToolCallHint.Call("ReadFile", ("filePath", (string)files.Keys.First()))}";
        IReadOnlyList<object>? structuredDetail = count > MaxListedFiles
            ? files.Select(f => (object)$"{f.Key} ({f.Value})").ToList()
            : null;
        // TODO per-file attribution: name the writer that changed each file once proposal_cross_session_write_journal.md lands
        var message = $"{toolName}: {count} target file(s) changed after you last read them; nothing was written: {list}. Next: {reread}, then repeat the edit against the fresh content. If refused again, wait a moment (the server may still be syncing an external change).";
        return new ResultError(ToolErrorCode.StaleRead, message, StructuredDetail: structuredDetail);
    }
}
