using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Audit test: verifies that each path-taking tool accepts a solution-root-relative path with '/'
/// separators as its filePath input, as per plan_wire_relative_paths_backstop_filter.md step 5.
/// The fixture's probe file is passed as a root-relative '/' path computed from
/// _fixture.SolutionDirectory, and each row asserts the call is not a NotFound/InvalidArgument
/// error - other errors (e.g. symbol not found, nothing to do in dry run) are valid and do not
/// fail the audit. Rows are listed in the report; failed rows are findings, not bugs to fix in
/// this step (out of scope).
/// </summary>
[TestFixture]
[NonParallelizable]
public class RelativePathInputAuditTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;
    private string _relativeProbeFilePath = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var source = """
            namespace ContosoOrders.Core;
            public class RelativePathAuditProbe
            {
                public void AuditTarget() { }
                public void AuditCaller() { AuditTarget(); }
            }
            """;

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "RelativePathAuditProbe.cs"),
                source,
                reloadSolution: false);
        }

        // Compute root-relative path with '/' separators.
        var absolutePath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "RelativePathAuditProbe.cs");
        var relative = Path.GetRelativePath(_fixture.SolutionDirectory, absolutePath);
        _relativeProbeFilePath = relative.Replace(Path.DirectorySeparatorChar, '/');

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
            new Dictionary<string, object?> { ["reason"] = "audit test", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    private async Task<(CallToolResult Result, string Text)> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(
            tool,
            args!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (result, text);
    }

    private bool IsPathResolutionError(string errorMsg)
    {
        return errorMsg.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
            || errorMsg.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase)
            || errorMsg.Contains("InvalidArgument", StringComparison.OrdinalIgnoreCase)
            || errorMsg.Contains("InvalidPath", StringComparison.OrdinalIgnoreCase)
            || errorMsg.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    // Row 1: GetFileOutline with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task GetFileOutline_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("GetFileOutline", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["filePath"] = _relativeProbeFilePath,
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"GetFileOutline failed with a path resolution error: {text}");
    }

    // Row 2: GetMethodSource with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task GetMethodSource_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("GetMethodSource", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["filePath"] = _relativeProbeFilePath,
            ["methodName"] = "AuditTarget",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"GetMethodSource failed with a path resolution error: {text}");
    }

    // Row 3: ReadFile with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task ReadFile_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("ReadFile", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["filePath"] = _relativeProbeFilePath,
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"ReadFile failed with a path resolution error: {text}");
    }

    // Row 4: Declaration (read-only) with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task Declaration_View_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("Declaration", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["operation"] = "view",
            ["filePath"] = _relativeProbeFilePath,
            ["symbolName"] = "RelativePathAuditProbe",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"Declaration (view) failed with a path resolution error: {text}");
    }

    // Row 5: ParameterEdit (view action) with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task ParameterEdit_View_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("ParameterEdit", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["operation"] = "method",
            ["filePath"] = _relativeProbeFilePath,
            ["action"] = "view",
            ["methodName"] = "AuditTarget",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"ParameterEdit (view) failed with a path resolution error: {text}");
    }

    // Row 6: SafeDeleteUnusedSymbol with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task SafeDeleteUnusedSymbol_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("SafeDeleteUnusedSymbol", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["filePath"] = _relativeProbeFilePath,
            ["symbolName"] = "AuditTarget",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"SafeDeleteUnusedSymbol failed with a path resolution error: {text}");
    }

    // Row 7: GetDiagnostics with relative file path (scope=file)
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task GetDiagnostics_FileScope_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("GetDiagnostics", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["scope"] = "file",
            ["scopeName"] = _relativeProbeFilePath,
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"GetDiagnostics (file scope) failed with a path resolution error: {text}");
    }

    // Row 8: Member (operation view) with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task Member_View_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("Member", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["filePath"] = _relativeProbeFilePath,
            ["operation"] = "view",
            ["containerName"] = "RelativePathAuditProbe",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"Member (view) failed with a path resolution error: {text}");
    }

    // Row 9: ReplaceSnippet (validate action, single edit) with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task ReplaceSnippet_Validate_SingleEdit_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("ReplaceSnippet", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["action"] = "validate",
            ["filePath"] = _relativeProbeFilePath,
            ["oldContent"] = "public void AuditTarget() { }",
            ["newContent"] = "public void AuditTargetRenamed() { }",
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"ReplaceSnippet (validate, single) failed with a path resolution error: {text}");
    }

    // Row 10: ReplaceSnippet (validate action, batchEdits) with relative path in batchEdits[].filePath
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task ReplaceSnippet_Validate_BatchEdits_AcceptsRelativePath()
    {
        var batchEdit = new Dictionary<string, object?>
        {
            ["filePath"] = _relativeProbeFilePath,
            ["oldContent"] = "public void AuditCaller() { AuditTarget(); }",
            ["newContent"] = "public void AuditCallerRenamed() { AuditTarget(); }",
        };

        var (result, text) = await CallAsync("ReplaceSnippet", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["action"] = "validate",
            ["batchEdits"] = new[] { batchEdit },
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"ReplaceSnippet (validate, batch) failed with a path resolution error: {text}");
    }

    // Row 11: Build (file scope) with relative path
    [Test]
    [Category("RelativePathInputAudit")]
    public async Task Build_FileScope_AcceptsRelativePath()
    {
        var (result, text) = await CallAsync("Build", new Dictionary<string, object?>
        {
            ["reason"] = "audit test",
            ["level"] = "quickBuild",
            ["scope"] = "file",
            ["scopeName"] = _relativeProbeFilePath,
        });

        Assert.That(!IsPathResolutionError(text), Is.True,
            $"Build (file scope, quickBuild) failed with a path resolution error: {text}");
    }
}
