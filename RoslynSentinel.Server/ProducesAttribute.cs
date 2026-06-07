namespace RoslynSentinel.Server;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ProducesAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public ProducesAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}
