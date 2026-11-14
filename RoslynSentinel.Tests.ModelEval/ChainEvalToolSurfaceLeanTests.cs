using System.IO.Pipelines;
using System.Text.Json;

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
/// Covers the lean arm of <see cref="ChainEvalToolSurface"/> (ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean).
/// Uses the string overload of NewHiddenTools so no environment variable is mutated.
/// </summary>
[TestFixture]
public class ChainEvalToolSurfaceLeanTests
{
    private static readonly string[] LegacyHiddenNames =
    [
        "Features", "McpServerStatus", "WriteFile", "ApplyDiff", "ApplyUnifiedDiff", "Git", "GitRemote",
        "ListWorkspaceSolutions", "LoadSolution", "CreateProject", "SplitProjectByFolder",
    ];

    private static System.Reflection.Assembly[] ToolAssemblies() =>
    [
        typeof(RoslynSentinel.Tools.Basic.ServerStatusTools).Assembly,
        typeof(RoslynSentinel.Tools.Advanced.SubAgentTools).Assembly,
        typeof(RoslynSentinel.Tools.Experimental.CodeTransformTools).Assembly,
    ];

    private static HashSet<string> DiscoveredToolNames() =>
        McpToolSchemaPatcher.DiscoverToolMethods(ToolAssemblies())
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Test]
    public void Default_UnsetEmptyFullAndUnknownValues_ReturnTheFullSurfaceList()
    {
        foreach (var value in new string?[] { null, "", "full", "bogus" })
        {
            var hidden = ChainEvalToolSurface.NewHiddenTools(value, null);

            Assert.That(hidden, Is.EquivalentTo(ChainEvalToolSurface.NewFullSurfaceHiddenTools()), $"value '{value}'");
            Assert.That(hidden, Is.EquivalentTo(LegacyHiddenNames), $"value '{value}'");
        }
    }

    [Test]
    public void LeanAllowList_HasExactly18DistinctNames_AllDiscovered()
    {
        var allow = ChainEvalToolSurface.LeanAllowList;
        var discovered = DiscoveredToolNames();

        Assert.That(allow, Has.Count.EqualTo(18));
        Assert.That(allow.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(18));
        Assert.That(allow.Where(n => !discovered.Contains(n)), Is.Empty, "allow-list names not discovered");
    }

    [Test]
    public void Lean_HidesEveryDiscoveredToolOutsideTheAllowList()
    {
        var allow = new HashSet<string>(ChainEvalToolSurface.LeanAllowList, StringComparer.OrdinalIgnoreCase);
        var hidden = ChainEvalToolSurface.NewHiddenTools("lean", null);

        var notHidden = DiscoveredToolNames().Where(n => !allow.Contains(n) && !hidden.Contains(n));
        var wronglyHidden = allow.Where(n => hidden.Contains(n));

        Assert.That(notHidden, Is.Empty, "discovered tools outside the allow-list that are not hidden");
        Assert.That(wronglyHidden, Is.Empty, "allow-listed tools that are hidden");
    }

    [Test]
    public void Lean_ExtraAllowedIsNotHidden()
    {
        var withExtra = ChainEvalToolSurface.NewHiddenTools("lean", new[] { "EditSnippets" });
        var withoutExtra = ChainEvalToolSurface.NewHiddenTools("lean", null);

        Assert.Multiple(() =>
        {
            Assert.That(withExtra, Does.Not.Contain("EditSnippets"));
            Assert.That(withExtra, Does.Contain("WriteFile"));
            Assert.That(withExtra, Does.Contain("Git"));
            Assert.That(withoutExtra, Does.Contain("EditSnippets"));
        });
    }

    [Test]
    public async Task Lean_InProcessServerOffersExactlyTheAllowList()
    {
        var tools = await ListOfferedToolsAsync(ChainEvalToolSurface.NewHiddenTools("lean", null));
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allow = ChainEvalToolSurface.LeanAllowList;

        TestContext.Out.WriteLine($"Allow-listed but not offered: {string.Join(", ", allow.Where(n => !names.Contains(n)))}");
        TestContext.Out.WriteLine($"Offered but not allow-listed: {string.Join(", ", names.Where(n => !allow.Contains(n, StringComparer.OrdinalIgnoreCase)))}");

        Assert.That(names, Is.EquivalentTo(allow));
    }

    [Test]
    public async Task EditSnippetsArm_UnderLean_OffersEditSnippetsInsteadOfReplaceSnippet()
    {
        // Same shape as the EditSnippets arm in OrderPricingRefactorChainAgentTests.SetUp.
        var hidden = new HashSet<string>(ChainEvalToolSurface.NewHiddenTools("lean", new[] { "EditSnippets" }), StringComparer.OrdinalIgnoreCase)
        {
            "ReplaceSnippet",
        };

        var tools = await ListOfferedToolsAsync(hidden, "EditSnippetsExperiment");
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = ChainEvalToolSurface.LeanAllowList
            .Where(n => !string.Equals(n, "ReplaceSnippet", StringComparison.OrdinalIgnoreCase))
            .Append("EditSnippets")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.That(names, Is.EquivalentTo(expected));
    }

    [Test]
    public async Task EditSnippetsArm_OnFullSurface_StillOffersEditSnippets()
    {
        var hidden = new HashSet<string>(ChainEvalToolSurface.NewHiddenTools(null, new[] { "EditSnippets" }), StringComparer.OrdinalIgnoreCase)
        {
            "ReplaceSnippet",
        };

        var tools = await ListOfferedToolsAsync(hidden, "EditSnippetsExperiment");
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("EditSnippets"));
            Assert.That(names, Does.Not.Contain("ReplaceSnippet"));
        });
    }

    [Test]
    [NonParallelizable]
    public void LegacyFullSurfaceList_IgnoresTheToolsetVariable()
    {
        // Legacy whole-file fixtures never call NewHiddenTools, so they stay on the full surface
        // regardless of this variable; this test pins the list NewFullSurfaceHiddenTools returns.
        var saved = Environment.GetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar, "lean");

            var fullSurface = ChainEvalToolSurface.NewFullSurfaceHiddenTools();
            var envHidden = ChainEvalToolSurface.NewHiddenTools();

            Assert.Multiple(() =>
            {
                Assert.That(fullSurface, Is.EquivalentTo(LegacyHiddenNames));
                Assert.That(fullSurface, Has.Count.EqualTo(11));
                Assert.That(fullSurface, Does.Contain("WriteFile"));
                Assert.That(envHidden, Does.Contain("WriteFile"));
                Assert.That(envHidden, Does.Not.Contain("Search"));
                Assert.That(envHidden, Has.Count.GreaterThan(11));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChainEvalToolSurface.ToolsetEnvVar, saved);
        }
    }

    [Test]
    public async Task SchemaSize_LeanIsSmallerThanFull_AndIsLogged()
    {
        var full = await ListOfferedToolsAsync(ChainEvalToolSurface.NewFullSurfaceHiddenTools());
        var lean = await ListOfferedToolsAsync(ChainEvalToolSurface.NewHiddenTools("lean", null));
        var fullChars = SchemaChars(full);
        var leanChars = SchemaChars(lean);

        TestContext.Out.WriteLine(
            $"schema chars full={fullChars} tools={full.Count}; lean={leanChars} tools={lean.Count}; approx tokens = chars/4");

        Assert.That(leanChars, Is.LessThan(fullChars));
    }

    private static int SchemaChars(IEnumerable<McpClientTool> tools) =>
        tools.Sum(t => JsonSerializer.Serialize(t.ProtocolTool).Length);

    private static HashSet<string> ModesWith(string? extraMode)
    {
        var modes = new HashSet<string>(ChainEvalToolSurface.NewModes(), StringComparer.OrdinalIgnoreCase);
        if (extraMode is not null)
        {
            modes.Add(extraMode);
        }

        return modes;
    }

    // Same in-process server wiring as ChainEvalToolSurfaceTests.SetUp, with the hide set supplied by the caller.
    private static async Task<IList<McpClientTool>> ListOfferedToolsAsync(HashSet<string> hideTools, string? extraMode = null)
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
            ModesWith(extraMode),
            hideTools: hideTools);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        var host = hostBuilder.Build();
        _ = host.RunAsync();

        McpClient? client = null;
        try
        {
            var clientTransport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);

            client = await McpClient.CreateAsync(
                clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
            return await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await host.StopAsync();
            host.Dispose();
        }
    }
}
