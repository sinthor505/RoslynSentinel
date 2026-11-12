using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Pipeline check for the error-policy side channel: a real <c>Search(mode: symbol)</c> miss over the
/// MCP client/server pair gets the <c>SymbolNotFound.Lookup</c> pointer as a trailing text block in
/// pointer mode, and no trailing block in off mode. Uses the real <c>PersistentWorkspaceManager</c>
/// load of a temp copy of the sample solution, with no tasks and no LLM client.
/// </summary>
// ErrorPolicyOptions is static process state: Configure() runs in SetUp and resets to off in TearDown,
// so this fixture must not run in parallel with other tests that depend on it.
[TestFixture]
[NonParallelizable]
public class ErrorPolicySymbolNotFoundPipelineTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private bool _hostRunning;

    // "Workspace" enables WorkspaceReadNavigationTools, which declares the real Search tool.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        await StartHostAsync("pointer");
    }

    [TearDown]
    public async Task TearDown()
    {
        await StopHostAsync();
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        _fixture.Dispose();
    }

    [Test]
    public async Task SearchSymbol_MissingMethod_InPointerMode_AppendsSymbolNotFoundLookupPointer()
    {
        var result = await CallMissingSymbolSearchAsync();

        Assert.That(result.IsError, Is.True, "A missing method must come back as an error result.");

        var textBlocks = result.Content.OfType<TextContentBlock>().ToList();
        Assert.That(result.Content.Count, Is.EqualTo(2), "Pointer mode should add exactly one trailing text block to the error.");
        Assert.That(textBlocks, Has.Count.EqualTo(2));

        using var first = JsonDocument.Parse(textBlocks[0].Text);
        Assert.That(first.RootElement.GetProperty("errorData").GetProperty("errorCode").GetString(), Is.EqualTo("NotFound"));

        Assert.That(
            textBlocks[1].Text,
            Is.EqualTo(ErrorPolicyFilter.PointerText(ErrorPolicyKey.SymbolNotFoundLookup)),
            "The trailing block should be the SymbolNotFound.Lookup pointer text.");
    }

    [Test]
    public async Task SearchSymbol_MissingMethod_InOffMode_HasNoTrailingBlock()
    {
        await StopHostAsync();
        await StartHostAsync("off");

        var result = await CallMissingSymbolSearchAsync();

        Assert.That(result.IsError, Is.True, "A missing method must come back as an error result.");
        Assert.That(result.Content.Count, Is.EqualTo(1), "Off mode must not add a trailing text block.");
    }

    private async Task StartHostAsync(string errorPolicyMode)
    {
        // Configure before the services are built: the error-policy filter is registered at build time, based on Mode.
        ErrorPolicyOptions.Configure(new[] { $"--error-policy-mode={errorPolicyMode}" });

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();
        _hostRunning = true;

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    private async Task StopHostAsync()
    {
        if (!_hostRunning)
        {
            return;
        }

        _hostRunning = false;
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<CallToolResult> CallMissingSymbolSearchAsync()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["reason"] = "error-policy pipeline check for a missing method",
            ["mode"] = "symbol",
            ["query"] = "NoSuchMethodAnywhereXyz",
            ["symbolKind"] = "method",
        };

        return await _client.CallToolAsync(
            "Search",
            arguments!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
    }
}
