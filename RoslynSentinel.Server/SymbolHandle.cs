namespace RoslynSentinel.Server;

public readonly struct SymbolHandle
{
    public string SessionId
    {
        get; init;
    }
    public string ProjectName
    {
        get; init;
    }
    public string SymbolKey
    {
        get; init;
    }

    public SymbolHandle(string sessionId, string projectName, string symbolKey)
    {
        SessionId = sessionId;
        ProjectName = projectName;
        SymbolKey = symbolKey;
    }
}
