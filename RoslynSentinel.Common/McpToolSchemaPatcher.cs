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
/// <c>McpServerBuilderExtensions.WithTools<TToolType></c> does, except it also supplies a
/// <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/> that patches up the two known-broken
/// schema shapes <c>JsonSchemaExporter</c> produces for custom-converter wrapper types.
/// </summary>
/// <remarks>
/// Any tool parameter (or POCO property on a class used as a parameter) typed as a custom struct
/// with a <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> that <c>JsonSchemaExporter</c>
/// can't build a normal schema for makes it fall back to one of two broken shapes, both handled below:
/// <list type="bullet">
/// <item>A converter implementing only <c>ReadAsPropertyName</c>/<c>WriteAsPropertyName</c> (e.g.
/// <see cref="FilePathWrapper"/>) makes the exporter emit the bare JSON Schema boolean <c>true</c> ->
/// legal JSON Schema 2020-12, but rejected outright by LM Studio's grammar converter with
/// "Unrecognized schema: true" the instant MCP tools are enabled.</item>
/// <item>A converter implementing only <c>Read</c>/<c>Write</c> (e.g. <see cref="ToolCallReason"/>)
/// makes the exporter emit an object with only the property's <c>description</c> and no <c>type</c>
/// at all -> not rejected outright, but silently unconstrained: LM Studio's grammar has nothing telling
/// it the value must be a string, so nothing stops a model from emitting a number/bool/object there
/// instead (confirmed live 2026-09-11: RunTest's <c>reason</c> parameter, the only <c>ToolCallReason</c>
/// in the schema missing a sibling <c>type</c> next to every other parameter's, letting a model supply
/// a bare float that then failed <see cref="ToolCallReasonJsonConverter"/>'s validation).</item>
/// </list>
/// The SDK's own <c>WithTools<T>()</c> never passes a
/// <see cref="McpServerToolCreateOptions.SchemaCreateOptions"/> (confirmed by decompiling
/// ModelContextProtocol.Core 2.2.0's <c>AIFunctionMcpServerTool</c>), so there is no way to plug this
/// fix in through the SDK's extension method -> this reimplements its registration loop with that one
/// option supplied.
/// </remarks>
public static class McpToolSchemaPatcher
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
        // is the exporter's other fallback for a custom-converter type it can't introspect further ->
        // every WithSentinelTools<T>-generated parameter that DOES have a normal shape always carries a
        // "type", so an object missing one here is never an intentionally-untyped schema, always this
        // fallback. Defaulting it to "string" matches every current custom-converter wrapper type
        // (ToolCallReason, FilePathWrapper) being string-backed.
        if (node is JsonObject obj && !obj.ContainsKey("type") && !obj.ContainsKey("$ref") && !obj.ContainsKey("anyOf"))
        {
            obj["type"] = "string";
        }

        // DataTag chaining contract (proposal_datatag_chaining_contract.md): surface which
        // DataTag a property/parameter represents directly on its schema node, as a vendor
        // extension key (JSON Schema permits free-form keys; the SDK reserves none of its own).
        // A schema node could need both the type-fix above AND a tag injection here -> this is
        // deliberately additive to the existing fixups, not a replacement branch.
        if (!SchemaOptions.EmitDataTags && node is JsonObject defaultObj &&
            defaultObj.TryGetPropertyValue("default", out JsonNode? defaultValue) && defaultValue is null)
        {
            // proposal_reduce_tool_schema_token_cost.md Step 1: a null default is pure token cost
            // (the property's optionality is already expressed by its absence from "required").
            defaultObj.Remove("default");
        }

        if (SchemaOptions.EmitDataTags && node is JsonObject tagObj)
        {
            ICustomAttributeProvider? propertyProvider = ctx.PropertyAttributeProvider;
            if (propertyProvider is not null)
            {
                string[] producesTags = propertyProvider
                    .GetCustomAttributes(typeof(ProducesAttribute), inherit: true)
                    .Cast<ProducesAttribute>()
                    .Select(p => p.Tag.ToString())
                    .ToArray();
                if (producesTags.Length == 1)
                {
                    tagObj["x-produces-tag"] = producesTags[0];
                }
                else if (producesTags.Length > 1)
                {
                    tagObj["x-produces-tag"] = new JsonArray(producesTags.Select(t => (JsonNode)t).ToArray());
                }
            }

            ICustomAttributeProvider? parameterProvider = ctx.ParameterAttributeProvider;
            if (parameterProvider is not null)
            {
                string[] consumesTags = parameterProvider
                    .GetCustomAttributes(typeof(ConsumesAttribute), inherit: true)
                    .Cast<ConsumesAttribute>()
                    .Select(c => c.Tag.ToString())
                    .Concat(parameterProvider
                        .GetCustomAttributes(typeof(ExternalInputRequiredAttribute), inherit: true)
                        .Cast<ExternalInputRequiredAttribute>()
                        .Select(e => e.Tag.ToString()))
                    .ToArray();
                if (consumesTags.Length == 1)
                {
                    tagObj["x-consumes-tag"] = consumesTags[0];
                }
                else if (consumesTags.Length > 1)
                {
                    tagObj["x-consumes-tag"] = new JsonArray(consumesTags.Select(t => (JsonNode)t).ToArray());
                }
            }
        }

        return node;
    }

    /// <summary>
    /// Drop-in replacement for <c>IMcpServerBuilder.WithTools<TToolType>()</c> that fixes up
    /// any bare-<c>true</c> schema node produced for <typeparamref name="TToolType"/>'s tool methods.
    /// </summary>
    public static IMcpServerBuilder WithSentinelTools<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] TToolType>(
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
                builder.Services.AddSingleton<McpServerTool>(services =>
                {
                    McpServerTool tool = McpServerTool.Create(toolMethod, target: null, CreateOptions(services, serializerOptions));
                    ApplyConsumesTags(tool, toolMethod);
                    ApplyReplaceSnippetLimits(tool, toolMethod);
                    ApplyLeanProfile(tool, toolMethod);
                    return tool;
                });
            }
            else
            {
                // Mirrors the SDK's own WithTools<T>() instance-method branch: construct a fresh
                // target per invocation via ActivatorUtilities (DI-aware constructor injection,
                // falling back to Activator.CreateInstance with no DI container), rather than
                // resolving a pre-registered singleton -> matches the SDK's documented "an instance
                // is constructed for each invocation" behavior for parity with WithTools<T>().
                builder.Services.AddSingleton<McpServerTool>(services =>
                {
                    McpServerTool tool = McpServerTool.Create(
                        toolMethod,
                        createTargetFunc: (RequestContext<CallToolRequestParams> request) => CreateTarget(((MessageContext)request).Services, typeof(TToolType)),
                        CreateOptions(services, serializerOptions));
                    ApplyConsumesTags(tool, toolMethod);
                    ApplyReplaceSnippetLimits(tool, toolMethod);
                    ApplyLeanProfile(tool, toolMethod);
                    return tool;
                });
            }
        }

        return builder;
    }

    /// <summary>
    /// Reflects over every type in <paramref name="assemblies"/> to find every method carrying
    /// <see cref="McpServerToolAttribute"/>, independent of whether that method's declaring class
    /// is currently DI-registered/mode-gated. Used by McpServerStatus to report ground truth on
    /// what tools exist in this process versus what is actually active for the current mode -
    /// a hand-maintained registry (ToolClassRegistry) only tracks whole classes, not methods, and
    /// can silently miss a gated or newly-added tool.
    /// </summary>
    public static IReadOnlyList<(string ToolName, string ClassName)> DiscoverAllDeclaredTools(params Assembly[] assemblies)
    {
        var discovered = new List<(string ToolName, string ClassName)>();

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (toolAttribute is null)
                    {
                        continue;
                    }

                    discovered.Add((toolAttribute.Name ?? method.Name, type.Name));
                }
            }
        }

        return discovered;
    }

    private static McpServerToolCreateOptions CreateOptions(IServiceProvider services, JsonSerializerOptions? serializerOptions) => new()
    {
        Services = services,
        SerializerOptions = serializerOptions,
        SchemaCreateOptions = SchemaCreateOptions,
    };

    private static object CreateTarget(IServiceProvider? services, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type) =>
        services is null ? Activator.CreateInstance(type)! : ActivatorUtilities.CreateInstance(services, type);

    /// <summary>
    /// Post-processes <paramref name="tool"/>'s already-built <see cref="Tool.InputSchema"/> to add
    /// <c>x-consumes-tag</c> extensions for parameters carrying <see cref="ConsumesAttribute"/> or
    /// <see cref="ExternalInputRequiredAttribute"/>.
    /// </summary>
    /// <remarks>
    /// This cannot be done from <see cref="RewriteBrokenSchemaNode"/>: that callback receives an
    /// <see cref="AIJsonSchemaCreateContext"/> built from a JsonSchemaExporterContext, whose
    /// ParameterAttributeProvider only ever resolves for a record/class property's own constructor
    /// parameter (confirmed against Microsoft.Extensions.AI.Abstractions source) - never for a tool
    /// method's own top-level parameters, which are schema'd through a separate SDK path
    /// (AIJsonUtilities.CreateFunctionJsonSchema) that never threads its real ParameterInfo into that
    /// context. x-produces-tag works from that callback because [Produces] in practice sits on DTO
    /// properties, not tool parameters, so it stays on the reachable property path. This mirrors the
    /// SDK's own AIFunctionMcpServerTool.AddMcpHeaderExtensions - reparse the already-built InputSchema,
    /// look up each parameter by its schema name (ParameterInfo.Name; the SDK only overrides this via
    /// AIParameterNameAttribute, unused in this codebase), inject the vendor key, reassign.
    /// </remarks>
    private static void ApplyConsumesTags(McpServerTool tool, MethodInfo toolMethod)
    {
        if (!SchemaOptions.EmitDataTags)
        {
            return;
        }

        ParameterInfo[] parameters = toolMethod.GetParameters();

        JsonNode? schemaNode = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        if (schemaNode is not JsonObject schemaObj ||
            !schemaObj.TryGetPropertyValue("properties", out JsonNode? propertiesNode) ||
            propertiesNode is not JsonObject propertiesObj)
        {
            return;
        }

        bool anyTagged = false;
        foreach (ParameterInfo parameter in parameters)
        {
            if (parameter.Name is null ||
                !propertiesObj.TryGetPropertyValue(parameter.Name, out JsonNode? propNode) ||
                propNode is not JsonObject propObj)
            {
                continue;
            }

            string[] consumesTags = parameter
                .GetCustomAttributes(typeof(ConsumesAttribute), inherit: true)
                .Cast<ConsumesAttribute>()
                .Select(c => c.Tag.ToString())
                .Concat(parameter
                    .GetCustomAttributes(typeof(ExternalInputRequiredAttribute), inherit: true)
                    .Cast<ExternalInputRequiredAttribute>()
                    .Select(e => e.Tag.ToString()))
                .ToArray();

            if (consumesTags.Length == 1)
            {
                propObj["x-consumes-tag"] = consumesTags[0];
                anyTagged = true;
            }
            else if (consumesTags.Length > 1)
            {
                propObj["x-consumes-tag"] = new JsonArray(consumesTags.Select(t => (JsonNode)t).ToArray());
                anyTagged = true;
            }
        }

        if (anyTagged)
        {
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schemaNode);
        }
    }

    /// <summary>
    /// Post-processes <paramref name="tool"/>'s already-built <see cref="Tool.InputSchema"/> to inline
    /// the live <see cref="ReplaceSnippetOptions"/> line/char limits into the <c>oldContent</c>/
    /// <c>newContent</c> parameter descriptions of the <c>ReplaceSnippet</c> tool method.
    /// </summary>
    /// <remarks>
    /// Same reparse-by-parameter-name approach as <see cref="ApplyConsumesTags"/>, for the same reason:
    /// <c>oldContent</c>/<c>newContent</c> are the tool method's own top-level parameters (schema'd via
    /// AIJsonUtilities.CreateFunctionJsonSchema), not POCO properties, so RewriteBrokenSchemaNode's
    /// ParameterAttributeProvider path can't see which parameter is being rewritten there. The limits
    /// are runtime-configurable (ReplaceSnippetOptions.Configure, called from every server entry point
    /// before AddRoslynSentinelTools*/WithSentinelTools runs), so they can't be baked into the
    /// [Description] const strings in ToolParams - only a post-build patch like this can surface them.
    /// </remarks>
    private static void ApplyReplaceSnippetLimits(McpServerTool tool, MethodInfo toolMethod)
    {
        if (toolMethod.Name != "ReplaceSnippet")
        {
            return;
        }

        JsonNode? schemaNode = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        if (schemaNode is not JsonObject schemaObj ||
            !schemaObj.TryGetPropertyValue("properties", out JsonNode? propertiesNode) ||
            propertiesNode is not JsonObject propertiesObj)
        {
            return;
        }

        bool anyPatched = PatchDescription(
            propertiesObj,
            "oldContent",
            $"Verbatim text to find and replace using literal substring match. Limit: {ReplaceSnippetOptions.MaxOldContentLines} lines / {ReplaceSnippetOptions.MaxOldContentChars} chars (excluding leading indentation).");
        anyPatched |= PatchDescription(
            propertiesObj,
            "newContent",
            $"Verbatim replacement text for oldContent. Limit: {ReplaceSnippetOptions.MaxNewContentLines} lines / {ReplaceSnippetOptions.MaxNewContentChars} chars (excluding leading indentation).");

        if (anyPatched)
        {
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schemaNode);
        }

        static bool PatchDescription(JsonObject propertiesObj, string parameterName, string description)
        {
            if (!propertiesObj.TryGetPropertyValue(parameterName, out JsonNode? propNode) ||
                propNode is not JsonObject propObj)
            {
                return false;
            }

            propObj["description"] = description;
            return true;
        }
    }

    /// <summary>
    /// When <see cref="SchemaOptions.Profile"/> is <see cref="SchemaProfile.Lean"/>, removes the
    /// boilerplate parameters <c>autoStage</c>, <c>returnDiff</c>, <c>validateOnApply</c>,
    /// <c>lineBefore</c> and <c>lineAfter</c> from <paramref name="tool"/>'s already-built
    /// top-level <see cref="Tool.InputSchema"/> (and from its <c>required</c> array).
    /// </summary>
    /// <remarks>
    /// Only the top-level <c>properties</c> object is touched: nested item schemas (e.g.
    /// <c>batchEdits</c> items, which carry their own <c>lineBefore</c>/<c>lineAfter</c>) are left
    /// alone. The C# method keeps every parameter, so runtime binding is unchanged; the names that were
    /// actually stripped are registered in <see cref="HiddenSchemaParams"/> so the argument validator,
    /// which treats the emitted schema as its allow-list, still accepts them at call time.
    /// </remarks>
    private static void ApplyLeanProfile(McpServerTool tool, MethodInfo toolMethod)
    {
        if (SchemaOptions.Profile != SchemaProfile.Lean)
        {
            return;
        }

        string[] leanHiddenParameters = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];

        JsonNode? schemaNode = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        if (schemaNode is not JsonObject schemaObj ||
            !schemaObj.TryGetPropertyValue("properties", out JsonNode? propertiesNode) ||
            propertiesNode is not JsonObject propertiesObj)
        {
            return;
        }

        var removed = new List<string>();
        foreach (string name in leanHiddenParameters)
        {
            if (propertiesObj.Remove(name))
            {
                removed.Add(name);
            }
        }

        if (removed.Count == 0)
        {
            return;
        }

        if (schemaObj.TryGetPropertyValue("required", out JsonNode? requiredNode) &&
            requiredNode is JsonArray requiredArray)
        {
            for (int i = requiredArray.Count - 1; i >= 0; i--)
            {
                if (requiredArray[i] is JsonValue value &&
                    value.TryGetValue(out string? requiredName) &&
                    requiredName is not null &&
                    removed.Contains(requiredName))
                {
                    requiredArray.RemoveAt(i);
                }
            }
        }

        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schemaNode);
        HiddenSchemaParams.Register(tool.ProtocolTool.Name, removed);
    }
}
