namespace RoslynSentinel.Common;

/// <summary>
/// Ready-to-run ExternalFileDrift calls (operations LedgerList, LedgerFix, LedgerRelease) that resolve an open scoped
/// operation ledger. Built through <see cref="ToolCallHint"/> so the operation and parameter names are checked against
/// the tool catalog (ToolHintRatchetTests).
/// </summary>
public static class LedgerMessages
{
    public static string ListCall() => ToolCallHint.Call("ExternalFileDrift", ("operation", "LedgerList"));

    public static string FixCall(string entryId) => ToolCallHint.Call("ExternalFileDrift", ("operation", "LedgerFix"), ("entryIds", entryId));

    public static string ReleaseCall() => ToolCallHint.Call("ExternalFileDrift", ("operation", "LedgerRelease"));

    /// <summary>How to resolve an open ledger: list it, mark fixed entries, or abandon it.</summary>
    public static string Recovery() =>
        $"To resolve it: list the entries with {ListCall()}, fix those call sites, then mark them with {FixCall("<entryId>")}; " +
        $"or abandon the ledger with {ReleaseCall()}.";
}
