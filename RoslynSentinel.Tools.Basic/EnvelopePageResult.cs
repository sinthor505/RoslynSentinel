using System.Text.Json.Nodes;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// One page of one node of a stored <see cref="ResultWrapperType.Envelope"/> result: <c>GetLargeResult</c> by <c>path</c>.
/// Exactly one of <see cref="Items"/> (a JSON array node, <see cref="Unit"/> <c>items</c>) and <see cref="Text"/> (a string
/// node, <see cref="Unit"/> <c>lines</c>) is set. The page is fitted to the inline budget by <see cref="ResultPager"/>
/// before it is returned, so the result marshaller never cuts it again.
/// </summary>
public sealed record EnvelopePageResult
{
    /// <summary>Wire path of the paged node, as listed in <c>largeResult.omitted[].path</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Index of the first returned element or line.</summary>
    public required int Offset { get; init; }

    /// <summary>Elements or lines in this page.</summary>
    public required int Returned { get; init; }

    /// <summary>Elements or lines the stored node holds.</summary>
    public required int Total { get; init; }

    /// <summary><c>items</c> (array elements) or <c>lines</c> (string lines).</summary>
    public required string Unit { get; init; }

    /// <summary>Offset to pass on the next call; null when the node is read to its end.</summary>
    public int? NextOffset { get; init; }

    /// <summary>True when elements or lines remain after this page.</summary>
    public required bool HasMoreData { get; init; }

    /// <summary>The page of an array node. Declared as the records of this result so the pager cuts it first.</summary>
    [ResultRecords]
    public JsonArray? Items { get; init; }

    /// <summary>The page of a string node: whole lines, line terminators kept, so consecutive pages concatenate to the stored text.</summary>
    public string? Text { get; init; }
}
