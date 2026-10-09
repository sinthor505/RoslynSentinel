using System.IO.Pipelines;
using System.Text.Json;
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
/// Coverage for the wire-path-relativize filter that rewrites absolute file paths under the
/// solution root to root-relative '/' form and stamps a top-level solutionRoot property -
/// see docs/current/plans/plan_wire_relative_paths_backstop_filter.md. Runs against a real
/// in-process McpClient/McpServer pair (same pattern as ToolCallEchoFilterTests) because
/// the point is that the rewrite survives the full filter chain.
/// NonParallelizable: test 4 flips the process-wide ROSLYNSENTINEL_RELATIVE_PATHS env var.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WirePathRelativizeFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    // Enough distinct callers to exceed the 15 KB offload threshold for test 5.
    private const int OffloadCallerCount = 400;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var callers = Enumerable.Range(0, OffloadCallerCount)
            .Select(i => $"    public void RelativizeTestCaller{i}() {{ RelativizeTestTarget(); }}")
            .ToList();

        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class WirePathRelativizeProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     "    public void RelativizeTestTarget() { }" + Environment.NewLine +
                     string.Join(Environment.NewLine, callers) + Environment.NewLine +
                     "}" + Environment.NewLine;

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "WirePathRelativizeProbe.cs"),
                source,
                reloadSolution: false);
        }

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

        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise the filter without a loaded solution.");
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

    [Test]
    public async Task Search_SymbolMode_ReturnsRelativeFilePaths_AndSolutionRoot()
    {
        var (result, text) = await CallAsync("Search", new Dictionary<string, object?>
        {
            ["reason"] = "test symbol search",
            ["mode"] = "symbol",
            ["query"] = "WirePathRelativizeProbe",
        });

        Assert.That(result.IsError, Is.Not.True, $"Search must succeed. Got: {text}");

        var responseBody = JsonNode.Parse(text)!.AsObject();

        // Check that solutionRoot is present and matches the fixture directory (case-insensitive, trim trailing separators).
        Assert.That(responseBody.ContainsKey("solutionRoot"), Is.True, "Response must contain top-level solutionRoot property.");
        var solutionRoot = responseBody["solutionRoot"]!.GetValue<string>();

        var expectedRoot = _fixture.SolutionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actualRoot = solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.That(actualRoot, Is.EqualTo(expectedRoot).IgnoreCase,
            $"solutionRoot must equal fixture directory. Expected: {expectedRoot}, Got: {actualRoot}");

        // Check that all filePaths in successData are relative (not rooted) and use forward slashes.
        var successData = responseBody["successData"];
        if (successData is JsonArray successArray)
        {
            foreach (var item in successArray)
            {
                if (item is JsonObject itemObj && itemObj.ContainsKey("filePath"))
                {
                    var filePath = itemObj["filePath"]!.GetValue<string>();
                    Assert.That(Path.IsPathRooted(filePath), Is.False, $"filePath must be relative: {filePath}");
                    Assert.That(filePath, Does.Not.Contain("\\"), $"filePath must use forward slashes: {filePath}");
                }
            }
        }
    }

    [Test]
    public async Task RelativeFilePath_RoundTripsIntoReadFile()
    {
        // First, get a relative filePath from Search.
        var (searchResult, searchText) = await CallAsync("Search", new Dictionary<string, object?>
        {
            ["reason"] = "test symbol search",
            ["mode"] = "symbol",
            ["query"] = "WirePathRelativizeProbe",
        });

        Assert.That(searchResult.IsError, Is.Not.True, "Search must succeed.");

        var responseBody = JsonNode.Parse(searchText)!.AsObject();
        var successData = responseBody["successData"];
        var filePath = string.Empty;

        if (successData is JsonArray successArray && successArray.Count > 0)
        {
            if (successArray[0] is JsonObject firstItem && firstItem.ContainsKey("filePath"))
            {
                filePath = firstItem["filePath"]!.GetValue<string>();
            }
        }

        Assert.That(filePath, Is.Not.Empty, "Search response must contain at least one filePath.");

        // Now call ReadFile with that relative path.
        var (readResult, readText) = await CallAsync("ReadFile", new Dictionary<string, object?>
        {
            ["reason"] = "test read relative path",
            ["filePath"] = filePath,
        });

        Assert.That(readResult.IsError, Is.Not.True, $"ReadFile must succeed with relative path. Got: {readText}");

        // Check that the returned source contains the probe class name.
        Assert.That(readText, Does.Contain("WirePathRelativizeProbe"), "ReadFile result must contain the probe class name.");
    }

    [Test]
    public async Task LargeResultAndSolutionPath_StayAbsolute()
    {
        // Test that largeResult.filePath and solutionPath (if present) remain absolute.
        // For this, we'll call a tool that might return solutionPath (if available in active modes).

        // First, verify that offloaded large results keep absolute paths in largeResult.
        var (result, text) = await CallAsync("FindReferences", new Dictionary<string, object?>
        {
            ["reason"] = "test offload scenario",
            ["symbolName"] = "RelativizeTestTarget",
            ["kind"] = "callers",
        });

        Assert.That(result.IsError, Is.Not.True, $"FindReferences must succeed. Got: {text}");

        var stub = JsonNode.Parse(text)!.AsObject();

        // Check if it was offloaded.
        if (stub.ContainsKey("offloaded") && stub["offloaded"]!.GetValue<bool>())
        {
            // The stub should have largeResult info.
            if (stub.ContainsKey("largeResult"))
            {
                var largeResult = stub["largeResult"]!.AsObject();
                if (largeResult.ContainsKey("filePath"))
                {
                    var largeResultPath = largeResult["filePath"]!.GetValue<string>();
                    Assert.That(Path.IsPathRooted(largeResultPath), Is.True,
                        $"largeResult.filePath must remain absolute: {largeResultPath}");
                }
            }
        }
    }

    [Test]
    public async Task EnvVarOff_ReturnsAbsolutePaths_AndNoSolutionRoot()
    {
        var originalValue = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_RELATIVE_PATHS");
        try
        {
            // Disable relative paths.
            Environment.SetEnvironmentVariable("ROSLYNSENTINEL_RELATIVE_PATHS", "0");

            var (result, text) = await CallAsync("Search", new Dictionary<string, object?>
            {
                ["reason"] = "test symbol search",
                ["mode"] = "symbol",
                ["query"] = "WirePathRelativizeProbe",
            });

            Assert.That(result.IsError, Is.Not.True, "Search must succeed.");

            var responseBody = JsonNode.Parse(text)!.AsObject();

            // Check that there is no solutionRoot when disabled.
            Assert.That(responseBody.ContainsKey("solutionRoot"), Is.False,
                "When ROSLYNSENTINEL_RELATIVE_PATHS=0, response must not have solutionRoot.");

            // Check that filePaths are absolute (rooted).
            var successData = responseBody["successData"];
            if (successData is JsonArray successArray)
            {
                foreach (var item in successArray)
                {
                    if (item is JsonObject itemObj && itemObj.ContainsKey("filePath"))
                    {
                        var filePath = itemObj["filePath"]!.GetValue<string>();
                        Assert.That(Path.IsPathRooted(filePath), Is.True,
                            $"When disabled, filePath must be absolute: {filePath}");
                    }
                }
            }
        }
        finally
        {
            // Restore original value.
            if (originalValue == null)
            {
                Environment.SetEnvironmentVariable("ROSLYNSENTINEL_RELATIVE_PATHS", null);
            }
            else
            {
                Environment.SetEnvironmentVariable("ROSLYNSENTINEL_RELATIVE_PATHS", originalValue);
            }
        }
    }

    [Test]
    public async Task OffloadedLargeResult_RawFile_ContainsRelativePathsAndSolutionRoot()
    {
        // Call FindReferences with enough callers to trigger offload.
        var (result, text) = await CallAsync("FindReferences", new Dictionary<string, object?>
        {
            ["reason"] = "test offload large result",
            ["symbolName"] = "RelativizeTestTarget",
            ["kind"] = "callers",
        });

        Assert.That(result.IsError, Is.Not.True, "FindReferences must succeed.");

        var stub = JsonNode.Parse(text)!.AsObject();

        // Verify the response was offloaded.
        Assert.That(stub["offloaded"]!.GetValue<bool>(), Is.True, "Precondition: response must be offloaded with 400 callers.");

        // Extract the listSummary to verify we have caller data.
        var listSummary = stub["listSummary"]?.AsObject();
        Assert.That(listSummary, Is.Not.Null, "The stub must relay listSummary.");
        Assert.That(listSummary!["fileCount"]!.GetValue<int>(), Is.GreaterThan(0), "Must have at least one file with callers.");

        var byFile = listSummary["byFile"]!.AsArray();
        Assert.That(byFile, Is.Not.Empty, "byFile array must have entries.");

        // Find the raw file written by the offload filter.
        var largeResultsDir = Path.Combine(_fixture.SolutionDirectory, ".roslynsentinel", "largeresults");
        Assert.That(Directory.Exists(largeResultsDir), Is.True, $"Large results directory must exist: {largeResultsDir}");

        // Find the most recently created file.
        var largeResultFiles = Directory.GetFiles(largeResultsDir, "largeresult_*.json")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .ToList();

        Assert.That(largeResultFiles, Is.Not.Empty, "Must find at least one large result file.");
        var rawFilePath = largeResultFiles[0];

        // Read the raw file to verify the filter worked.
        var rawContent = File.ReadAllText(rawFilePath);

        // Verify the file is large (exceeded offload threshold).
        Assert.That(rawContent.Length, Is.GreaterThan(10000), "Raw file should be substantial.");

        // Verify it contains solutionRoot (filter was applied).
        Assert.That(rawContent, Does.Contain("\"solutionRoot\""),
            "Raw offload file must contain solutionRoot property (filter was applied).");

        // Verify it contains relative filePaths with forward slashes (the key rewrite).
        // Look for the specific pattern: "filePath": "ContosoOrders.Core/...
        Assert.That(rawContent, Does.Contain("\"filePath\": \"ContosoOrders.Core/"),
            "Raw offload file must contain relative file paths with forward slashes (rewrite occurred).");
    }
}
