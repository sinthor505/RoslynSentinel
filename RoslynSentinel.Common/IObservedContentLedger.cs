namespace RoslynSentinel.Common;

/// <summary>
/// Records the content this server last returned to a calling agent for a file. Keyed per agent
/// (the agent key is <c>ToolCallContext.Current?.AgentId ?? ""</c>) and stored as the SHA-256 of
/// the text, so a later read can be compared by content. This is the observed-content ledger:
/// it is NOT the scoped operation ledger and NOT the edit attribution ledger.
/// </summary>
public interface IObservedContentLedger
{
    /// <summary>
    /// Records the SHA-256 of <paramref name="text"/> as the content the current calling agent was
    /// last shown for <paramref name="path"/>.
    /// </summary>
    void RecordObserved(FilePathWrapper path, string text);
}
