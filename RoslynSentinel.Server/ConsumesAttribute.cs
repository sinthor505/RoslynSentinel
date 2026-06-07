namespace RoslynSentinel.Server;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ConsumesAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public ConsumesAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}
