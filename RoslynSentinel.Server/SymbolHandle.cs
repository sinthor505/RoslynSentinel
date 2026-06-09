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
    public string Id
    {
        get; init;
    }

    public SymbolHandle(string sessionId, string projectName, string symbolKey)
    {
        SessionId = sessionId;
        ProjectName = projectName;
        Id = symbolKey;
    }
}
