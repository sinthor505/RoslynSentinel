namespace RoslynSentinel.Server;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ExternalInputRequiredAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public ExternalInputRequiredAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}
