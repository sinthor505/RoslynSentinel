// Coverage for plan_tool_surface_controller_stage_c.md Step 7: the runtime-toolset layer is owned by ToolSurfaceController.
// ToolsetService is a facade that writes through the registered controller and reads it back. Same in-process host shape
// as ToolSurfaceControllerTests (the host is never started); one claude-mode host guards the dependency direction
// (controller -> ToolsetService would be a cycle).
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
public class ToolSurfaceControllerWiringTests
{
    private const string ClaudeLeanMode = "claude-lean";
    private const string ClaudeMode = "claude";
    private const string GitSet = "git";
    private const string GitRemoteTool = "GitRemote";

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

    // Builds a host for the given mode (not started), the same way ToolSurfaceControllerTests.BuildHarness does.
    private static IHost BuildHost(string mode)
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mode };
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
    public void LeanHost_ToolsetServiceWritesThroughTheRegisteredController()
    {
        using var host = BuildHost(ClaudeLeanMode);
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        var toolsets = host.Services.GetRequiredService<ToolsetService>();

        toolsets.SetEnabled(ToolSetName.git, true);

        Assert.That(controller.RuntimeToolSets, Does.Contain(ToolSetName.git));

        var snapshot = controller.Refresh("test");
        var gitRemote = snapshot.Find(GitRemoteTool);
        Assert.That(gitRemote, Is.Not.Null);
        Assert.That(gitRemote!.Origin, Is.EqualTo(ToolSurfaceOrigin.RuntimeToolset));
        Assert.That(gitRemote.ToolSet, Is.EqualTo(GitSet));
    }

    [Test]
    public void LeanHost_ToolsetServiceReadsMirrorTheController()
    {
        using var host = BuildHost(ClaudeLeanMode);
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        var toolsets = host.Services.GetRequiredService<ToolsetService>();

        toolsets.SetEnabled(ToolSetName.git, true);

        Assert.That(toolsets.EnabledToolSets, Is.EqualTo(controller.RuntimeToolSets));
        Assert.That(toolsets.DynamicToolNames, Is.EqualTo(controller.RuntimeToolNames));
    }

    [Test]
    public void StartupToolsets_AppliedBeforeStart_AreAttributedInTheFirstSnapshot()
    {
        using var host = BuildHost(ClaudeLeanMode);
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();

        ServerStartupHelpers.ApplyStartupToolsets(host.Services, new[] { "--toolsets=git" });
        var snapshot = controller.Refresh("startup");

        Assert.That(snapshot.Version, Is.EqualTo(1));
        var gitRemote = snapshot.Find(GitRemoteTool);
        Assert.That(gitRemote, Is.Not.Null);
        Assert.That(gitRemote!.Origin, Is.EqualTo(ToolSurfaceOrigin.RuntimeToolset));
        Assert.That(gitRemote.ToolSet, Is.EqualTo(GitSet));
    }

    [Test]
    public void OtherMode_ControllerResolvesWithoutToolsetService()
    {
        using var host = BuildHost(ClaudeMode);

        Assert.That(host.Services.GetRequiredService<ToolSurfaceController>(), Is.Not.Null);
        Assert.That(host.Services.GetService<ToolsetService>(), Is.Null);
    }
}
