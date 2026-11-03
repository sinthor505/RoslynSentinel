namespace RoslynSentinel.Common;

/// <summary>
/// Marks the property of a tool's success-data type that holds the result's records (the list a caller pages
/// through). <see cref="ResultPager"/> cuts this array first when a result is over budget, and reports the cut
/// as not inferred. At most one property per type; without the hint the pager cuts the largest array or string
/// and marks the cut inferred.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ResultRecordsAttribute : Attribute
{
}
