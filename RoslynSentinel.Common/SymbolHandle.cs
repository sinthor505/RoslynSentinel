namespace RoslynSentinel.Common;

public readonly struct SymbolHandle
{
    public string SymbolName
    {
        get; init;
    }
    public string SessionId
    {
        get; init;
    }
    public string ProjectName
    {
        get; init;
    }
    public string SymbolId
    {
        get; init;
    }

    public SymbolHandle(string sessionId, string symbolName, string symbolKey, string projectName)
    {
        SessionId = sessionId;
        SymbolName = symbolName;
        SymbolId = symbolKey;
        ProjectName = projectName;
    }
}
