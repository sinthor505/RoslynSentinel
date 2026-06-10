namespace RoslynSentinel.Common;

/// <summary>Return type for get_operation_detail — a filtered slice of an operation blob.</summary>
public class OperationDetailResult
{
    public string ChangeId { get; set; } = "";
    public string BlobName { get; set; } = "";
    public int TotalItems
    {
        get; set;
    }
    public int ReturnedItems
    {
        get; set;
    }
    public string? Filter
    {
        get; set;
    }
    public List<OperationItemRecord> Items { get; set; } = new();
}
