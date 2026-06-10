namespace RoslynSentinel.Common;

public record GenerateToStringResult : EngineResultBase
{
    public string UpdatedContent
    {
        get;
        init;
    }
    public List<string> IncludedProperties
    {
        get;
        init;
    }
    public List<string> ExcludedProperties
    {
        get;
        init;
    }
    public string? Warning
    {
        get;
        init;
    }

    public GenerateToStringResult(string updatedContent, List<string> includedProperties, List<string> excludedProperties, string? warning)
    {
        this.UpdatedContent = updatedContent;
        this.IncludedProperties = includedProperties;
        this.ExcludedProperties = excludedProperties;
        this.Warning = warning;
    }
}
