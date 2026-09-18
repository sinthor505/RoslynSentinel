namespace RoslynSentinel.Common;

public interface IScopedOperationLedger
{
    bool TryOpen(string operationName, IReadOnlyList<LedgerEntryBase> entries, out string? rejectionReason);
    bool IsBlocked(FilePathWrapper filePath, out string? blockReason);
    void RecordFix(IReadOnlyList<string> entryIds, string changeId);
    void RecordUndo(string changeId);
    bool TryRelease();
    IReadOnlyList<LedgerEntryBase> GetOpenEntries();
}
