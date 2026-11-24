namespace RoslynSentinel.Common;

/// <summary>
/// One registration method's startup tool classes, already selected for this launch, in tools/list order.
/// </summary>
/// <param name="ToolTypes">The <c>[McpServerToolType]</c> classes selected for this launch, in tools/list order.</param>
public sealed record ToolSurfaceStartupRequest(IReadOnlyList<Type> ToolTypes);
