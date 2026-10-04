// Coverage for proposal_reduce_tool_schema_token_cost.md Step 4b, slice 4b-1: the opt-in, exclusive
// "claude-lean" mode exposes exactly the proposal's 25 Core tools plus McpToolsetControl, slice 4b-2
// (a per-tool allow-list on top of the
// class registry), "claude" keeps its pre-existing 68-tool set, and "all" never expands to claude-lean.
// Same in-process MCP host shape as SchemaLeanProfileTests.cs; no solution load is needed. Nothing here
// mutates process-wide static state, so the fixture needs no [NonParallelizable].
using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ClaudeLeanModeTests
{
    /// <summary>The proposal's Core set plus McpToolsetControl (slice 4b-2), spelled out independently of ToolClassRegistry.ClaudeLeanToolNames.</summary>
    private static readonly string[] CoreTools =
    [
        "LoadSolution", "ReadFile", "GetFileOutline", "GetMethodSource", "GetLargeResult",
        "Search", "FindReferences", "InspectSymbol", "LocateSymbol", "GetDiagnostics",
        "Build", "RunTest", "Git", "ReplaceSnippet", "Member",
        "UsingDirective", "RenameSymbol", "WriteFile", "CreateFile", "DeleteFile",
        "UndoLastApply", "McpServerControl", "McpServerStatus", "AcknowledgeExternalFileChanges", "ListExternalDiskChanges",
        "McpToolsetControl",
    ];

    /// <summary>The Advanced server's tool set for --mode claude, captured before claude-lean existed.</summary>
    private static readonly string[] ClaudeAdvancedBaseline =
    [
        "AcknowledgeExternalFileChanges", "ApplyDiff", "ApplyUnifiedDiff", "Build", "ChangeAccessibility",
        "ChangeSignature", "ConstructorParameter", "ConvertAnonymousToNamed", "CreateFile", "CreateProject",
        "DeleteFile", "ExtractLocalVariable", "ExtractMembers", "ExtractMethodSafe", "Features",
        "FindReferences", "GetBestInsertionPoint", "GetDiagnostics", "GetFileOutline", "GetLargeResult",
        "GetMethodSource", "GetOperationDetail", "GetTypeInfo", "GetWorkspaceHealth", "Git",
        "Inline", "InlineClass", "InspectSymbol", "Introduce", "IntroduceParameterObject",
        "InvertAssignments", "IsSessionHalted", "ListAll", "ListExternalDiskChanges", "ListProjectFrameworkTargets",
        "ListSolutionItems", "ListWorkspaceSolutions", "LoadSolution", "LocateSymbol", "McpServerControl",
        "McpServerStatus", "Member", "MethodSignature", "ModifyAttribute", "ModifyBaseType",
        "ModifyEnum", "ModifyModifier", "MoveAllTypesToFiles", "MoveMember", "MoveType",
        "PreviewRenameImpact", "ProjectDoc", "QuerySymbolRelationships", "ReadFile", "RenameSymbol",
        "ReplaceSnippet", "RetryFailedChanges", "RunTest", "SafeDeleteUnusedSymbol", "Search",
        "SplitProjectByFolder", "SummaryComment", "SyncInterface", "SyncTypeAndFilename", "UndoLastApply",
        "UsingDirective", "WrapRange", "WriteFile",
    ];

    private static async Task<string[]> ListToolNamesAsync(bool advanced, string mode)
    {
        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mode };
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        if (advanced)
        {
            services.AddRoslynSentinelEnginesAdvanced();
        }
        else
        {
            services.AddRoslynSentinelEnginesBasic();
        }

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        if (advanced)
        {
            mcpBuilder.AddRoslynSentinelToolsAdvanced(services, activeModes);
        }
        else
        {
            mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes);
        }

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
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        await host.StopAsync();
        return names;
    }

    // ---- exposed tool set ----

    [TestCase(true)]
    [TestCase(false)]
    public async Task ClaudeLean_ExposesExactlyTheTwentySixCoreTools(bool advanced)
    {
        var names = await ListToolNamesAsync(advanced, "claude-lean");

        Assert.That(names, Has.Length.EqualTo(26));
        Assert.That(names, Is.EquivalentTo(CoreTools));
    }

    [Test]
    public async Task Claude_AdvancedServer_StillExposesItsPreExistingToolSet()
    {
        var names = await ListToolNamesAsync(advanced: true, "claude");

        Assert.That(names, Is.EquivalentTo(ClaudeAdvancedBaseline));
    }

    [Test]
    public async Task Claude_BasicServer_IsTheAdvancedBaselineMinusAdvancedOnlyClasses()
    {
        // AdvancedRefactoringTools is the only Claude class Basic does not register; its 13 tools drop out.
        var names = await ListToolNamesAsync(advanced: false, "claude");

        Assert.That(names, Has.Length.EqualTo(ClaudeAdvancedBaseline.Length - 13));
        Assert.That(names, Is.SubsetOf(ClaudeAdvancedBaseline));
        Assert.That(names, Is.SupersetOf(CoreTools.Where(t => t != "McpToolsetControl")));
        Assert.That(names, Does.Not.Contain("McpToolsetControl"));
    }

    // ---- registry and startup parsing ----

    [Test]
    public void Registry_ListsClaudeLean_InBothServersMaps_AndCoreSetMatches()
    {
        Assert.That(ToolClassRegistry.BasicModeToToolClasses.ContainsKey("claude-lean"), Is.True);
        Assert.That(ToolClassRegistry.AdvancedModeToToolClasses.ContainsKey("claude-lean"), Is.True);
        Assert.That(ToolClassRegistry.ClaudeLeanToolNames, Is.EquivalentTo(CoreTools));
    }

    [Test]
    public void Registry_EveryCoreToolName_IsDeclaredByAToolMethod()
    {
        // Guards against a typo in the allow-list silently dropping a tool from the surface.
        var declared = McpToolSchemaPatcher
            .DiscoverAllDeclaredTools(typeof(ServerStatusTools).Assembly)
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(declared, Is.SupersetOf(CoreTools));
    }

    [TestCase("all")]
    [TestCase("ALL")]
    public void ParseArgs_All_DoesNotExpandToClaudeLean(string all)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={all}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Not.Contain("claude-lean"));
        Assert.That(activeModes, Does.Contain("Claude"));
    }

    [TestCase("claude-lean")]
    [TestCase("all,claude-lean")]
    public void ParseArgs_NamedExplicitly_ActivatesClaudeLean(string modeArg)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={modeArg}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Contain("claude-lean"));
    }

    [Test]
    public void ResolveActiveToolClasses_ClaudeLeanAlone_YieldsElevenToolClasses()
    {
        var classes = ServerStartupHelpers.ResolveActiveToolClasses(
            new HashSet<string>(["claude-lean"], StringComparer.OrdinalIgnoreCase),
            ToolClassRegistry.BasicModeToToolClasses,
            new HashSet<string>(),
            new HashSet<string>());

        Assert.That(classes, Has.Count.EqualTo(11));
        Assert.That(classes, Does.Contain("ToolsetControlTools"));
        Assert.That(classes, Does.Contain("DeclarationTools"));
        Assert.That(classes, Does.Not.Contain("AdvancedRefactoringTools"));
        Assert.That(classes, Does.Not.Contain("DocumentationTools"));
    }

    // ---- escape-hatch advice must not name tools the allow-list removed ----

    [Test]
    public void WriteToolAdvice_UnderAllowList_NamesOnlyAllowedTools()
    {
        var classes = ToolClassRegistry.BasicModeToToolClasses["claude-lean"];
        var helper = new WriteToolAdviceHelper(classes, ToolClassRegistry.ClaudeLeanToolNames);

        Assert.That(helper.IsExposed("WriteFile"), Is.True);
        Assert.That(helper.IsExposed("ApplyDiff"), Is.False);
        Assert.That(helper.IsExposed("ApplyUnifiedDiff"), Is.False);
        Assert.That(helper.AdviseForOversizedEdit("ReplaceSnippet").ToolNames, Does.Not.Contain("ApplyDiff"));
    }

    [Test]
    public void WriteToolAdvice_WithoutAllowList_StillNamesWholeClass()
    {
        var helper = new WriteToolAdviceHelper(ToolClassRegistry.BasicModeToToolClasses["Claude"]);

        Assert.That(helper.IsExposed("ApplyDiff"), Is.True);
    }
}
