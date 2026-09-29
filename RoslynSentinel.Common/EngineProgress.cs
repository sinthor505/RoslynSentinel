namespace RoslynSentinel.Common;

/// <summary>
/// Protocol-neutral progress report for engine operations. Tool classes adapt this to the
/// transport's progress type at the call boundary, so engines never reference MCP types.
/// </summary>
public sealed record EngineProgress(float Progress, string? Message = null, float? Total = null);
