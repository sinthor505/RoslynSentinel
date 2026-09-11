using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Registers <see cref="McpServerTool"/> instances the same way
/// <c>McpServerBuilderExtensions.WithTools&lt;TToolType&gt;</c> does, except it also supplies a
/// <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/> that patches up the two known-broken
/// schema shapes <c>JsonSchemaExporter</c> produces for custom-converter wrapper types.
/// </summary>
/// <remarks>
/// Any tool parameter (or POCO property on a class used as a parameter) typed as a custom struct
/// with a <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> that <c>JsonSchemaExporter</c>
/// can't build a normal schema for makes it fall back to one of two broken shapes, both handled below:
/// <list type="bullet">
/// <item>A converter implementing only <c>ReadAsPropertyName</c>/<c>WriteAsPropertyName</c> (e.g.
/// <see cref="FilePathWrapper"/>) makes the exporter emit the bare JSON Schema boolean <c>true</c> —
/// legal JSON Schema 2020-12, but rejected outright by LM Studio's grammar converter with
/// "Unrecognized schema: true" the instant MCP tools are enabled.</item>
/// <item>A converter implementing only <c>Read</c>/<c>Write</c> (e.g. <see cref="ToolCallReason"/>)
/// makes the exporter emit an object with only the property's <c>description</c> and no <c>type</c>
/// at all — not rejected outright, but silently unconstrained: LM Studio's grammar has nothing telling
/// it the value must be a string, so nothing stops a model from emitting a number/bool/object there
/// instead (confirmed live 2026-09-11: RunTest's <c>reason</c> parameter, the only <c>ToolCallReason</c>
/// in the schema missing a sibling <c>type</c> next to every other parameter's, letting a model supply
/// a bare float that then failed <see cref="ToolCallReasonJsonConverter"/>'s validation).</item>
/// </list>
/// The SDK's own <c>WithTools&lt;T&gt;()</c> never passes a
/// <see cref="McpServerToolCreateOptions.SchemaCreateOptions"/> (confirmed by decompiling
/// ModelContextProtocol.Core 2.2.0's <c>AIFunctionMcpServerTool</c>), so there is no way to plug this
/// fix in through the SDK's extension method — this reimplements its registration loop with that one
/// option supplied.
/// </remarks>
public static class McpToolSchemaFix
{
    public static readonly AIJsonSchemaCreateOptions SchemaCreateOptions = new()
    {
        TransformSchemaNode = RewriteBrokenSchemaNode,
    };

    private static JsonNode RewriteBrokenSchemaNode(AIJsonSchemaCreateContext ctx, JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue(out bool boolValue) && boolValue)
        {
            return new JsonObject { ["type"] = "string" };
        }

        // A schema object with no "type" at all (only "description", possibly alongside "default")
        // is the exporter's other fallback for a custom-converter type it can't introspect further —
        // every WithToolsFixed<T>-generated parameter that DOES have a normal shape always carries a
        // "type", so an object missing one here is never an intentionally-untyped schema, always this
        // fallback. Defaulting it to "string" matches every current custom-converter wrapper type
        // (ToolCallReason, FilePathWrapper) being string-backed.
        if (node is JsonObject obj && !obj.ContainsKey("type") && !obj.ContainsKey("$ref") && !obj.ContainsKey("anyOf"))
        {
            obj["type"] = "string";
        }

        return node;
    }

    /// <summary>
    /// Drop-in replacement for <c>IMcpServerBuilder.WithTools&lt;TToolType&gt;()</c> that fixes up
    /// any bare-<c>true</c> schema node produced for <typeparamref name="TToolType"/>'s tool methods.
    /// </summary>
    public static IMcpServerBuilder WithToolsFixed<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] TToolType>(
        this IMcpServerBuilder builder,
        JsonSerializerOptions? serializerOptions = null)
    {
        foreach (var toolMethod in typeof(TToolType).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (toolMethod.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            if (toolMethod.IsStatic)
            {
                builder.Services.AddSingleton<McpServerTool>(services => McpServerTool.Create(toolMethod, target: null, CreateOptions(services, serializerOptions)));
            }
            else
            {
                // Mirrors the SDK's own WithTools<T>() instance-method branch: construct a fresh
                // target per invocation via ActivatorUtilities (DI-aware constructor injection,
                // falling back to Activator.CreateInstance with no DI container), rather than
                // resolving a pre-registered singleton — matches the SDK's documented "an instance
                // is constructed for each invocation" behavior for parity with WithTools<T>().
                builder.Services.AddSingleton<McpServerTool>(services => McpServerTool.Create(
                    toolMethod,
                    createTargetFunc: (RequestContext<CallToolRequestParams> request) => CreateTarget(((MessageContext)request).Services, typeof(TToolType)),
                    CreateOptions(services, serializerOptions)));
            }
        }

        return builder;
    }

    private static McpServerToolCreateOptions CreateOptions(IServiceProvider services, JsonSerializerOptions? serializerOptions) => new()
    {
        Services = services,
        SerializerOptions = serializerOptions,
        SchemaCreateOptions = SchemaCreateOptions,
    };

    private static object CreateTarget(IServiceProvider? services, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type) =>
        services is null ? Activator.CreateInstance(type)! : ActivatorUtilities.CreateInstance(services, type);
}
