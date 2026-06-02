namespace RoslynSentinel.Server;

public sealed class ToolUpdateResult
{
    public bool Success
    {
        get; set;
    }

    public string? UpdatedContent
    {
        get; set;
    }  // null when autoStage=true

    public string? ChangeId
    {
        get; set;
    }        // populated when autoStage=true

    public string? Message
    {
        get; set;
    }         // human-readable status  

    public ResultError? Error
    {
        get; set;
    }      // null on success
}
