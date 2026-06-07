namespace RoslynSentinel.Server;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ToolControlAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public ToolControlAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}
