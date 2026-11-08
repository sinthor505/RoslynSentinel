namespace RoslynSentinel.Common;

/// <summary>
/// One cut made by <see cref="ResultPager"/>: the node at <paramref name="Path"/> keeps its first
/// <paramref name="Returned"/> of <paramref name="Total"/> <paramref name="Unit"/> inline; the rest is omitted.
/// </summary>
/// <param name="Path">Wire-name JSON path of the node, for example <c>successData.regexResults</c> or <c>successData.sites[3].matches</c>.</param>
/// <param name="Returned">Units kept inline (0 when the node was moved out or replaced by an empty prefix).</param>
/// <param name="Total">Units the node held before any cut.</param>
/// <param name="Unit"><see cref="ResultPager.UnitItems"/> (array elements), <see cref="ResultPager.UnitLines"/> (string lines).</param>
/// <param name="Inferred">True when the pager chose the node itself; false when a <see cref="ResultRecordsAttribute"/> hint named it.</param>
public sealed record OmittedPart(string Path, int Returned, int Total, string Unit, bool Inferred);
