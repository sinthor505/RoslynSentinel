namespace RoslynSentinel.Common;

/// <summary>
/// Maps a write-chokepoint refusal (<see cref="ApplyChangesResult.RefusalCode"/>) to the structured
/// <see cref="ResultError"/> a tool returns, so every write tool words the same refusal the same way.
/// </summary>
public static class WriteRefusal
{
    /// <summary>
    /// Maps a refused write to its <see cref="ResultError"/>. Returns false, with <paramref name="error"/> null, for any code not mapped here.
    /// When <paramref name="ledger"/> is given (see <see cref="LedgerOf"/>), a StaleRead message names the last writer of each failed file; without it the message is unchanged.
    /// </summary>
    public static bool TryMap(string toolName, ApplyChangesResult result, out ResultError error, EditAttributionLedger? ledger = null)
    {
        error = null!;
        if (!result.IsError || result.RefusalCode is null)
        {
            return false;
        }

        switch (result.RefusalCode)
        {
            case ToolErrorCode.StaleRead:
                error = StaleReadError(toolName, result, ledger);
                return true;
            case ToolErrorCode.FoldPending:
                error = FoldPendingError(toolName, result);
                return true;
            case ToolErrorCode.WriteLockBusy:
                error = WriteLockBusyError(toolName, result);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Lists at most this many failed files in the message; the rest go to StructuredDetail.</summary>
    private const int MaxListedFiles = 10;

    private static ResultError StaleReadError(string toolName, ApplyChangesResult result, EditAttributionLedger? ledger = null)
    {
        var files = result.FailedFiles;
        var count = files.Count;
        var writers = LastWritersFor(files, ledger);
        var now = DateTime.UtcNow;
        var callerSession = ToolCallContext.Current?.SessionId;
        string Entry(KeyValuePair<FilePathWrapper, string> f) =>
            writers.TryGetValue(f.Key.ToString(), out var writer)
                ? $"{f.Key} ({f.Value}; last changed by {EditAttributionWording.Describe(writer, now, callerSession)})"
                : $"{f.Key} ({f.Value})";
        var list = count == 0
            ? "(no file names reported)"
            : string.Join("; ", files.Take(MaxListedFiles).Select(Entry))
              + (count > MaxListedFiles ? $"; and {count - MaxListedFiles} more" : string.Empty);
        var reread = count == 0
            ? $"re-read the file(s) you are editing with {ToolCallHint.Call("ReadFile", ("filePath", "<file you are editing>"))}"
            : $"re-read with {ToolCallHint.Call("ReadFile", ("filePath", (string)files.Keys.First()))}";
        IReadOnlyList<object>? structuredDetail = count > MaxListedFiles
            ? files.Select(f => (object)Entry(f)).ToList()
            : null;
        var message = $"{toolName}: {count} target file(s) changed after you last read them; nothing was written: {list}. Next: {reread}, then repeat the edit against the fresh content. If refused again, wait a moment (the server may still be syncing an external change).";
        return new ResultError(ToolErrorCode.StaleRead, message, StructuredDetail: structuredDetail);
    }

    /// <summary>
    /// The edit attribution ledger a workspace manager exposes, or null when it exposes none; pass it to
    /// <see cref="TryMap"/> so a StaleRead refusal names the last writer.
    /// </summary>
    public static EditAttributionLedger? LedgerOf(object? workspaceManager) =>
        (workspaceManager as IEditAttributionLedgerProvider)?.EditAttributions;

    // Empty when there is nothing to look up in: no ledger and no solution root on the call.
    private static IReadOnlyDictionary<string, EditAttribution> LastWritersFor(IReadOnlyDictionary<FilePathWrapper, string> files, EditAttributionLedger? ledger)
    {
        var solutionRoot = ToolCallContext.Current?.SolutionRoot;
        if (files.Count == 0 || (ledger is null && string.IsNullOrWhiteSpace(solutionRoot)))
        {
            return new Dictionary<string, EditAttribution>();
        }

        return EditAttributionWording.LastWriters(ledger, solutionRoot, files.Keys.Select(k => k.ToString()).ToList(), DateTime.UtcNow);
    }

    private static ResultError FoldPendingError(string toolName, ApplyChangesResult result)
    {
        var files = result.FailedFiles;
        var count = files.Count;
        var list = count == 0
            ? "(no file names reported)"
            : string.Join("; ", files.Take(MaxListedFiles).Select(f => f.Key))
              + (count > MaxListedFiles ? $"; and {count - MaxListedFiles} more" : string.Empty);
        IReadOnlyList<object>? structuredDetail = count > MaxListedFiles
            ? files.Select(f => (object)f.Key).ToList()
            : null;
        var message = $"{toolName}: {list} is changing on disk; retry in a moment. Nothing was written.";
        return new ResultError(ToolErrorCode.FoldPending, message, StructuredDetail: structuredDetail);
    }

    private static ResultError WriteLockBusyError(string toolName, ApplyChangesResult result)
    {
        return new ResultError(ToolErrorCode.WriteLockBusy, $"{toolName}: {result.Summary}");
    }
}
