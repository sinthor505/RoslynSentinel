namespace RoslynSentinel.Common;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class SupportsBatchingAttribute : Attribute
{
    public enum BatchMode
    {
        Single,
        Batch,
        SingleAndBatch
    }

    public SupportsBatchingAttribute()
    {
    }

    public SupportsBatchingAttribute(BatchMode mode)
    {
        this.Mode = mode;
    }

    public BatchMode Mode
    {
        get;
    }
}
