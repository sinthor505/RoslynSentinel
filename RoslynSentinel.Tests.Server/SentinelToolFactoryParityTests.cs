using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Guard for <see cref="SentinelToolFactory"/>, the replica of the SDK's private tool builders (csharp-sdk v2.3.0,
/// plan_wire_result_marshaller.md Step 1). With the identity <c>MarshalResult</c> the replica must be
/// indistinguishable from the SDK-built tool: same protocol tool JSON, same metadata sequence, same call results.
/// A later SDK upgrade that changes the replicated members fails here first.
/// </summary>
[TestFixture]
[Category("SentinelToolFactory")]
public class SentinelToolFactoryParityTests
{
    private static readonly Assembly[] ToolAssemblies =
        [typeof(ServerStatusTools).Assembly, typeof(AdvancedRefactoringTools).Assembly];

    // ---- Microsoft.Extensions.AI MarshalResult semantics (the plan's later steps depend on them) ----

    /// <summary>Methods whose result the MEAI AIFunction hands to MarshalResult; one declared return shape each.</summary>
    public static class MarshalProbe
    {
        public sealed class Payload
        {
            public string Name { get; set; } = "payload";
        }

        public static string SyncString() => "sync";

        public static Task<string> TaskString() => Task.FromResult("task-string");

        public static async ValueTask<Payload> ValueTaskPayload()
        {
            await Task.Yield();
            return new Payload();
        }

        public static async Task<object> TaskObject()
        {
            await Task.Yield();
            return new Payload();
        }

        public static async Task TaskVoid() => await Task.Yield();
    }

    private static async Task<(int Calls, object? Value, Type? DeclaredType)> CaptureMarshalCallAsync(string methodName)
    {
        int calls = 0;
        object? capturedValue = null;
        Type? capturedType = null;

        AIFunction function = AIFunctionFactory.Create(
            typeof(MarshalProbe).GetMethod(methodName)!,
            target: null,
            new AIFunctionFactoryOptions
            {
                MarshalResult = (result, type, _) =>
                {
                    calls++;
                    capturedValue = result;
                    capturedType = type;
                    return new ValueTask<object?>(result);
                },
            });

        await function.InvokeAsync(new AIFunctionArguments(), TestContext.CurrentContext.CancellationToken);
        return (calls, capturedValue, capturedType);
    }

    [Test]
    public async Task MarshalResult_SyncMethod_ReceivesTheValueAndDeclaredType()
    {
        var (calls, value, type) = await CaptureMarshalCallAsync(nameof(MarshalProbe.SyncString));

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(value, Is.EqualTo("sync"));
        Assert.That(type, Is.EqualTo(typeof(string)));
    }

    [Test]
    public async Task MarshalResult_TaskOfT_ReceivesTheAwaitedValueAndT()
    {
        var (calls, value, type) = await CaptureMarshalCallAsync(nameof(MarshalProbe.TaskString));

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(value, Is.EqualTo("task-string"), "the awaited result, not the Task");
        Assert.That(type, Is.EqualTo(typeof(string)), "T, not Task<T>");
    }

    [Test]
    public async Task MarshalResult_ValueTaskOfT_ReceivesTheAwaitedValueAndT()
    {
        var (calls, value, type) = await CaptureMarshalCallAsync(nameof(MarshalProbe.ValueTaskPayload));

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(value, Is.TypeOf<MarshalProbe.Payload>(), "the awaited result, not the ValueTask");
        Assert.That(type, Is.EqualTo(typeof(MarshalProbe.Payload)), "T, not ValueTask<T>");
    }

    [Test]
    public async Task MarshalResult_TaskOfObject_ReceivesTheRuntimeValueAndDeclaredObject()
    {
        var (calls, value, type) = await CaptureMarshalCallAsync(nameof(MarshalProbe.TaskObject));

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(value, Is.TypeOf<MarshalProbe.Payload>(), "the runtime instance");
        Assert.That(type, Is.EqualTo(typeof(object)), "the declared T, so the runtime type must come from the value");
    }

    [Test]
    public async Task MarshalResult_NonGenericTask_ReceivesNull()
    {
        var (calls, value, _) = await CaptureMarshalCallAsync(nameof(MarshalProbe.TaskVoid));

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(value, Is.Null);
    }

    // ---- switch ----

    [TestCase(null, ResultMarshallerMode.On)]
    [TestCase("", ResultMarshallerMode.On)]
    [TestCase("   ", ResultMarshallerMode.On)]
    [TestCase("off", ResultMarshallerMode.Off)]
    [TestCase(" OFF ", ResultMarshallerMode.Off)]
    [TestCase("nonsense", ResultMarshallerMode.On)]
    [TestCase("passthrough", ResultMarshallerMode.Passthrough)]
    [TestCase(" PassThrough ", ResultMarshallerMode.Passthrough)]
    [TestCase("on", ResultMarshallerMode.On)]
    [TestCase("ON", ResultMarshallerMode.On)]
    public void ResultMarshallerOptions_Parse_MapsTheDocumentedValues(string? raw, ResultMarshallerMode expected) =>
        Assert.That(ResultMarshallerOptions.Parse(raw), Is.EqualTo(expected));

    // ---- parity over every declared tool ----

    private static string ProtocolJson(McpServerTool tool) =>
        JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        System.Collections.IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(FormatValue)) + "]",
        _ => value.ToString() ?? string.Empty,
    };

    // Items are compared by type and, for attributes, by their public property values ("TypeId" is per-instance identity).
    private static string DescribeMetadataItem(object item)
    {
        if (item is MethodInfo method)
        {
            return "method:" + method.DeclaringType?.FullName + "." + method.Name + "(" +
                string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
        }

        var properties = item.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "TypeId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                string rendered;
                try
                {
                    rendered = FormatValue(p.GetValue(item));
                }
                catch (Exception ex)
                {
                    rendered = "<throws " + ex.GetType().Name + ">";
                }

                return p.Name + "=" + rendered;
            });

        return item.GetType().FullName + "(" + string.Join(";", properties) + ")";
    }

    private static IEnumerable<string> ParityMismatches(IServiceProvider? services)
    {
        foreach (var (toolName, toolType, method) in McpToolSchemaPatcher.DiscoverToolMethods(ToolAssemblies))
        {
            McpServerTool sdk = McpToolSchemaPatcher.CreateSentinelTool(method, toolType, services!, null, useReplicatedFactory: false);
            McpServerTool replica = McpToolSchemaPatcher.CreateSentinelTool(method, toolType, services!, null, useReplicatedFactory: true);

            if (ProtocolJson(sdk) != ProtocolJson(replica))
            {
                yield return toolName + " (" + toolType.Name + "): ProtocolTool JSON differs" +
                    Environment.NewLine + "  sdk:     " + ProtocolJson(sdk) +
                    Environment.NewLine + "  replica: " + ProtocolJson(replica);
            }

            string[] sdkMetadata = sdk.Metadata.Select(DescribeMetadataItem).ToArray();
            string[] replicaMetadata = replica.Metadata.Select(DescribeMetadataItem).ToArray();
            if (!sdkMetadata.SequenceEqual(replicaMetadata, StringComparer.Ordinal))
            {
                yield return toolName + " (" + toolType.Name + "): Metadata differs" +
                    Environment.NewLine + "  sdk:     " + string.Join(" | ", sdkMetadata) +
                    Environment.NewLine + "  replica: " + string.Join(" | ", replicaMetadata);
            }
        }
    }

    [Test]
    public void EveryDeclaredTool_ReplicaMatchesSdk_WithAServiceProvider()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.That(McpToolSchemaPatcher.DiscoverToolMethods(ToolAssemblies).Count, Is.GreaterThan(50));
        Assert.That(ParityMismatches(services).Take(5).ToList(), Is.Empty);
    }

    [Test]
    public void EveryDeclaredTool_ReplicaMatchesSdk_WithNoServiceProvider()
    {
        // ToolSignatureCatalogReflection builds tools with services: null.
        Assert.That(ParityMismatches(null).Take(5).ToList(), Is.Empty);
    }

    // ---- invocation parity through an in-memory server ----

    public interface IParityGreeter
    {
        string Greeting { get; }
    }

    public sealed class ParityGreeter : IParityGreeter
    {
        public string Greeting => "hello from DI";
    }

    public sealed class ParityProbeResult
    {
        public string Kind { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string? Greeting { get; set; }

        public bool HasServer { get; set; }
    }

    public static class ParityStaticProbe
    {
        [McpServerTool(Name = "ParityStaticProbe")]
        [System.ComponentModel.Description("Test-only static probe.")]
        public static ParityProbeResult Echo(string text) => new() { Kind = "static", Text = text };
    }

    public sealed class ParityInstanceProbe(IParityGreeter greeter)
    {
        [McpServerTool(Name = "ParityInstanceProbe")]
        [System.ComponentModel.Description("Test-only instance probe with constructor injection.")]
        public ParityProbeResult Greet(string text, McpServer server) =>
            new() { Kind = "instance", Text = text, Greeting = greeter.Greeting, HasServer = server is not null };
    }

    public static class ParityTaskProbe
    {
        [McpServerTool(Name = "ParityTaskProbe")]
        [System.ComponentModel.Description("Test-only probe run under a task-eligible tool name.")]
        public static async Task<ParityProbeResult> Work(string text)
        {
            await Task.Delay(10);
            return new() { Kind = "task", Text = text };
        }
    }

    // (declaring type, method, wire tool name). The task probe runs under a name TaskEnabledToolsHelper makes task-eligible.
    private static readonly (Type Type, string Method, string WireName)[] Probes =
    [
        (typeof(ParityStaticProbe), nameof(ParityStaticProbe.Echo), "ParityStaticProbe"),
        (typeof(ParityInstanceProbe), nameof(ParityInstanceProbe.Greet), "ParityInstanceProbe"),
        (typeof(ParityTaskProbe), nameof(ParityTaskProbe.Work), TaskEligibleName),
    ];

    private static string TaskEligibleName => TaskEnabledToolsHelper.Names.Order(StringComparer.Ordinal).First();

    private static McpServerTool BuildProbeTool(bool replicated, Type type, string method, string wireName, IServiceProvider services)
    {
        MethodInfo methodInfo = type.GetMethod(method)!;
        McpServerToolCreateOptions options = new()
        {
            Services = services,
            Name = wireName,
            SchemaCreateOptions = McpToolSchemaPatcher.SchemaCreateOptions,
        };

        if (replicated)
        {
            return SentinelToolFactory.Create(methodInfo, type, options);
        }

        return methodInfo.IsStatic
            ? McpServerTool.Create(methodInfo, target: null, options)
            : McpServerTool.Create(
                methodInfo,
                createTargetFunc: (RequestContext<CallToolRequestParams> request) =>
                    ActivatorUtilities.CreateInstance(((MessageContext)request).Services!, type),
                options);
    }

    // Calls every probe once (the task probe also through task polling) and returns the serialized results.
    private static async Task<List<string>> CallProbesAsync(bool replicated)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<IParityGreeter, ParityGreeter>();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        foreach (var probe in Probes)
        {
            var captured = probe;
            services.AddSingleton<McpServerTool>(sp => BuildProbeTool(replicated, captured.Type, captured.Method, captured.WireName, sp));
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        _ = host.RunAsync();

        try
        {
            var clientTransport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);
            await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

            var results = new List<string>();
            foreach (var probe in Probes)
            {
                var result = await client.CallToolAsync(
                    probe.WireName,
                    new Dictionary<string, object?> { ["text"] = "hello " + probe.WireName },
                    cancellationToken: TestContext.CurrentContext.CancellationToken);

                Assert.That(result.IsError, Is.Not.True, probe.WireName);
                results.Add(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions));
            }

            // Same tool as a task: the hook runs inside the task wrapper too. Only the content is compared (the
            // task result carries per-run task metadata).
            var polled = await client.CallToolWithPollingAsync(
                new CallToolRequestParams
                {
                    Name = TaskEligibleName,
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("hello " + TaskEligibleName),
                    },
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            Assert.That(polled.IsError, Is.Not.True, "polled " + TaskEligibleName);
            results.Add(JsonSerializer.Serialize(polled.Content, McpJsonUtilities.DefaultOptions));

            return results;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Test]
    public async Task ThreeProbeTools_ReturnIdenticalCallToolResults_BothWays()
    {
        Assert.That(TaskEnabledToolsHelper.Names, Does.Contain(TaskEligibleName));

        List<string> sdk = await CallProbesAsync(replicated: false);
        List<string> replica = await CallProbesAsync(replicated: true);

        Assert.That(sdk, Has.Count.EqualTo(Probes.Length + 1));
        Assert.That(sdk[1], Does.Contain("hello from DI"), "the instance probe got its constructor dependency from DI");
        Assert.That(replica, Is.EqualTo(sdk));
    }
}
