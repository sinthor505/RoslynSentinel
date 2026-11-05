namespace RoslynSentinel.Common;

public interface IScopedOperationLedger
{
    bool TryOpen(string operationName, IReadOnlyList<LedgerEntryBase> entries, out string? rejectionReason, string? openingChangeId = null);
    bool IsBlocked(FilePathWrapper filePath, out string? blockReason);
    void RecordFix(IReadOnlyList<string> entryIds, string changeId);
    void RecordUndo(string changeId);
    bool TryRelease();
    IReadOnlyList<LedgerEntryBase> GetOpenEntries();

    /// <summary>The operation that opened the current ledger (for example MoveMember); null when no ledger is open.</summary>
    string? OpenOperationName { get; }

    /// <summary>The change id of the apply that opened the current ledger; null when none is open or none was supplied.</summary>
    string? OpeningChangeId { get; }

    /// <summary>Abandons the open ledger even with unresolved entries (operator escape hatch). Returns how many unresolved entries were abandoned; 0 when no ledger was open.</summary>
    int ForceRelease();
}
