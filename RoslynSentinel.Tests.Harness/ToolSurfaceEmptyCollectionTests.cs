// Zero-tool host pin: a registration that serves no tools still has a
// tool collection, a controller at snapshot version 1 with no rows, a working runtime apply, and advertises the tools
// capability. ToolSurfaceGolden.ListToolsAsync disposes its host before returning, so this file builds its own host that
// stays alive for the assertions (same steps as ListToolsAsync).
using System.IO.Pipelines;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions and ReplaceSnippetOptions and the ROSLYNSENTINEL_* environment
public class ToolSurfaceEmptyCollectionTests
{
    // Same set as ToolSurfaceParityTests.AllModes (ServerStdio.cs builds it the same way).
    private static readonly HashSet<string> AllModes =
        new(ToolClassRegistry.ModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

    // DeclarationTools registers nothing outside claude-lean; hiding McpServerStatus removes the always-on tool.
    private const string ZeroToolArgv = "--include-tools=DeclarationTools --hide-tools=McpServerStatus";

    // Environment variables that feed the process statics reset below; saved in SetUp and restored in TearDown.
    private readonly Dictionary<string, string?> _savedEnvironment = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        _savedEnvironment.Clear();
        foreach (var name in ControlledEnvironmentVariables())
        {
            _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        ResetStatics();
    }

    [TearDown]
    public void TearDown()
    {
        // Statics first, while the variables are still cleared, so they return to their defaults rather than to the caller's environment.
        ResetStatics();
        foreach (var (name, value) in _savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _savedEnvironment.Clear();
    }

    private static IEnumerable<string> ControlledEnvironmentVariables()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "ROSLYNSENTINEL_SCHEMA_PROFILE", "ROSLYNSENTINEL_HIDE_PARAMS" };
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("ROSLYNSENTINEL_REPLACE_SNIPPET_", StringComparison.Ordinal))
            {
                names.Add(key);
            }
        }

        return names;
    }

    private static void ResetStatics()
    {
        // Both Configure calls read the environment, which SetUp has cleared, so they land on the built-in defaults.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        ReplaceSnippetOptions.Configure([]);
    }

    /// <summary>Stand-in for the LM Studio client: the engines need an ILlmClient (same stub as ToolSurfaceGolden).</summary>
    private sealed class StubLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default) =>
            Task.FromResult("stub");
    }

    /// <summary>A started in-process host and the client connected to it; disposing stops both.</summary>
    private sealed class LiveServer : IAsyncDisposable
    {
        public LiveServer(IHost host, McpClient client)
        {
            Host = host;
            Client = client;
        }

        public IHost Host { get; }

        public McpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.StopAsync(CancellationToken.None);
            Host.Dispose();
        }
    }

    /// <summary>
    /// Starts an in-process Basic host the way ToolSurfaceGolden.ListToolsAsync does (engines Advanced, stream transport over
    /// two pipes, ValidateOnBuild, stub ILlmClient), with the argv parsed as ServerStdio.Startup parses it, and keeps it running.
    /// </summary>
    private static async Task<LiveServer> StartBasicAsync(string argv, CancellationToken cancellationToken)
    {
        var args = argv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ServerStartupHelpers.ParseArgs(
            args, AllModes, out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);
        var hideTools = ServerStartupHelpers.ParseHideTools(args);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<ILlmClient, StubLlmClient>();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, includeTools, excludeTools, hideTools);

        var hostBuilder = Host.CreateApplicationBuilder();
        ServerStartupHelpers.EnableValidateOnBuild(hostBuilder);
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        var host = hostBuilder.Build();
        try
        {
            await host.StartAsync(cancellationToken);
            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);
            var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return new LiveServer(host, client);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>Builds GitRemote the way the server builds its startup tools (McpToolSchemaPatcher.CreateSentinelTool).</summary>
    private static McpServerTool? CreateGitRemoteTool(string toolName, IServiceProvider services)
    {
        var method = typeof(GitRemoteTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        return method is null ? null : McpToolSchemaPatcher.CreateSentinelTool(method, typeof(GitRemoteTools), services);
    }

    [Test]
    public async Task ZeroToolHost_HasAnEmptyCollection_AndAWorkingController()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await using var server = await StartBasicAsync(ZeroToolArgv, cancellationToken);
        var services = server.Host.Services;

        var collection = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
        Assert.That(collection, Is.Not.Null.And.Empty);

        var controller = services.GetRequiredService<ToolSurfaceController>();
        var snapshot = controller.Current;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Version, Is.EqualTo(1));
            Assert.That(snapshot.Tools, Is.Empty);
        });

        var change = controller.ApplyToolset(ToolSetName.git, true, ["GitRemote"], name => CreateGitRemoteTool(name, services));
        Assert.Multiple(() =>
        {
            Assert.That(change.AddedTools, Is.EqualTo(new[] { "GitRemote" }));
            Assert.That(collection!.TryGetPrimitive("GitRemote", out _), Is.True);
        });
    }

    [Test]
    public async Task ZeroToolHost_AdvertisesTheToolsCapability_AndListsNothing()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await using var server = await StartBasicAsync(ZeroToolArgv, cancellationToken);

        Assert.That(server.Client.ServerCapabilities?.Tools, Is.Not.Null);

        var tools = await server.Client.ListToolsAsync(cancellationToken: cancellationToken);
        Assert.That(tools, Is.Empty);
    }
}
