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

/// <summary>
/// One isolated world for a non-live "fake model" test: a real in-process MCP server (Advanced engines, tasks store, a tool
/// surface the caller picks) on two pipes, a generic host running it, an MCP client on the other end of the pipes, an
/// optional run directory in the temp folder and an optional <see cref="RoslynSentinel.Tests.TestSolutionFixture"/>. The
/// fake HTTP handler that stands in for the LLM is NOT part of this world: each test builds its own backend or runner around
/// <see cref="Client"/>. <see cref="DisposeAsync"/> disposes the client, stops and disposes the host, deletes the run
/// directory and disposes the fixture. Sibling of <see cref="ModelEvalMcpEnvironment"/>, which serves the live-model runs.
/// </summary>
internal sealed class ModelEvalFakeModelEnvironment : IAsyncDisposable
{
    private IHost? _host;
    private McpClient? _client;
    private RoslynSentinel.Tests.TestSolutionFixture? _fixture;

    private ModelEvalFakeModelEnvironment()
    {
    }

    public IHost Host => _host!;

    public McpClient Client => _client!;

    /// <summary>The temp run directory, or null when no <c>runDirectoryPrefix</c> was given.</summary>
    public string? RunDirectory { get; private set; }

    public RoslynSentinel.Tests.TestSolutionFixture Fixture => _fixture!;

    /// <summary>
    /// Builds the world. <paramref name="runDirectoryPrefix"/>, when non-null, creates a fresh
    /// <c>Path.GetTempPath()/prefix + 8 hex chars</c> directory exposed as <see cref="RunDirectory"/> and deleted on dispose.
    /// <paramref name="configureToolSurface"/> replaces the default registration
    /// (<see cref="ModelEvalToolSurfaces.WorkspaceAndSymbolNavigation"/>) and is called after the transport and tasks store are
    /// wired. <paramref name="withFixture"/> creates a <see cref="RoslynSentinel.Tests.TestSolutionFixture"/> (disposed with
    /// the world) before the server starts; the solution is not loaded. If any step throws, the partly built world is
    /// disposed before the exception propagates.
    /// </summary>
    public static async Task<ModelEvalFakeModelEnvironment> CreateAsync(
        CancellationToken cancellationToken,
        string? runDirectoryPrefix = null,
        Action<IMcpServerBuilder, IServiceCollection>? configureToolSurface = null,
        bool withFixture = false)
    {
        var environment = new ModelEvalFakeModelEnvironment();
        try
        {
            await environment.InitializeAsync(runDirectoryPrefix, configureToolSurface, withFixture, cancellationToken);
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(
        string? runDirectoryPrefix,
        Action<IMcpServerBuilder, IServiceCollection>? configureToolSurface,
        bool withFixture,
        CancellationToken cancellationToken)
    {
        if (runDirectoryPrefix is not null)
        {
            RunDirectory = Path.Combine(Path.GetTempPath(), runDirectoryPrefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(RunDirectory);
        }

        if (withFixture)
        {
            _fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        }

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        (configureToolSurface ?? ModelEvalToolSurfaces.WorkspaceAndSymbolNavigation)(mcpBuilder, services);

        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync(CancellationToken.None); // stopped by DisposeAsync, not by the test's token

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);
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

        if (RunDirectory is not null && Directory.Exists(RunDirectory))
        {
            Directory.Delete(RunDirectory, recursive: true);
        }

        _fixture?.Dispose();
    }
}
