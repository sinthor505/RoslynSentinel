namespace RoslynSentinel.Common;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ProducesAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public bool Required
    {
        get;
    }

    /// <summary>
    /// Producer-preference weight for <see cref="ToolGraph.ProducersOf"/> selection.
    /// When multiple tools produce the same tag, the highest-weight producer is selected.
    /// Ties are broken by ordinal tool-name sort for determinism.
    /// Default 0.
    /// </summary>
    public int Preference { get; set; }

    public ProducesAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }
}
