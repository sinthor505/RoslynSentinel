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
/// Extends the harness proven in <see cref="McpTasksHarnessTests"/> (see
/// [[project_mcp_tasks_test_harness_plan]]) to <c>BulkComment</c> — a task-eligible tool that does
/// real work (LLM-generated doc comments applied to real files), rather than <c>Features</c>' pure
/// delay. Uses <see cref="RoslynSentinel.Tests.TestSolutionFixture"/> (a temp-directory copy of
/// Samples/ContosoOrders) loaded through a real <c>PersistentWorkspaceManager.LoadSolutionAsync</c>,
/// and a <see cref="FakeLlmClient"/> DI override so no real LM Studio process is required and the
/// cancellation test has a controllable delay to cancel mid-flight.
/// </summary>
[TestFixture]
public class McpTasksHarnessBulkCommentTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private FakeLlmClient _fakeLlmClient = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Generation" };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        _fakeLlmClient = new FakeLlmClient();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();
        // Last registration wins for non-enumerable resolution — overrides the real LmStudioClient
        // forwarding registered a few lines above inside AddRoslynSentinelEnginesAdvanced.
        services.AddSingleton<ILlmClient>(_fakeLlmClient);

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

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

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
        _fixture.Dispose();
    }

    [Test]
    public async Task TaskCapableClient_CallingBulkComment_ReturnsCreateTaskResult()
    {
        var requestParams = new CallToolRequestParams
        {
            Name = "BulkComment",
            Arguments = ToArguments(new Dictionary<string, object?> { ["scope"] = "solution", ["dryRun"] = true }),
        };

        var augmented = await _client.CallToolAsTaskAsync(requestParams, TestContext.CurrentContext.CancellationToken);

        Assert.That(augmented.IsTask, Is.True, "Expected a task-eligible tool call under a tasks-capable client to return CreateTaskResult.");
        Assert.That(augmented.TaskCreated!.TaskId, Is.Not.Empty);
    }

    [Test]
    public async Task TaskCapableClient_PollingToCompletion_DryRunMatchesSynchronousResult()
    {
        var arguments = new Dictionary<string, object?> { ["scope"] = "solution", ["dryRun"] = true };

        var syncResult = await _client.CallToolAsync(
            "BulkComment",
            arguments!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var polledResult = await _client.CallToolWithPollingAsync(
            new CallToolRequestParams { Name = "BulkComment", Arguments = ToArguments(arguments) },
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(syncResult.IsError, Is.Not.True);
        Assert.That(polledResult.IsError, Is.Not.True);
        Assert.That(
            SerializeContent(polledResult),
            Is.EqualTo(SerializeContent(syncResult)),
            "Task-backed and synchronous dryRun calls with identical arguments should return equivalent content.");

        using var doc = JsonDocument.Parse(SerializeContent(polledResult));
        var data = FindDataElement(doc.RootElement);
        Assert.That(data.GetProperty("dryRun").GetBoolean(), Is.True);
        Assert.That(data.GetProperty("totalMembers").GetInt32(), Is.GreaterThan(0), "ContosoOrders sample should contain commentable members.");
        Assert.That(data.GetProperty("commentedThisCall").GetInt32(), Is.EqualTo(0), "dryRun must never call the LLM or apply comments.");
        Assert.That(_fakeLlmClient.CallCount, Is.EqualTo(0), "dryRun must short-circuit before any LLM call.");
    }

    [Test]
    public async Task RealRun_CommentsStaleMembers_UsingFakeLlmClient()
    {
        // Scoped to ContosoOrders.Core only: ContosoOrders.Tests references xunit via NuGet, and
        // TestSolutionFixture copies only .sln/.csproj/.cs/.md files (no restore), so that project
        // always has pre-existing unresolved-reference errors. BulkComment's line-shifting edits
        // (inserting [ContentHash] attributes/doc comments) then make ValidationEngine's diagnostic
        // delta miscount those pre-existing errors as newly introduced (its dedup key includes line
        // number, which every edit above a diagnostic shifts) — a real latent bug in the delta
        // comparison, but orthogonal to this harness. A real (non-dryRun) BulkComment run scoped to
        // a project that already compiles cleanly is what a realistic caller would do, and sidesteps
        // it entirely.
        var requestParams = new CallToolRequestParams
        {
            Name = "BulkComment",
            Arguments = ToArguments(new Dictionary<string, object?> { ["scope"] = "project", ["projectName"] = "ContosoOrders.Core", ["dryRun"] = false }),
        };

        var result = await _client.CallToolWithPollingAsync(requestParams, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);

        using var doc = JsonDocument.Parse(SerializeContent(result));
        var data = FindDataElement(doc.RootElement);
        Assert.That(data.GetProperty("commentedThisCall").GetInt32(), Is.GreaterThan(0), "Expected at least one stale member to be commented against the ContosoOrders sample.");
        Assert.That(_fakeLlmClient.CallCount, Is.GreaterThan(0), "Expected the fake LLM client to have been invoked for real (non-dryRun) work.");
    }

    [Test]
    public async Task CancelTask_MidFlight_StopsBeforeCompletionAndReportsCancelled()
    {
        _fakeLlmClient.Delay = TimeSpan.FromSeconds(15);

        var requestParams = new CallToolRequestParams
        {
            Name = "BulkComment",
            Arguments = ToArguments(new Dictionary<string, object?> { ["scope"] = "project", ["projectName"] = "ContosoOrders.Core", ["dryRun"] = false }),
        };

        var augmented = await _client.CallToolAsTaskAsync(requestParams, TestContext.CurrentContext.CancellationToken);
        Assert.That(augmented.IsTask, Is.True);

        var taskId = augmented.TaskCreated!.TaskId;

        // Cancel well before any single fake LLM call (15s delay each) could finish, so "cancelled
        // early" and "ran to completion" are unambiguous.
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

    private static JsonElement FindDataElement(JsonElement contentArray)
    {
        var block = contentArray.EnumerateArray().First();
        var text = block.GetProperty("text").GetString()!;
        using var parsed = JsonDocument.Parse(text);
        return parsed.RootElement.GetProperty("data").Clone();
    }

    private static string SerializeContent(CallToolResult result) =>
        JsonSerializer.Serialize(result.Content);

    private static IDictionary<string, JsonElement> ToArguments(IDictionary<string, object?> arguments) =>
        arguments.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.SerializeToElement(kvp.Value));
}
