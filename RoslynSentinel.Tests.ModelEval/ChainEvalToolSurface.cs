using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

// Both chain tests and the transcript-resume tests use this so the resume environment matches.
// DeleteFile is kept; the other WholeFileWrite tools (WriteFile, ApplyDiff, ApplyUnifiedDiff) and the
// git/project tools are hidden because they are useless on a temp fixture and cost schema tokens.
public static class ChainEvalToolSurface
{
    // Fresh set per call: the registration call may keep the instance.
    public static HashSet<string> NewModes() =>
        new(new[] { "Refactor", "Workspace", "WholeFileWrite" }, StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> NewHiddenTools() =>
        new(
            new[]
            {
                "Features", "McpServerStatus", "WriteFile", "ApplyDiff", "ApplyUnifiedDiff", "Git", "GitRemote",
                "ListWorkspaceSolutions", "LoadSolution", "CreateProject", "SplitProjectByFolder",
            },
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Checks the tool list that <see cref="ChainEvalToolSurface"/> produces, through the real in-process MCP server.
/// No LM Studio needed.
/// </summary>
[TestFixture]
public class ChainEvalToolSurfaceTests
{
    private IHost _host = null!;
    private McpClient _mcpClient = null!;

    [SetUp]
    public async Task SetUp()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(
            services,
            ChainEvalToolSurface.NewModes(),
            hideTools: ChainEvalToolSurface.NewHiddenTools());

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

        _mcpClient = await McpClient.CreateAsync(
            clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Test]
    public async Task OfferedTools_HaveDeleteFile_AndNoWriteFileGitOrProjectTools()
    {
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("DeleteFile"), "DeleteFile must be offered (self-made duplicate files)");
            Assert.That(names, Does.Contain("ReplaceSnippet"), "ReplaceSnippet must be offered");
            Assert.That(names, Does.Contain("Member"), "Member must be offered");
            Assert.That(names, Does.Contain("GetFileOutline"), "GetFileOutline must be offered");
            Assert.That(names, Does.Contain("RenameSymbol"), "RenameSymbol must be offered");

            Assert.That(names, Does.Not.Contain("WriteFile"), "WriteFile must be hidden");
            Assert.That(names, Does.Not.Contain("ApplyDiff"), "ApplyDiff must be hidden");
            Assert.That(names, Does.Not.Contain("ApplyUnifiedDiff"), "ApplyUnifiedDiff must be hidden");
            Assert.That(names, Does.Not.Contain("Git"), "Git must be hidden");
            Assert.That(names, Does.Not.Contain("GitRemote"), "GitRemote must be hidden");
            Assert.That(names, Does.Not.Contain("ListWorkspaceSolutions"), "ListWorkspaceSolutions must be hidden");
            Assert.That(names, Does.Not.Contain("LoadSolution"), "LoadSolution must be hidden");
            Assert.That(names, Does.Not.Contain("CreateProject"), "CreateProject must be hidden");
            Assert.That(names, Does.Not.Contain("SplitProjectByFolder"), "SplitProjectByFolder must be hidden");
            Assert.That(names, Does.Not.Contain("Features"), "Features must be hidden");
            Assert.That(names, Does.Not.Contain("McpServerStatus"), "McpServerStatus must be hidden");
        });
    }
}
