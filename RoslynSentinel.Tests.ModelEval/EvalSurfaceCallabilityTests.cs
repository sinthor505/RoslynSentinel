using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ModelContextProtocol.Extensions.Tasks;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Pins the callability rule for the in-process eval hosts: no runtime toolset is registered, and
/// ActiveToolSurface.IsCallable on the DI instance matches an unbound copy of the startup rule.
/// Each host is built and its DI container read; it is never started, so no LM Studio, network or model is needed.
/// Assertions (a) and (b) are the real pins. Assertion (c) passes vacuously until the callability switch lands
/// (IsCallable does not read the live snapshot yet). The per-case "exposed vs callable" lines are measurements
/// written to TestContext.Out, not assertions. Covers in-process eval hosts only, not SubAgent child servers.
/// </summary>
[TestFixture]
[NonParallelizable]
public class EvalSurfaceCallabilityTests
{
    public static IEnumerable<TestCaseData> SurfaceCases()
    {
        // The ModelEvalMcpEnvironment default registration, for both toolset values.
        foreach (var toolset in new[] { "lean", "full" })
        {
            yield return Case(
                $"Harness default, toolset={toolset}",
                (mcpBuilder, services) => mcpBuilder.AddRoslynSentinelToolsAdvanced(
                    services,
                    ChainEvalToolSurface.NewModes(),
                    hideTools: ChainEvalToolSurface.NewHiddenTools(toolset, null)));
        }

        // Each edit arm on the Advanced surface, with the lean toolset.
        foreach (var arm in Enum.GetValues<EditArm>())
        {
            yield return Case(
                $"Advanced edit arm {arm}",
                (mcpBuilder, services) => mcpBuilder.AddRoslynSentinelToolsAdvanced(
                    services,
                    EditArmToolSurface.NewAdvancedModes(arm),
                    hideTools: EditArmToolSurface.NewAdvancedHiddenTools(arm, "lean")));
        }

        // Each edit arm on the Basic surface.
        foreach (var arm in Enum.GetValues<EditArm>())
        {
            yield return Case(
                $"Basic edit arm {arm}",
                (mcpBuilder, services) => mcpBuilder.AddRoslynSentinelToolsBasic(
                    services,
                    EditArmToolSurface.NewBasicModes(arm),
                    hideTools: EditArmToolSurface.ArmHiddenTools(arm)));
        }

        // The named surfaces, registered through the same method groups the harness passes as configureToolSurface.
        yield return Case("ModelEvalToolSurfaces.Basic", ModelEvalToolSurfaces.Basic);
        yield return Case("ModelEvalToolSurfaces.WorkspaceAndSymbolNavigation", ModelEvalToolSurfaces.WorkspaceAndSymbolNavigation);
    }

    [Test]
    [TestCaseSource(nameof(SurfaceCases))]
    public void EvalSurface_HasNoRuntimeToolsets_SoIsCallableEqualsTheStartupRule(
        string caseName,
        Action<IMcpServerBuilder, IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = TaskEnabledToolsHelper.SelectExecutionMode);
        register(mcpBuilder, services);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        // Built, never started: only the DI container is read.
        using var host = hostBuilder.Build();

        // Reading Current forces the snapshot build.
        var controller = host.Services.GetRequiredService<ToolSurfaceController>();
        var snapshot = controller.Current;
        var toolsetService = host.Services.GetService<ToolsetService>();
        var live = host.Services.GetRequiredService<ActiveToolSurface>();

        // An unbound copy of the startup rule: the same constructor inputs, without the live-snapshot binding.
        var unbound = new ActiveToolSurface(
            live.ModeArg,
            live.ActiveModes,
            live.IncludeTools,
            live.ExcludeTools,
            live.ActiveToolClasses,
            live.ClassModes,
            live.AllowedToolNames);

        // Only assemblies already loaded are seen by discovery, so the set is taken after the host is built.
        var roslynAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
            .ToArray();
        var declaredNames = McpToolSchemaPatcher.DiscoverAllDeclaredTools(roslynAssemblies)
            .Select(t => t.ToolName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var callabilityMismatches = new List<string>();
        foreach (var name in declaredNames)
        {
            var callable = live.IsCallable(name);
            var unboundCallable = unbound.IsCallable(name);
            if (callable != unboundCallable)
            {
                callabilityMismatches.Add($"{name}: DI={callable} unbound={unboundCallable}");
            }

            // Measurement only: exposed in the snapshot versus callable.
            var exposed = snapshot.IsExposed(name);
            if (exposed != callable)
            {
                TestContext.Out.WriteLine($"[measure] {caseName}: {name} exposed={exposed} callable={callable}");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(toolsetService, Is.Null, $"{caseName}: ToolsetService must not be registered on an eval host");

            var runtimeRows = snapshot.Tools
                .Where(t => t.Origin == ToolSurfaceOrigin.RuntimeToolset)
                .Select(t => t.Name)
                .ToList();
            Assert.That(runtimeRows, Is.Empty, $"{caseName}: snapshot rows with Origin RuntimeToolset");

            Assert.That(
                callabilityMismatches,
                Is.Empty,
                $"{caseName}: IsCallable differs between the DI instance and the unbound copy:{Environment.NewLine}"
                + string.Join(Environment.NewLine, callabilityMismatches));
        });
    }

    private static TestCaseData Case(string name, Action<IMcpServerBuilder, IServiceCollection> register) =>
        new TestCaseData(name, register).SetName(name);
}
