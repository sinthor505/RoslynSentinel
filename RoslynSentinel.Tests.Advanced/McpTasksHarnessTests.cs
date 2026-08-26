using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Proves the in-memory client/server harness pattern for testing the MCP Tasks extension end to
/// end (real JSON-RPC over paired streams, not direct tool-class calls) against <c>Features</c> —
/// a trivial, dependency-free tool with a test-only <c>delaySeconds</c> knob — before extending
/// coverage to task-eligible tools with real work (BulkComment, Asyncify, AsyncifyLoop).
/// </summary>
/// <remarks>
/// See [[project_mcp_tasks_test_harness_plan]]: no other test project drives the server through a
/// real <see cref="McpClient"/> yet, so this fixture is also the first proof that the pattern
/// (paired <see cref="Pipe"/>s + <c>WithStreamServerTransport</c> + <see cref="StreamClientTransport"/>)
/// works at all, independent of whether MCP Tasks specifically behaves correctly.
/// </remarks>
[TestFixture]
public class McpTasksHarnessTests
{
    private IHost _host = null!;
    private McpClient _client = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    [SetUp]
    public async Task SetUp()
    {
        // Client-to-server and server-to-client pipes. Naming mirrors ServerStdio.cs's
        // --interactive setup, just with the client/server roles reversed for who reads/writes which end.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Test]
    public async Task PlainCall_ReturnsNormalResult_NoTaskInvolved()
    {
        var result = await _client.CallToolAsync(
            "Features",
            new Dictionary<string, object?> { ["action"] = "list" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.Content, Is.Not.Empty);
    }

    [Test]
    public async Task TaskCapableClient_CallingTaskEligibleTool_ReturnsCreateTaskResult()
    {
        // Default McpClientOptions.ProtocolVersion (null) negotiates 2026-07-28 when the server
        // supports it, which is what makes CallToolAsTaskAsync inject the tasks capability.
        var requestParams = new CallToolRequestParams
        {
            Name = "Features",
            Arguments = ToArguments(new Dictionary<string, object?> { ["action"] = "list", ["delaySeconds"] = 5 }),
        };

        var augmented = await _client.CallToolAsTaskAsync(requestParams, TestContext.CurrentContext.CancellationToken);

        Assert.That(augmented.IsTask, Is.True, "Expected a task-eligible tool call under a tasks-capable client to return CreateTaskResult.");
        Assert.That(augmented.TaskCreated!.TaskId, Is.Not.Empty);
    }

    [Test]
    public async Task TaskCapableClient_PollingToCompletion_MatchesSynchronousResult()
    {
        var syncResult = await _client.CallToolAsync(
            "Features",
            new Dictionary<string, object?> { ["action"] = "list" }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var polledResult = await _client.CallToolWithPollingAsync(
            new CallToolRequestParams
            {
                Name = "Features",
                Arguments = ToArguments(new Dictionary<string, object?> { ["action"] = "list", ["delaySeconds"] = 3 }),
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(polledResult.IsError, Is.Not.True);
        Assert.That(
            SerializeContent(polledResult),
            Is.EqualTo(SerializeContent(syncResult)),
            "Task-backed and synchronous calls to the same tool/arguments should return equivalent content.");
    }

    [Test]
    public async Task CancelTask_MidFlight_StopsBeforeCompletionAndReportsCancelled()
    {
        var requestParams = new CallToolRequestParams
        {
            Name = "Features",
            Arguments = ToArguments(new Dictionary<string, object?> { ["action"] = "list", ["delaySeconds"] = 15 }),
        };

        var augmented = await _client.CallToolAsTaskAsync(requestParams, TestContext.CurrentContext.CancellationToken);
        Assert.That(augmented.IsTask, Is.True);

        var taskId = augmented.TaskCreated!.TaskId;

        // Cancel well before the 15s delay would naturally finish, so "cancelled early" and
        // "ran to completion" are unambiguous.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.CurrentContext.CancellationToken);
        await _client.CancelTaskAsync(taskId, TestContext.CurrentContext.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        GetTaskResult status;
        do
        {
            status = await _client.GetTaskAsync(taskId, TestContext.CurrentContext.CancellationToken);
            if (status is CancelledTaskResult)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.CurrentContext.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        Assert.That(status, Is.InstanceOf<CancelledTaskResult>(), "Expected the task to reach Cancelled status after tasks/cancel.");
    }

    private static string SerializeContent(CallToolResult result) =>
        JsonSerializer.Serialize(result.Content);

    private static IDictionary<string, JsonElement> ToArguments(IDictionary<string, object?> arguments) =>
        arguments.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.SerializeToElement(kvp.Value));
}
