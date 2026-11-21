using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Stages files into a freshly built world. Receives the fixture on disk, the in-process server's workspace manager
/// and the caller's cancellation token.
/// </summary>
internal delegate Task ModelEvalStaging(
    RoslynSentinel.Tests.TestSolutionFixture fixture,
    IWorkspaceManager workspaceManager,
    CancellationToken cancellationToken);

/// <summary>
/// One isolated world for a live-model run: a fixture on disk, an in-process MCP server (Advanced engines, the
/// <see cref="ChainEvalToolSurface"/> tool surface) on two pipes, a generic host carrying the LM Studio agent client and an
/// agent.log in the sample directory, an MCP client on the other end of the pipes, and the agent client from DI.
/// <see cref="DisposeAsync"/> disposes the client, stops and disposes the host, then disposes the fixture.
/// </summary>
internal sealed class ModelEvalMcpEnvironment : IAsyncDisposable
{
    private IHost? _host;
    private McpClient? _client;
    private RoslynSentinel.Tests.TestSolutionFixture? _fixture;

    private ModelEvalMcpEnvironment()
    {
    }

    public IHost Host => _host!;

    public McpClient Client => _client!;

    public LmStudioAgentClient AgentClient { get; private set; } = null!;

    public RoslynSentinel.Tests.TestSolutionFixture Fixture => _fixture!;

    public string SolutionDirectory => _fixture!.SolutionDirectory;

    /// <summary>
    /// Builds the world. <paramref name="beforeLoad"/> runs after the host is running but before the solution is loaded, so
    /// the server never sees its writes (a golden checkout). <paramref name="afterLoad"/> runs after the solution is loaded,
    /// through the workspace manager (staged files, a baseline test run), and before the MCP client is created.
    /// <paramref name="configureToolSurface"/> replaces the default tool registration
    /// (<c>AddRoslynSentinelToolsAdvanced</c> with the <see cref="ChainEvalToolSurface"/> modes and hidden tools) when a run
    /// needs another surface or request filters; it is called at the same point in the builder chain.
    /// If any step throws, the partly built world is disposed before the exception propagates.
    /// </summary>
    public static async Task<ModelEvalMcpEnvironment> CreateAsync(
        string sampleDirectory,
        bool restorePackages,
        ModelEvalStaging? beforeLoad,
        ModelEvalStaging? afterLoad,
        CancellationToken cancellationToken,
        Action<IMcpServerBuilder, IServiceCollection>? configureToolSurface = null)
    {
        var environment = new ModelEvalMcpEnvironment();
        try
        {
            await environment.InitializeAsync(sampleDirectory, restorePackages, beforeLoad, afterLoad, configureToolSurface, cancellationToken);
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(
        string sampleDirectory,
        bool restorePackages,
        ModelEvalStaging? beforeLoad,
        ModelEvalStaging? afterLoad,
        Action<IMcpServerBuilder, IServiceCollection>? configureToolSurface,
        CancellationToken cancellationToken)
    {
        // restorePackages: ContosoOrders.Tests needs restore assets or a solution-scope build reports CS0246 for every Xunit file
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        if (configureToolSurface is null)
        {
            mcpBuilder.AddRoslynSentinelToolsAdvanced(
                services, ChainEvalToolSurface.NewModes(),
                hideTools: ChainEvalToolSurface.NewHiddenTools());
        }
        else
        {
            configureToolSurface(mcpBuilder, services);
        }

        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(sampleDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync(CancellationToken.None); // stopped by DisposeAsync, not by the test's token

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        if (beforeLoad is not null)
        {
            await beforeLoad(_fixture, workspaceManager, cancellationToken);
        }

        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, cancellationToken);
        if (afterLoad is not null)
        {
            await afterLoad(_fixture, workspaceManager, cancellationToken);
        }

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);
        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);
        AgentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();
    }
}
