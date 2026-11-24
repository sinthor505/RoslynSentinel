using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// The built-in schema patches, in the order they must run.
/// </summary>
public static class DefaultToolSchemaPatches
{
    /// <summary>All default patches, sorted by ascending order.</summary>
    public static IReadOnlyList<IToolSchemaPatch> All { get; } =
    [
        new ConsumesTagsPatch(),
        new BatchItemFieldsPatch(),
        new ReplaceSnippetLimitsPatch(),
        new HiddenParametersPatch(),
    ];
}

/// <summary>Adds the consumes-tags hints to a tool schema.</summary>
internal sealed class ConsumesTagsPatch : IToolSchemaPatch
{
    /// <inheritdoc />
    public string Name => "ConsumesTags";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public void Apply(McpServerTool tool, MethodInfo toolMethod) =>
        McpToolSchemaPatcher.ApplyConsumesTags(tool, toolMethod);
}

/// <summary>Adjusts the item fields of batch edit parameters.</summary>
internal sealed class BatchItemFieldsPatch : IToolSchemaPatch
{
    /// <inheritdoc />
    public string Name => "BatchItemFields";

    /// <inheritdoc />
    public int Order => 200;

    /// <inheritdoc />
    public void Apply(McpServerTool tool, MethodInfo toolMethod) =>
        McpToolSchemaPatcher.ApplyBatchItemFields(tool, toolMethod);
}

/// <summary>Applies the size limits to the ReplaceSnippet schema.</summary>
internal sealed class ReplaceSnippetLimitsPatch : IToolSchemaPatch
{
    /// <inheritdoc />
    public string Name => "ReplaceSnippetLimits";

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public void Apply(McpServerTool tool, MethodInfo toolMethod) =>
        McpToolSchemaPatcher.ApplyReplaceSnippetLimits(tool, toolMethod);
}

/// <summary>Hides internal parameters from the published tool schema.</summary>
internal sealed class HiddenParametersPatch : IToolSchemaPatch
{
    /// <inheritdoc />
    public string Name => "HiddenParameters";

    /// <inheritdoc />
    public int Order => 400;

    /// <inheritdoc />
    public void Apply(McpServerTool tool, MethodInfo toolMethod) => Apply(tool, toolMethod, services: null);

    /// <summary>
    /// Reads the standalone DI <see cref="ParameterSurface"/> singleton, never the ToolSurfaceController: resolving
    /// the controller builds every tool, so a patch resolving it would re-enter a singleton under construction.
    /// The FromStartup fallback covers <c>services: null!</c> from ToolSignatureCatalogReflection and direct test builds.
    /// </summary>
    public void Apply(McpServerTool tool, MethodInfo toolMethod, IServiceProvider? services) =>
        McpToolSchemaPatcher.ApplyHiddenParameters(tool, toolMethod, services?.GetService<ParameterSurface>() ?? ParameterSurface.FromStartup());
}
