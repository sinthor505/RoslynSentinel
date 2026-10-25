using System.IO.Pipelines;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Staleness guard for the call-shape blocks in the subagent definitions (.claude/agents/*.md, between the
/// <c>call-shapes:start</c> and <c>call-shapes:end</c> markers): every <c>Tool(param, param: value)</c> shape documented there
/// must name only parameters that the tool's emitted input schema declares. A parameter rename in a tool then fails
/// this test, naming the agent file, the tool and the stale parameter, instead of the block drifting silently.
/// <para>
/// Reads the schemas through an in-process <see cref="McpClient"/>/<c>McpServer</c> pair (same harness pattern as
/// <see cref="ToolParameterAliasTests"/>), so they are the schemas a real client sees.
/// </para>
/// </summary>
[TestFixture]
public class AgentCallShapeBlocksTests
{
    // Workspace declares Search, ReadFile, GetMethodSource, GetFileOutline, ReplaceSnippet, CreateFile, Build and RunTest;
    // Refactor declares Member. A tool named in a block but missing from the harness fails with the mode to add (see ToolModes).
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor" };

    // Mode that enables each tool the blocks may document; used only to name the mode to add when a tool is missing from the harness.
    private static readonly Dictionary<string, string> ToolModes = new(StringComparer.Ordinal)
    {
        ["Search"] = "Workspace",
        ["ReadFile"] = "Workspace",
        ["GetMethodSource"] = "Workspace",
        ["GetFileOutline"] = "Workspace",
        ["ReplaceSnippet"] = "Workspace",
        ["CreateFile"] = "Workspace",
        ["Build"] = "Workspace",
        ["RunTest"] = "Workspace",
        ["Member"] = "Refactor",
    };

    private const string StartMarker = "<!-- call-shapes:start -->";
    private const string EndMarker = "<!-- call-shapes:end -->";

    private static readonly string[] AgentFiles =
    [
        "implementer.md",
        "implementer-senior.md",
        "implementer-sweep.md",
        "test-writer.md",
    ];

    // Tool(arg, arg: value, ...) shapes; group 1 is the tool name, group 2 the argument text.
    private static readonly Regex CallShape = new(@"\b([A-Z][A-Za-z]+)\(([^)]*)\)", RegexOptions.Compiled);

    // Tool name -> parameter names declared by its emitted input schema.
    private Dictionary<string, HashSet<string>> _schemaParameters = null!;

    [OneTimeSetUp]
    public async Task LoadEmittedSchemas()
    {
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

        using var host = hostBuilder.Build();
        _ = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

        _schemaParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var schema = tool.ProtocolTool.InputSchema;
            if (schema.ValueKind == System.Text.Json.JsonValueKind.Object &&
                schema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    names.Add(property.Name);
                }
            }

            _schemaParameters[tool.Name] = names;
        }

        await host.StopAsync();
    }

    private static string ReadAgentFile(string agentFile)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoslynSentinel.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "Could not find the repo root (the directory holding RoslynSentinel.slnx) above the test directory.");
        return File.ReadAllText(Path.Combine(directory!.FullName, ".claude", "agents", agentFile));
    }

    private static int CountOccurrences(string text, string marker) =>
        Regex.Matches(text, Regex.Escape(marker)).Count;

    [TestCaseSource(nameof(AgentFiles))]
    public void AgentFile_HasExactlyOneCallShapeMarkerPair(string agentFile)
    {
        var text = ReadAgentFile(agentFile);

        Assert.That(CountOccurrences(text, StartMarker), Is.EqualTo(1), $"{agentFile} must contain exactly one '{StartMarker}'.");
        Assert.That(CountOccurrences(text, EndMarker), Is.EqualTo(1), $"{agentFile} must contain exactly one '{EndMarker}'.");
        Assert.That(text.IndexOf(StartMarker, StringComparison.Ordinal), Is.LessThan(text.IndexOf(EndMarker, StringComparison.Ordinal)),
            $"In {agentFile} the start marker must come before the end marker.");
    }

    [TestCaseSource(nameof(AgentFiles))]
    public void AgentFile_EveryDocumentedParameterIsDeclaredByTheToolSchema(string agentFile)
    {
        var text = ReadAgentFile(agentFile);
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0).And.LessThan(end), $"{agentFile} needs its call-shapes block (see AgentFile_HasExactlyOneCallShapeMarkerPair).");
        var block = text.Substring(start + StartMarker.Length, end - start - StartMarker.Length);

        var failures = new List<string>();
        var shapeCount = 0;
        foreach (Match match in CallShape.Matches(block))
        {
            shapeCount++;
            var tool = match.Groups[1].Value;
            var argumentText = match.Groups[2].Value;

            if (!_schemaParameters.TryGetValue(tool, out var declared))
            {
                var mode = ToolModes.TryGetValue(tool, out var knownMode) ? knownMode : "<the mode that declares it, see ToolClassRegistry.cs>";
                failures.Add($"{agentFile}: tool '{tool}' is not exposed by this test's server; add mode '{mode}' to ActiveModes.");
                continue;
            }

            foreach (var rawToken in argumentText.Split(','))
            {
                var token = rawToken.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                var colon = token.IndexOf(':');
                var name = (colon >= 0 ? token[..colon] : token).Trim().TrimEnd('?');
                if (name.Length == 0 || declared.Contains(name))
                {
                    continue;
                }

                failures.Add($"{agentFile}: {tool}({argumentText.Trim()}) names unknown parameter '{name}'; {tool} declares: " +
                             string.Join(", ", declared.OrderBy(n => n, StringComparer.Ordinal)) + ".");
            }
        }

        Assert.That(shapeCount, Is.GreaterThan(0), $"{agentFile}: no Tool(...) call shape was found between the markers; the extraction regex or the block is broken.");
        Assert.That(failures, Is.Empty, "Stale call shapes (fix the block in the named agent file):" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
