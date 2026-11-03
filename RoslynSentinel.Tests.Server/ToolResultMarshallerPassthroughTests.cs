using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Step 2 of plan_wire_result_marshaller.md: the pass-through <see cref="ToolResultMarshaller"/>. Unit cases pin which
/// shapes are returned unchanged and what happens on failure; the in-memory-server case pins that, with the marshaller
/// registered, the call result text is byte-identical to what the SDK produces without it.
/// </summary>
[TestFixture]
[Category("SentinelToolFactory")]
public class ToolResultMarshallerPassthroughTests
{
    private static readonly MethodInfo AnyMethod = typeof(ToolResultMarshallerPassthroughTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AnyMethodTarget()
    {
    }

    // ---- unit: shapes returned unchanged ----

    public static IEnumerable<TestCaseData> UnchangedShapes()
    {
        yield return new TestCaseData(null).SetName("null");
        yield return new TestCaseData("already text").SetName("string");
        yield return new TestCaseData(new TextContentBlock { Text = "block" }).SetName("ContentBlock");
        yield return new TestCaseData(new TextContent("ai")).SetName("AIContent");
        yield return new TestCaseData(new CallToolResult { Content = [new TextContentBlock { Text = "ctr" }] }).SetName("CallToolResult");
        yield return new TestCaseData(new List<ContentBlock> { new TextContentBlock { Text = "b" } }).SetName("IEnumerable<ContentBlock>");
        yield return new TestCaseData(new List<AIContent> { new TextContent("a") }).SetName("IEnumerable<AIContent>");
    }

    [TestCaseSource(nameof(UnchangedShapes))]
    public async Task MarshalAsync_SdkContentShapes_AreReturnedUnchanged(object? result)
    {
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);

        object? marshalled = await marshaller.MarshalAsync(
            "Probe", AnyMethod, result, result?.GetType(), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(marshalled, Is.SameAs(result));
    }

    [Test]
    public async Task MarshalAsync_OtherShapes_AreSerializedOnce_ExactlyLikeTheSdkDefaultBranch()
    {
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = new List<int> { 1, 2, 3 }, StatusMessage = "caf\u00e9 <b> & 'q'" };

        object? marshalled = await marshaller.MarshalAsync(
            "Probe", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(marshalled, Is.TypeOf<string>());
        Assert.That(marshalled, Is.EqualTo(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)))));
    }

    // ---- unit: never throws ----

    // Serializing this throws (the getter), which is the failure the marshaller must absorb.
    public sealed class ThrowingPayload
    {
        public string Value => throw new InvalidOperationException("secret-internal-detail C:\\internal\\path");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    [Test]
    public async Task MarshalAsync_WhenSerializationThrows_ReturnsTheOriginalAndLogsAtErrorLevel()
    {
        var logger = new CapturingLogger<ToolResultMarshaller>();
        var marshaller = new ToolResultMarshaller(logger);
        var payload = new ThrowingPayload();

        object? marshalled = await marshaller.MarshalAsync(
            "Probe", AnyMethod, payload, typeof(ThrowingPayload), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(marshalled, Is.SameAs(payload), "the SDK then serializes (and fails on) the original exactly as it does today");
        Assert.That(logger.Entries, Has.Count.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Error));
        Assert.That(logger.Entries[0].Exception, Is.Not.Null);
        Assert.That(logger.Entries[0].Message, Does.Contain("Probe"));
        Assert.That(logger.Entries[0].Message, Does.Not.Contain("secret-internal-detail"), "exception text goes to the exception, not the message");
    }

    // ---- byte identity through the in-memory server ----

    // Hand-built instances of the real result envelope and DTO types the named tools return (Search, ListAll, Member,
    // Build, an error): the real tools need a loaded workspace, which Tests.Server does not have.
    public static class FixtureTools
    {
        [McpServerTool(Name = "FixtureSearch")]
        public static Task<SentinelCallToolResult<object>> Search(string text) =>
            Task.FromResult(new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new
                {
                    Mode = "text",
                    Query = text,
                    RegexResults = Enumerable.Range(1, 300).Select(i => new
                    {
                        FilePath = $"RoslynSentinel.Common/File{i}.cs",
                        Line = i,
                        Column = i % 40,
                        Preview = $"var s = \"caf\u00e9 <{i}> & 'x' \\ \u2192 \ud83d\ude00\";",
                        EnclosingMember = i % 3 == 0 ? null : "Member" + i,
                    }).ToList(),
                },
                TotalRecords = 300,
                HasMoreData = true,
                NextOffset = 50,
                WorkspaceVersion = 7,
                StatusMessage = "300 matches across 300 files.",
                WarningDetails = "caf\u00e9 <warn> & 'q'",
            });

        [McpServerTool(Name = "FixtureListAll")]
        public static Task<SentinelCallToolResult<object>> ListAll(string text) =>
            Task.FromResult(new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = Enumerable.Range(1, 120)
                    .Select(i => new Dictionary<string, object?>
                    {
                        ["kind"] = i % 2 == 0 ? "method" : "class",
                        ["name"] = "Symbol" + i,
                        ["container"] = null,
                        ["startLine"] = i * 3,
                        ["endLine"] = i * 3 + 2,
                    })
                    .ToList(),
                TotalRecords = 120,
            });

        [McpServerTool(Name = "FixtureMember")]
        public static Task<SentinelCallToolResult<object>> Member(string text) =>
            Task.FromResult(new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new DocumentEditResult(EditOutcome.Modified, "RoslynSentinel.Common/Foo.cs", "change-abc123", isCommitted: true),
                WorkspaceVersion = 9,
            });

        [McpServerTool(Name = "FixtureBuild")]
        public static Task<SentinelCallToolResult<object>> Build(string text) =>
            Task.FromResult(new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new DiagnosticSummary(2, 1, []),
                StatusMessage = "Build Succeeded: 0 error(s), 1 warning(s).",
            });

        [McpServerTool(Name = "FixtureError")]
        public static Task<SentinelCallToolResult<object>> Error(string text) =>
            Task.FromResult(new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.NotFound, "snippet not found: " + text, "closest windows follow", ["window one", "window two"]),
            });

        [McpServerTool(Name = "FixtureString")]
        public static string PlainString(string text) => "plain " + text;

        [McpServerTool(Name = "FixtureCallToolResult")]
        public static CallToolResult Raw(string text) => new() { Content = [new TextContentBlock { Text = "raw " + text }], IsError = true };

        [McpServerTool(Name = "FixtureVoid")]
        public static Task Nothing(string text) => Task.CompletedTask;
    }

    // Wraps the real marshaller to prove the hook ran and what it handed back.
    private sealed class RecordingMarshaller(IToolResultMarshaller inner) : IToolResultMarshaller
    {
        public List<(string Tool, object? Result, object? Marshalled)> Calls { get; } = [];

        public async ValueTask<object?> MarshalAsync(
            string toolName, MethodInfo method, object? result, Type? returnType, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
        {
            object? marshalled = await inner.MarshalAsync(toolName, method, result, returnType, serializerOptions, cancellationToken);
            lock (Calls)
            {
                Calls.Add((toolName, result, marshalled));
            }

            return marshalled;
        }
    }

    private static readonly string[] FixtureToolNames =
        ["FixtureSearch", "FixtureListAll", "FixtureMember", "FixtureBuild", "FixtureError", "FixtureString", "FixtureCallToolResult", "FixtureVoid"];

    private static async Task<List<string>> CallFixturesAsync(bool replicated, RecordingMarshaller? marshaller)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        if (marshaller is not null)
        {
            services.AddSingleton<IToolResultMarshaller>(marshaller);
        }

        foreach (MethodInfo method in typeof(FixtureTools).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            MethodInfo captured = method;
            services.AddSingleton<McpServerTool>(sp =>
                McpToolSchemaPatcher.CreateSentinelTool(captured, typeof(FixtureTools), sp, null, useReplicatedFactory: replicated));
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
            foreach (string toolName in FixtureToolNames)
            {
                var result = await client.CallToolAsync(
                    toolName,
                    new Dictionary<string, object?> { ["text"] = "caf\u00e9 <x> & 'y'" },
                    cancellationToken: TestContext.CurrentContext.CancellationToken);
                results.Add(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions));
            }

            return results;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Test]
    public async Task PassthroughText_IsByteIdenticalToTheSdkDefault_ForRealResultShapes()
    {
        List<string> sdk = await CallFixturesAsync(replicated: false, marshaller: null);

        var recording = new RecordingMarshaller(new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance));
        List<string> passthrough = await CallFixturesAsync(replicated: true, marshaller: recording);

        Assert.That(sdk, Has.Count.EqualTo(FixtureToolNames.Length));
        for (int i = 0; i < FixtureToolNames.Length; i++)
        {
            Assert.That(passthrough[i], Is.EqualTo(sdk[i]), FixtureToolNames[i]);
        }

        // Sanity: the fixtures produced real content, and the hook really ran once per call.
        Assert.That(sdk[0], Does.Contain("RoslynSentinel.Common/File300.cs"));
        Assert.That(sdk[4], Does.Contain("snippet not found"));
        Assert.That(recording.Calls.Select(c => c.Tool), Is.EquivalentTo(FixtureToolNames));

        foreach (var (tool, result, marshalled) in recording.Calls)
        {
            if (result is SentinelCallToolResult<object>)
            {
                Assert.That(marshalled, Is.TypeOf<string>(), tool + ": envelope results become wire text");
            }
            else
            {
                Assert.That(marshalled, Is.SameAs(result), tool + ": SDK content shapes are returned unchanged");
            }
        }
    }

    [Test]
    public async Task ReplicaWithNoMarshallerRegistered_IsByteIdenticalToTheSdkDefault()
    {
        List<string> sdk = await CallFixturesAsync(replicated: false, marshaller: null);
        List<string> replicaIdentity = await CallFixturesAsync(replicated: true, marshaller: null);

        Assert.That(replicaIdentity, Is.EqualTo(sdk));
    }
}
