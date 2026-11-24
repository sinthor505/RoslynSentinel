namespace RoslynSentinel.Common;

/// <summary>Where a tool entry in the exposed surface came from.</summary>
public enum ToolSurfaceOrigin
{
    /// <summary>Registered when the server started.</summary>
    Startup,

    /// <summary>Added or removed at runtime by enabling a toolset.</summary>
    RuntimeToolset,
}

/// <summary>One tool row in a tool-surface snapshot.</summary>
/// <param name="Name">Tool name as exposed to clients.</param>
/// <param name="ClassName">Declaring tool class.</param>
/// <param name="Origin">Whether the tool was registered at startup or by a toolset.</param>
/// <param name="ToolSet">Toolset that gates the tool, or null when ungated.</param>
/// <param name="ListedParameters">Parameter names still listed in the emitted schema.</param>
/// <param name="HiddenParameters">Parameter names stripped from the emitted schema but still accepted.</param>
public sealed record ToolSurfaceTool(string Name, string ClassName, ToolSurfaceOrigin Origin, string? ToolSet,
    IReadOnlyList<string> ListedParameters, IReadOnlyList<string> HiddenParameters);

/// <summary>
/// Point-in-time view of the tools exposed to clients. Name keeps the repo's *Snapshot convention
/// (ToolTelemetrySnapshot, GitBackendSnapshot) rather than *Result.
/// </summary>
/// <param name="Version">Monotonic version, incremented on each rebuild.</param>
/// <param name="BuiltUtc">UTC time the snapshot was built.</param>
/// <param name="Trigger">What caused the rebuild.</param>
/// <param name="Fingerprint">Stable digest of the exposed tool set and schemas.</param>
/// <param name="Tools">Exposed tool rows.</param>
/// <param name="EnabledToolSets">Toolsets enabled when the snapshot was built.</param>
/// <param name="SchemaPatches">Schema patches applied to emitted tool schemas.</param>
public sealed record ToolSurfaceSnapshot(long Version, DateTime BuiltUtc, string Trigger, string Fingerprint,
    IReadOnlyList<ToolSurfaceTool> Tools, IReadOnlyList<string> EnabledToolSets, IReadOnlyList<string> SchemaPatches)
{
    /// <summary>Snapshot used before the first build.</summary>
    public static ToolSurfaceSnapshot Empty { get; } = new(0, DateTime.MinValue, "none", "", [], [], []);

    /// <summary>True when <paramref name="toolName"/> is exposed in this snapshot.</summary>
    public bool IsExposed(string toolName) => throw new NotImplementedException(); // TODO implement (Step A2)

    /// <summary>Returns the row for <paramref name="toolName"/>, or null when it is not exposed.</summary>
    public ToolSurfaceTool? Find(string toolName) => throw new NotImplementedException(); // TODO implement (Step A2)
}
