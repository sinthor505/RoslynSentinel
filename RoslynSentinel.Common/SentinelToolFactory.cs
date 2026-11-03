using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Common;

/// <summary>
/// Builds an <see cref="McpServerTool"/> the way the SDK's <c>McpServerTool.Create(MethodInfo, ...)</c> does,
/// except that the <see cref="AIFunction"/> is created here so its
/// <see cref="AIFunctionFactoryOptions.MarshalResult"/> hook is ours. The SDK sets that hook to identity and does not
/// expose it on <see cref="McpServerToolCreateOptions"/>; the result is still the SDK's own
/// <c>AIFunctionMcpServerTool</c> (made by the public <c>McpServerTool.Create(AIFunction, options)</c>), so
/// invocation, result conversion, structured content and the output schema stay SDK-owned.
/// </summary>
/// <remarks>
/// <para>
/// Replicated from csharp-sdk tag v2.3.0, <c>src/ModelContextProtocol.Core/Server/</c>
/// (plan_wire_result_marshaller.md, "SDK replication map"). The line numbers refer to that tag:
/// <list type="bullet">
/// <item><see cref="CreateFunctionOptions"/> = <c>AIFunctionMcpServerTool.CreateAIFunctionFactoryOptions</c> (:71-110),
/// with <c>RequestServiceProvider&lt;CallToolRequestParams&gt;.IsAugmentedWith</c> (RequestServiceProvider.cs:19-23)
/// replaced by <see cref="AugmentedServiceTypes"/>.</item>
/// <item><see cref="DeriveCreateOptions"/> = <c>DeriveOptions</c> (:161-214). The four internal hint backing fields of
/// <see cref="McpServerToolAttribute"/> (<c>_destructive</c>, <c>_idempotent</c>, <c>_openWorld</c>, <c>_readOnly</c>)
/// are read as "was it set" from <see cref="MemberInfo.GetCustomAttributesData"/> named arguments.</item>
/// <item><see cref="CreateMetadata"/> = <c>CreateMetadata</c> (:386-403).</item>
/// <item><see cref="DeriveName"/> = <c>DeriveName</c> (:338-383); the plan's map omitted it, but
/// <c>CreateAIFunctionFactoryOptions</c> falls back to it when neither the options nor the attribute carry a name.</item>
/// <item>The instance target func <c>args =&gt; CreateTarget(args.Services, toolType)</c> replaces the SDK's cast of
/// <c>args.Services</c> to its internal <c>RequestServiceProvider</c> followed by <c>createTargetFunc(Request)</c>;
/// <c>InvokeAsync</c> (:277-278) sets <c>request.Services</c> to the same provider it puts on <c>args.Services</c>, so
/// the existing <c>CreateTarget(((MessageContext)request).Services, ...)</c> received the identical object.</item>
/// </list>
/// Any later SDK upgrade must re-diff these members against its tag; the parity fixture
/// <c>SentinelToolFactoryParityTests</c> is the guard.
/// </para>
/// <para>
/// <b>MarshalResult semantics (verified 2026-11-02 against Microsoft.Extensions.AI 10.11.0 by
/// <c>SentinelToolFactoryParityTests.MarshalResult_*</c>):</b> the hook runs exactly once per call, after the method has
/// finished, with the <i>awaited</i> value of a <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> method (never the task) and
/// the declared <c>T</c> as the type argument. A sync method gets its value and declared return type. For
/// <c>Task&lt;object&gt;</c> the type is <c>object</c>, so the runtime type must come from the value. A non-generic
/// <c>Task</c> gets a null value. The returned value is what the SDK's result switch (<c>string</c>, <c>ContentBlock</c>,
/// <c>CallToolResult</c>, otherwise JSON text) then sees.
/// </para>
/// </remarks>
public static class SentinelToolFactory
{
    private static readonly Regex NonAsciiLetterDigits = new("[^0-9A-Za-z]+", RegexOptions.Compiled);

    // Wire names of the tools built here, i.e. tools whose results pass through the MarshalResult hook. Names are only
    // ever added: a tool re-created under the same name is still hooked. Read by the offload filter's miss detector.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> HookedToolNames = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="toolName"/> was built by <see cref="Create"/>, so its result went through the marshaller hook.</summary>
    public static bool IsHooked(string? toolName) => toolName is not null && HookedToolNames.ContainsKey(toolName);

    /// <summary>The four service types the SDK's request-scoped service provider answers itself.</summary>
    internal static readonly IReadOnlySet<Type> AugmentedServiceTypes = new HashSet<Type>
    {
        typeof(RequestContext<CallToolRequestParams>),
        typeof(McpServer),
        typeof(IProgress<ProgressNotificationValue>),
        typeof(ClaimsPrincipal),
    };

    /// <summary>
    /// Creates the tool for <paramref name="method"/>. A static method needs no target; an instance method gets a fresh
    /// instance of <paramref name="toolType"/> per invocation from the request's service provider (DI-aware constructor
    /// injection, or <see cref="Activator"/> when there is no provider), like the SDK's <c>WithTools&lt;T&gt;()</c>.
    /// </summary>
    public static McpServerTool Create(
        MethodInfo method,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type toolType,
        McpServerToolCreateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(toolType);

        McpServerToolCreateOptions derived = DeriveCreateOptions(method, options);
        AIFunctionFactoryOptions functionOptions = CreateFunctionOptions(method, derived);

        AIFunction function = method.IsStatic
            ? AIFunctionFactory.Create(method, target: null, functionOptions)
            : AIFunctionFactory.Create(method, args => McpToolSchemaPatcher.CreateTarget(args.Services, toolType), functionOptions);

        HookedToolNames[ResolveToolName(method, derived)] = true;
        return McpServerTool.Create(function, derived);
    }

    /// <summary>
    /// Copy of the SDK's <c>CreateAIFunctionFactoryOptions</c> with the result hook owned here: <c>MarshalResult</c> hands
    /// the result to the <see cref="IToolResultMarshaller"/> resolved from <c>options.Services</c> (the root provider
    /// captured at creation) and is the SDK's identity when there is no provider or no marshaller registered.
    /// </summary>
    internal static AIFunctionFactoryOptions CreateFunctionOptions(MethodInfo method, McpServerToolCreateOptions? options) =>
        new()
        {
            Name = ResolveToolName(method, options),
            Description = options?.Description,
            MarshalResult = (result, returnType, cancellationToken) => MarshalResultAsync(
                options?.Services,
                ResolveToolName(method, options),
                method,
                result,
                returnType,
                options?.SerializerOptions ?? McpJsonUtilities.DefaultOptions,
                cancellationToken),
            SerializerOptions = options?.SerializerOptions ?? McpJsonUtilities.DefaultOptions,
            JsonSchemaCreateOptions = options?.SchemaCreateOptions,
            ConfigureParameterBinding = pi =>
            {
                if (AugmentedServiceTypes.Contains(pi.ParameterType) ||
                    (options?.Services?.GetService<IServiceProviderIsService>() is { } ispis &&
                     ispis.IsService(pi.ParameterType)))
                {
                    return new()
                    {
                        ExcludeFromSchema = true,
                        BindParameter = (pi, args) =>
                            args.Services?.GetService(pi.ParameterType) ??
                            (pi.HasDefaultValue ? null :
                             throw new ArgumentException("No service of the requested type was found.")),
                    };
                }

                if (pi.GetCustomAttribute<FromKeyedServicesAttribute>() is { } keyedAttr)
                {
                    return new()
                    {
                        ExcludeFromSchema = true,
                        BindParameter = (pi, args) =>
                            (args?.Services as IKeyedServiceProvider)?.GetKeyedService(pi.ParameterType, keyedAttr.Key) ??
                            (pi.HasDefaultValue ? null :
                             throw new ArgumentException("No service of the requested type was found.")),
                    };
                }

                return default;
            },
        };

    // The name the SDK gives the tool: explicit option, else the attribute's Name, else the derived snake_case name.
    private static string ResolveToolName(MethodInfo method, McpServerToolCreateOptions? options) =>
        options?.Name ?? method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? DeriveName(method);

    // Resolved on each call from the provider captured at creation, so a marshaller registered after the tool was built
    // is still seen. No provider or no registration = the SDK's identity behaviour.
    private static ValueTask<object?> MarshalResultAsync(
        IServiceProvider? rootServices,
        string toolName,
        MethodInfo method,
        object? result,
        Type? returnType,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken) =>
        rootServices?.GetService<IToolResultMarshaller>() is { } marshaller
            ? marshaller.MarshalAsync(toolName, method, result, returnType, serializerOptions, cancellationToken)
            : new ValueTask<object?>(result);

    /// <summary>Copy of the SDK's <c>DeriveOptions</c>; works on a fresh copy so the caller's options are never mutated.</summary>
    internal static McpServerToolCreateOptions DeriveCreateOptions(MethodInfo method, McpServerToolCreateOptions? options)
    {
        McpServerToolCreateOptions newOptions = CloneOptions(options);

        if (method.GetCustomAttribute<McpServerToolAttribute>() is { } toolAttr)
        {
            IList<CustomAttributeData> attributeData = method.GetCustomAttributesData();

            newOptions.Name ??= toolAttr.Name;
            newOptions.Title ??= toolAttr.Title;

            // The SDK reads the internal nullable backing fields; "was it set" is a named argument of the attribute here.
            if (ReadExplicitHint(attributeData, nameof(McpServerToolAttribute.Destructive)) is bool destructive)
            {
                newOptions.Destructive ??= destructive;
            }

            if (ReadExplicitHint(attributeData, nameof(McpServerToolAttribute.Idempotent)) is bool idempotent)
            {
                newOptions.Idempotent ??= idempotent;
            }

            if (ReadExplicitHint(attributeData, nameof(McpServerToolAttribute.OpenWorld)) is bool openWorld)
            {
                newOptions.OpenWorld ??= openWorld;
            }

            if (ReadExplicitHint(attributeData, nameof(McpServerToolAttribute.ReadOnly)) is bool readOnly)
            {
                newOptions.ReadOnly ??= readOnly;
            }

            if (newOptions.Icons is null && toolAttr.IconSource is { Length: > 0 } iconSource)
            {
                newOptions.Icons = [new() { Source = iconSource }];
            }

            newOptions.UseStructuredContent = toolAttr.UseStructuredContent;

            if (toolAttr.OutputSchemaType is Type outputSchemaType)
            {
                newOptions.OutputSchema ??= AIJsonUtilities.CreateJsonSchema(outputSchemaType,
                    serializerOptions: newOptions.SerializerOptions ?? McpJsonUtilities.DefaultOptions,
                    inferenceOptions: newOptions.SchemaCreateOptions);
            }
        }

        if (method.GetCustomAttribute<DescriptionAttribute>() is { } descAttr)
        {
            newOptions.Description ??= descAttr.Description;
        }

        // Set metadata if not already provided
        newOptions.Metadata ??= CreateMetadata(method);

        return newOptions;
    }

    /// <summary>Copy of the SDK's <c>CreateMetadata</c>: the method, then declaring-type attributes, then method attributes.</summary>
    internal static IReadOnlyList<object> CreateMetadata(MethodInfo method)
    {
        List<object> metadata = [method];

        if (method.DeclaringType is not null)
        {
            metadata.AddRange(method.DeclaringType.GetCustomAttributes());
        }

        metadata.AddRange(method.GetCustomAttributes());

        return metadata.AsReadOnly();
    }

    /// <summary>Copy of the SDK's <c>DeriveName</c> (snake_case of the method name, "Async" suffix dropped for async methods).</summary>
    internal static string DeriveName(MethodInfo method, JsonNamingPolicy? policy = null)
    {
        string name = method.Name;

        const string AsyncSuffix = "Async";
        if (IsAsyncMethod(method) &&
            name.EndsWith(AsyncSuffix, StringComparison.Ordinal) &&
            name.Length > AsyncSuffix.Length)
        {
            name = name.Substring(0, name.Length - AsyncSuffix.Length);
        }

        name = NonAsciiLetterDigits.Replace(name, "_").Trim('_');

        if (name.Length == 0)
        {
            name = method.Name;
        }

        return (policy ?? JsonNamingPolicy.SnakeCaseLower).ConvertName(name) ?? name;

        static bool IsAsyncMethod(MethodInfo method)
        {
            Type t = method.ReturnType;

            if (t == typeof(Task) || t == typeof(ValueTask))
            {
                return true;
            }

            if (t.IsGenericType)
            {
                t = t.GetGenericTypeDefinition();
                if (t == typeof(Task<>) || t == typeof(ValueTask<>) || t == typeof(IAsyncEnumerable<>))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static bool? ReadExplicitHint(IList<CustomAttributeData> attributeData, string propertyName)
    {
        foreach (CustomAttributeData data in attributeData)
        {
            if (data.AttributeType != typeof(McpServerToolAttribute))
            {
                continue;
            }

            foreach (CustomAttributeNamedArgument named in data.NamedArguments)
            {
                if (named.MemberName == propertyName && named.TypedValue.Value is bool value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    // Copy of the SDK's internal McpServerToolCreateOptions.Clone (McpServerToolCreateOptions.cs:203).
    private static McpServerToolCreateOptions CloneOptions(McpServerToolCreateOptions? options) =>
        options is null
            ? new()
            : new()
            {
                Services = options.Services,
                Name = options.Name,
                Description = options.Description,
                Title = options.Title,
                Destructive = options.Destructive,
                Idempotent = options.Idempotent,
                OpenWorld = options.OpenWorld,
                ReadOnly = options.ReadOnly,
                UseStructuredContent = options.UseStructuredContent,
                OutputSchema = options.OutputSchema,
                SerializerOptions = options.SerializerOptions,
                SchemaCreateOptions = options.SchemaCreateOptions,
                Metadata = options.Metadata,
                Icons = options.Icons,
                Meta = options.Meta,
            };
}
