using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for <see cref="ToolArgumentValidator.ApplyParameterAliases"/> and
/// <see cref="ToolArgumentValidator.ParameterAliases"/>: parameter names a model repeatedly supplies
/// instead of the declared one (mined from transcripts with scripts/Get-UnknownParameterReport.ps1)
/// are renamed to the declared parameter, the tool runs on the real value, and the response carries
/// a short note naming the real parameter.
/// <para>
/// Runs against a real in-process <see cref="McpClient"/>/<c>McpServer</c> pair (same pattern as
/// <see cref="ToolArgumentCaseNormalizationTests"/>) so the rename is proven to reach the SDK's own
/// case-sensitive argument binder.
/// </para>
/// </summary>
[TestFixture]
public class ToolParameterAliasTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;
    private string _seededFilePath = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    private const string TargetMethodName = "Target";

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class ParameterAliasProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     $"    public void {TargetMethodName}() {{ }}" + Environment.NewLine +
                     $"    public void Caller() {{ {TargetMethodName}(); }}" + Environment.NewLine +
                     "}" + Environment.NewLine;

        var relativeFilePath = Path.Combine("ContosoOrders.Core", "ParameterAliasProbe.cs");
        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(seedWorkspaceManager, relativeFilePath, source, reloadSolution: false);
        }

        _seededFilePath = Path.Combine(_fixture.SolutionDirectory, relativeFilePath);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

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

        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise aliases without a loaded solution.");
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
    public async Task Alias_IsRenamed_ToolRunsOnTheRealValue_AndNoteNamesTheDeclaredParameter()
    {
        // "symbol" is not a FindReferences parameter; "symbolName" is. Without the alias this is the
        // unknown-parameter rejection the transcripts show.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbol"] = TargetMethodName,
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"The alias 'symbol' must be treated as 'symbolName'. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("Caller"),
            $"FindReferences must have run with the real symbolName value under the renamed key. Got: {text}");
        Assert.That(text, Does.Contain("'symbol' is not a parameter of FindReferences"),
            $"The response must carry a note about the alias. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'symbolName'"), $"The note must name the declared parameter. Got: {text}");
    }

    [Test]
    public async Task DeclaredParameterAlreadyPresent_AliasIsNotRenamed_AndIsRejectedAsUnknown()
    {
        // Renaming "symbol" onto the already-supplied "symbolName" would silently drop one of two
        // values the caller passed - the alias must be left for Validate to reject.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["symbol"] = "SomethingElse",
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An alias alongside its declared target must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("symbol"), $"The rejection must name the unrecognised alias. Got: {text}");
        Assert.That(text, Does.Not.Contain("treated as"), "No rename may have happened.");
    }

    [Test]
    public async Task UnlistedUnknownParameter_IsStillRejected()
    {
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["notAnAlias"] = "x",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(TextOf(result), Does.Contain("notAnAlias"));
    }

    [Test]
    [Category("ToolArgumentValidator")] // sentinel:auto-category
    public async Task EveryAliasEntry_ForAnActiveTool_PointsAtADeclaredParameter_AndIsNotItselfDeclared()
    {
        // Guards the table against drift: an alias that became a real parameter (declared meaning
        // wins, so the entry is dead) or whose target was renamed/removed (stale) fails here.
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var declaredByTool = tools.ToDictionary(
            t => t.Name,
            t => t.JsonSchema.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var checkedEntries = 0;
        foreach (var (toolName, aliases) in ToolArgumentValidator.ParameterAliases)
        {
            if (!declaredByTool.TryGetValue(toolName, out var declared))
                continue; // tool not registered under the modes this fixture activates

            foreach (var (alias, canonical) in aliases)
            {
                Assert.That(declared, Does.Contain(canonical),
                    $"{toolName}: alias '{alias}' targets '{canonical}', which is not a declared parameter.");
                Assert.That(declared, Does.Not.Contain(alias),
                    $"{toolName}: alias '{alias}' is itself a declared parameter, so the entry is dead or wrong.");
                checkedEntries++;
            }
        }

        Assert.That(checkedEntries, Is.GreaterThan(0), "No alias entry matched an active tool - the check was vacuous.");
    }
}
