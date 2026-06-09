namespace RoslynSentinel.Server;

public record BestInsertionResult : EngineResultBase
{
    public string ContainerName
    {
        get; init;
    }
    public string MemberKind
    {
        get; init;
    }
    public int InsertBeforeLine
    {
        get; init;
    }
    public string Reason
    {
        get; init;
    } = string.Empty;

    public BestInsertionResult(FilePath filePath, string containerName, string memberKind, int insertBeforeLine, string reason)
    {
        this.FilePath = filePath;
        this.ContainerName = containerName;
        this.MemberKind = memberKind;
        this.InsertBeforeLine = insertBeforeLine;
        this.Reason = reason;
    }
}
