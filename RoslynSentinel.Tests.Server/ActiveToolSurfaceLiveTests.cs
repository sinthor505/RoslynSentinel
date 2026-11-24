// Coverage for plan_tool_surface_controller_stage_e.md Steps 17-18: ActiveToolSurface.IsCallable is the startup rule OR a live
// snapshot row whose origin is a runtime toolset. Same in-process claude-lean host shape as ToolSurfaceControllerTests
// (the host is never started), reading the ActiveToolSurface that the registration binds to the controller.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions (HiddenParameters), HiddenSchemaParams and the validator schema cache
[Category("ToolClassRegistry")]
public class ActiveToolSurfaceLiveTests
{
    private const string ClaudeLeanMode = "claude-lean";

    // ListSolutionItems is in the on-demand projectAdmin set; claude-lean does not serve it at startup.
    private const string ListSolutionItemsTool = "ListSolutionItems";

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // Process-static: the profile and explicit hidden list, the hidden-name registry, and the validator's schema cache.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
    }

    // Builds a claude-lean host (not started), the same way ToolSurfaceControllerTests.BuildHarness does.
    private static IHost BuildHost()
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

        return hostBuilder.Build();
    }

    [Test]
    public void ToolsetEnable_MakesItsToolsCallable_DisableRevokes()
    {
        using var host = BuildHost();
        var surface = host.Services.GetRequiredService<ActiveToolSurface>();
        var toolsets = host.Services.GetRequiredService<ToolsetService>();
        host.Services.GetRequiredService<ToolSurfaceController>().Refresh("test");
        Assert.That(surface.IsCallable(ListSolutionItemsTool), Is.False, "claude-lean does not serve it at startup");

        toolsets.SetEnabled(ToolSetName.projectAdmin, true);
        Assert.That(surface.IsCallable(ListSolutionItemsTool), Is.True, "the enabled toolset's tool is callable");

        toolsets.SetEnabled(ToolSetName.projectAdmin, false);
        Assert.That(surface.IsCallable(ListSolutionItemsTool), Is.False, "disabling the set revokes it");
    }

    [Test]
    public void BeforeFirstSnapshot_UsesTheStartupRule_AndForcesNoBuild()
    {
        using var host = BuildHost();
        var surface = host.Services.GetRequiredService<ActiveToolSurface>();
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        Assert.That(controller.CurrentIfBuilt, Is.Null, "nothing has read the snapshot yet");

        Assert.That(surface.IsCallable("ReadFile"), Is.True, "a startup tool answers by the startup rule");
        Assert.That(surface.IsCallable(ListSolutionItemsTool), Is.False, "a toolset tool is not callable before any snapshot");

        Assert.That(controller.CurrentIfBuilt, Is.Null, "IsCallable must not build the snapshot");
    }

    [Test]
    public void StartupToolsetRestore_IsCallableAfterTheFirstSnapshot()
    {
        using var host = BuildHost();
        var surface = host.Services.GetRequiredService<ActiveToolSurface>();
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();

        ServerStartupHelpers.ApplyStartupToolsets(host.Services, ["--toolsets=git"]);
        controller.Refresh("startup");

        Assert.That(surface.IsCallable("GitRemote"), Is.True);
    }

    [Test]
    public void UnboundSurface_KeepsTheStartupRule()
    {
        var none = new HashSet<string>();
        var allowList = new HashSet<string> { "ReadFile" };

        var unbound = new ActiveToolSurface("test", none, none, none, new HashSet<string> { "WorkspaceTools" }, null, allowList);
        Assert.That(unbound.IsCallable("ReadFile"), Is.True);
        Assert.That(unbound.IsCallable(ListSolutionItemsTool), Is.False, "class active but the name is not on the allow-list");

        var boundToNothing = unbound.WithLiveSnapshot(() => null);
        Assert.That(boundToNothing.IsCallable("ReadFile"), Is.True);
        Assert.That(boundToNothing.IsCallable(ListSolutionItemsTool), Is.False, "no snapshot built: the startup rule alone");
    }
}
