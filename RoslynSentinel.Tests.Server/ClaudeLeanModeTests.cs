// Coverage for proposal_reduce_tool_schema_token_cost.md Step 4b, slice 4b-1: the opt-in, exclusive
// "claude-lean" mode exposes exactly the proposal's Core tools plus McpToolsetControlTool, slice 4b-2
// (a per-tool allow-list on top of the
// class registry), "claude" keeps its pre-existing tool set, and "all" never expands to claude-lean.
// Counts are never hand-copied: CoreTools below is the one explicit oracle for the claude-lean surface and every
// expected count is derived from it or from the registry. Adding a core tool = edit ToolClassRegistry.ClaudeLeanToolNames
// and CoreTools (McpToolsetControlTests reads CoreTools too).
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
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ToolClassRegistry")] // sentinel:auto-category
public class ClaudeLeanModeTests
{
    /// <summary>
    /// The Advanced claude-lean core surface, spelled out independently of ToolClassRegistry.ClaudeLeanToolNames so the
    /// tests catch accidental registry changes. The single oracle: every claude-lean count in this file and in
    /// McpToolsetControlTests is derived from it. Edit it together with ToolClassRegistry.ClaudeLeanToolNames.
    /// </summary>
    internal static readonly string[] CoreTools =
    [
        "LoadSolution", "ReadFile", "GetFileOutline", "GetMethodSource", "GetLargeResult",
        "Search", "InspectSymbol", "GetDiagnostics",
        "Build", "RunTest", "Git", "ReplaceSnippet", "Member",
        "UsingDirective", "RenameSymbol", "WriteFile", "CreateFile", "DeleteFile",
        "UndoLastApply", "McpServerControl", "McpServerStatus", "ExternalFileDrift",
        "McpToolsetControl", "SubAgent",
    ];

    /// <summary>CoreTools minus SubAgentTool, which is declared in Tools.Advanced and never registered by the Basic server.</summary>
    private static readonly string[] BasicCoreTools = CoreTools.Where(t => t != "SubAgent").ToArray();

    /// <summary>The Advanced server's tool set for --mode claude, captured before claude-lean existed.</summary>
    private static readonly string[] ClaudeAdvancedBaseline =
    [
        "ApplyDiff", "ApplyUnifiedDiff", "Build", "ChangeAccessibility",
        "ChangeSignature", "ConstructorParameter", "ConvertAnonymousToNamed", "CreateFile", "CreateProject",
        "DeleteFile", "ExtractLocalVariable", "ExtractMembers", "ExtractMethod", "ExternalFileDrift", "Features",
        "FindReferences", "GetBestInsertionPoint", "GetDiagnostics", "GetFileOutline", "GetLargeResult",
        "GetMethodSource", "GetOperationDetail", "GetTypeInfo", "GetWorkspaceHealth", "Git",
        "Inline", "InlineClass", "InspectSymbol", "Introduce", "IntroduceParameterObject",
        "InvertAssignments", "ListAll", "ListProjectFrameworkTargets",
        "ListSolutionItems", "ListWorkspaceSolutions", "LoadSolution", "LocateSymbol", "McpServerControl",
        "McpServerStatus", "Member", "MethodSignature", "ModifyAttribute", "ModifyBaseType",
        "ModifyEnum", "ModifyModifier", "MoveAllTypesToFiles", "MoveMember", "MoveType",
        "PreviewRenameImpact", "ProjectDoc", "QuerySymbolRelationships", "ReadFile", "RenameSymbol",
        "ReplaceSnippet", "RetryFailedChanges", "RunTest", "DeleteUnusedSymbol", "Search",
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
        var hostRun = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        await host.StopAsync();
        await hostRun;
        return names;
    }

    // ---- exposed tool set ----

    [TestCase(true)]
    [TestCase(false)]
    public async Task ClaudeLean_ExposesExactlyTheCoreToolsForItsFlavor(bool advanced)
    {
        // SubAgentTool is Advanced-only, so the Basic claude-lean surface is one tool smaller (see BasicCoreTools).
        var expected = advanced ? CoreTools : BasicCoreTools;
        var names = await ListToolNamesAsync(advanced, "claude-lean");

        Assert.That(names, Has.Length.EqualTo(expected.Length), "ToolClassRegistry.ClaudeLeanToolNames and ClaudeLeanModeTests.CoreTools must agree; edit both");
        Assert.That(names, Is.EquivalentTo(expected), "ToolClassRegistry.ClaudeLeanToolNames and ClaudeLeanModeTests.CoreTools must agree; edit both");
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
        // AdvancedRefactoringTools is the only Claude class Basic does not register; its tools drop out.
        var advancedOnlyTools = McpToolSchemaPatcher
            .DiscoverAllDeclaredTools(typeof(AdvancedRefactoringTools).Assembly)
            .Where(t => t.ClassName == nameof(AdvancedRefactoringTools))
            .Select(t => t.ToolName)
            .Distinct()
            .ToArray();
        var names = await ListToolNamesAsync(advanced: false, "claude");

        Assert.That(advancedOnlyTools, Is.Not.Empty);
        Assert.That(names, Has.Length.EqualTo(ClaudeAdvancedBaseline.Length - advancedOnlyTools.Length));
        Assert.That(names.Intersect(advancedOnlyTools), Is.Empty);
        Assert.That(names, Is.SubsetOf(ClaudeAdvancedBaseline));
        Assert.That(names, Is.SupersetOf(BasicCoreTools.Where(t => t != "McpToolsetControl")));
        Assert.That(names, Does.Not.Contain("McpToolsetControl"));
    }

    // ---- registry and startup parsing ----

    [Test]
    public void Registry_ListsClaudeLean_InBothServersMaps_AndCoreSetMatches()
    {
        Assert.That(ToolClassRegistry.BasicModeToToolClasses.ContainsKey("claude-lean"), Is.True);
        Assert.That(ToolClassRegistry.AdvancedModeToToolClasses.ContainsKey("claude-lean"), Is.True);
        Assert.That(ToolClassRegistry.ClaudeLeanToolNames, Is.EquivalentTo(CoreTools), "ToolClassRegistry.ClaudeLeanToolNames and ClaudeLeanModeTests.CoreTools must agree; edit both");
    }

    [Test]
    [Category("ServerStatusTools")] // sentinel:auto-category
    public void Registry_EveryCoreToolName_IsDeclaredByAToolMethod()
    {
        // Guards against a typo in the allow-list silently dropping a tool from the surface.
        var declared = McpToolSchemaPatcher
            .DiscoverAllDeclaredTools(typeof(ServerStatusTools).Assembly, typeof(SubAgentTools).Assembly)
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(declared, Is.SupersetOf(CoreTools));
    }

    [TestCase("all")]
    [TestCase("ALL")]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ParseArgs_All_DoesNotExpandToClaudeLean(string all)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={all}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Not.Contain("claude-lean"));
        Assert.That(activeModes, Does.Contain("Claude"));
    }

    [TestCase("claude-lean")]
    [TestCase("all,claude-lean")]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ParseArgs_NamedExplicitly_ActivatesClaudeLean(string modeArg)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={modeArg}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Contain("claude-lean"));
    }

    [Test]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ResolveActiveToolClasses_ClaudeLeanAlone_YieldsExactlyTheRegistryClaudeLeanClasses()
    {
        var classes = ServerStartupHelpers.ResolveActiveToolClasses(
            new HashSet<string>(["claude-lean"], StringComparer.OrdinalIgnoreCase),
            ToolClassRegistry.BasicModeToToolClasses,
            new HashSet<string>(),
            new HashSet<string>());

        var registered = ToolClassRegistry.BasicModeToToolClasses["claude-lean"];
        Assert.That(classes, Has.Count.EqualTo(registered.Length), "claude-lean alone must resolve to exactly ToolClassRegistry.BasicModeToToolClasses[claude-lean]; edit ClaudeLeanToolClasses");
        Assert.That(classes, Is.EquivalentTo(registered));
        Assert.That(classes, Does.Contain("ToolsetControlTools"));
        Assert.That(classes, Does.Contain("DeclarationTools"));
        Assert.That(classes, Does.Contain("ParameterEditTools"));
        Assert.That(classes, Does.Contain("SemanticFindReplaceTools"));
        Assert.That(classes, Does.Contain("NamedArgumentsTools"));
        Assert.That(classes, Does.Contain("TestCategoryTaggingTools"));
        Assert.That(classes, Does.Contain("SubAgentTools"));
        Assert.That(classes, Does.Not.Contain("AdvancedRefactoringTools"));
        Assert.That(classes, Does.Not.Contain("DocumentationTools"));
    }

    [Test]
    public void TagTestCategories_IsRegisteredInBothModeMaps_AndOnDemandOnlyInClaudeLean()
    {
        Assert.That(ToolClassRegistry.BasicModeToToolClasses["TestCategories"], Is.EqualTo(new[] { "TestCategoryTaggingTools" }));
        Assert.That(ToolClassRegistry.AdvancedModeToToolClasses["TestCategories"], Is.EqualTo(new[] { "TestCategoryTaggingTools" }));
        Assert.That(ToolClassRegistry.ClaudeLeanOnDemandToolClasses, Does.Contain("TestCategoryTaggingTools"));

        // Off by default in claude-lean: the class is active but the tool is not allow-listed; the toolset switches it on.
        Assert.That(ToolClassRegistry.ClaudeLeanToolNames, Does.Not.Contain("TagTestCategories"));
        Assert.That(ToolsetCatalog.FindSet("TagTestCategories"), Is.EqualTo(ToolSetName.testCategories));
        Assert.That(ToolsetCatalog.AllToolNames, Does.Contain("TagTestCategories"));
    }

    [Test]
    public void QualifyCrefs_IsRegisteredInBothModeMaps_AndOnDemandOnlyInClaudeLean()
    {
        Assert.That(ToolClassRegistry.BasicModeToToolClasses["QualifyCrefs"], Is.EqualTo(new[] { "QualifyCrefsTools" }));
        Assert.That(ToolClassRegistry.AdvancedModeToToolClasses["QualifyCrefs"], Is.EqualTo(new[] { "QualifyCrefsTools" }));
        Assert.That(ToolClassRegistry.ClaudeLeanOnDemandToolClasses, Does.Contain("QualifyCrefsTools"));

        // Off by default in claude-lean: the class is active but the tool is not allow-listed; the toolset switches it on.
        Assert.That(ToolClassRegistry.ClaudeLeanToolNames, Does.Not.Contain("QualifyCrefs"));
        Assert.That(ToolsetCatalog.FindSet("QualifyCrefs"), Is.EqualTo(ToolSetName.moveExtract));
        Assert.That(ToolsetCatalog.AllToolNames, Does.Contain("QualifyCrefs"));
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
