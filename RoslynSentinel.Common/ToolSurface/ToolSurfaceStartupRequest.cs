namespace RoslynSentinel.Common;

/// <summary>
/// One registration method's startup tool classes, already selected for this launch, in tools/list order.
/// </summary>
/// <param name="Source">Names the registration method that supplied the types ("Basic", "Advanced").</param>
/// <param name="ToolTypes">The <c>[McpServerToolType]</c> classes selected for this launch, in tools/list order.</param>
public sealed record ToolSurfaceStartupRequest(string Source, IReadOnlyList<Type> ToolTypes);
