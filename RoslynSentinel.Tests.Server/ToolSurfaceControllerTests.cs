// Coverage for plan_tool_surface_controller.md Step A3: ToolSurfaceController builds snapshots from the live tool
// collection. Covers the exact tool list, the declaring class per tool, version movement on toolset enable/disable
// (through the Changed subscription, with no explicit Refresh), the no-change refresh, the first-read build and the
// hidden-parameter report. Same in-process host shape as ClaudeLeanSchemaBudgetTests; the host is never started.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions (HiddenParameters), HiddenSchemaParams and the validator schema cache
[Category("ToolClassRegistry")]
public class ToolSurfaceControllerTests
{
    private const string ClaudeLeanMode = "claude-lean";
    private const string DeclarationsSet = "declarations";

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // Process-static: the profile and explicit hidden list, the hidden-name registry, and the validator's schema cache.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        HiddenSchemaParams.Clear();
        ToolArgumentValidator.ClearSchemaCacheForTests();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(IHost host, ToolSurfaceController controller, ToolsetService toolsets)
        {
            Host = host;
            Controller = controller;
            Toolsets = toolsets;
        }

        public IHost Host { get; }

        public ToolSurfaceController Controller { get; }

        public ToolsetService Toolsets { get; }

        public void Dispose() => Host.Dispose();
    }

    // Builds a claude-lean host (not started) and a controller over it. Tools are patched at creation, so any
    // SchemaOptions change must be made before this runs.
    private static Harness BuildHarness()
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ClaudeLeanMode };
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, include);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        var host = hostBuilder.Build();
        var controller = ActivatorUtilities.CreateInstance<ToolSurfaceController>(host.Services);
        var toolsets = host.Services.GetRequiredService<ToolsetService>();
        return new Harness(host, controller, toolsets);
    }

    private static List<string> LiveToolNames(IHost host)
    {
        var collection = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        return collection.Select(tool => tool.ProtocolTool.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    [Test]
    public void Snapshot_ListsExactlyTheLiveCollection()
    {
        using var harness = BuildHarness();

        var snapshot = harness.Controller.Refresh("test");

        var snapshotNames = snapshot.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.That(snapshotNames, Is.EqualTo(LiveToolNames(harness.Host)));
        Assert.That(snapshot.Version, Is.EqualTo(1));
        Assert.That(snapshot.Trigger, Is.EqualTo("test"));
    }

    [Test]
    public void Snapshot_ClassNameMatchesDeclaringClass()
    {
        using var harness = BuildHarness();

        var snapshot = harness.Controller.Refresh("test");

        // McpServerStatus is declared in ServerStatusTools; ReplaceSnippet is declared in WorkspaceTools.
        Assert.That(snapshot.Find("McpServerStatus")?.ClassName, Is.EqualTo("ServerStatusTools"));
        Assert.That(snapshot.Find("ReplaceSnippet")?.ClassName, Is.EqualTo("WorkspaceTools"));
    }

    [Test]
    public void ToolsetEnable_BumpsVersion_AndMarksRuntimeOrigin()
    {
        using var harness = BuildHarness();
        var before = harness.Controller.Refresh("test").Version;

        // No explicit Refresh: the controller's Changed subscription must rebuild the snapshot itself.
        harness.Toolsets.SetEnabled(ToolSetName.declarations, true);

        var current = harness.Controller.Current;
        Assert.That(current.Version, Is.GreaterThan(before));
        Assert.That(current.Trigger, Is.EqualTo("toolCollectionChanged"));
        Assert.That(current.EnabledToolSets, Does.Contain(DeclarationsSet));

        var typedMember = current.Find("TypedMember");
        Assert.That(typedMember, Is.Not.Null);
        Assert.That(typedMember!.Origin, Is.EqualTo(ToolSurfaceOrigin.RuntimeToolset));
        Assert.That(typedMember.ToolSet, Is.EqualTo(DeclarationsSet));
    }

    [Test]
    public void ToolsetDisable_DropsSetAndTools()
    {
        using var harness = BuildHarness();
        harness.Controller.Refresh("test");
        harness.Toolsets.SetEnabled(ToolSetName.declarations, true);
        var enabledVersion = harness.Controller.Current.Version;

        harness.Toolsets.SetEnabled(ToolSetName.declarations, false);

        var current = harness.Controller.Current;
        Assert.That(current.Version, Is.GreaterThan(enabledVersion));
        Assert.That(current.EnabledToolSets, Does.Not.Contain(DeclarationsSet));
        Assert.That(current.IsExposed("TypedMember"), Is.False);
    }

    [Test]
    public void RefreshWithoutChange_KeepsVersion()
    {
        using var harness = BuildHarness();

        var first = harness.Controller.Refresh("first");
        var second = harness.Controller.Refresh("second");

        Assert.That(second.Version, Is.EqualTo(first.Version));
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void FirstRead_BuildsWithoutRefresh()
    {
        using var harness = BuildHarness();

        var current = harness.Controller.Current;

        Assert.That(current.Version, Is.EqualTo(1));
        Assert.That(current.Trigger, Is.EqualTo("firstRead"));
        Assert.That(current.Tools, Is.Not.Empty);
    }

    [Test]
    public void HiddenParameters_ReportedPerTool()
    {
        // Set before the host is built: tool schemas are patched at creation.
        SchemaOptions.Configure(["--hide-params=validateOnApply"]);
        using var harness = BuildHarness();

        var snapshot = harness.Controller.Refresh("test");

        var replaceSnippet = snapshot.Find("ReplaceSnippet");
        Assert.That(replaceSnippet, Is.Not.Null);
        Assert.That(replaceSnippet!.HiddenParameters, Does.Contain("validateOnApply"));
        Assert.That(replaceSnippet.ListedParameters, Does.Not.Contain("validateOnApply"));
    }
}
