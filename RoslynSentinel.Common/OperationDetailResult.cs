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
    public int Offset
    {
        get; set;
    }
    /// <summary>Pass as `offset` on the next call to continue past this page; null once no items remain.</summary>
    public int? NextOffset
    {
        get; set;
    }
    public string? Filter
    {
        get; set;
    }
    public List<OperationItemRecord> Items { get; set; } = new();
}
