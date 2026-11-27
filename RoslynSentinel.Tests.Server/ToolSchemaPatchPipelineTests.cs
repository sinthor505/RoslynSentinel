// Coverage for plan_tool_surface_controller.md Step B4: the IToolSchemaPatch pipeline. Covers the fallback when no
// patches are registered (schemas identical to the default registration), a host-added patch changing one listed
// description, RemoveAll plus the other three defaults keeping a hidden parameter listed, and the snapshot recording
// the patch names in apply order. Same in-process host shape as ToolSurfaceControllerTests; the host is never started.
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions (HiddenParameters), HiddenSchemaParams and the validator schema cache
[Category("ToolClassRegistry")]
public class ToolSchemaPatchPipelineTests
{
    private const string HiddenOption = "--hide-params=validateOnApply";
    private const string HiddenParameterName = "validateOnApply";
    private const string HiddenParametersPatchName = "HiddenParameters";
    private const string ProbeToolName = "ReplaceSnippet";
    private const string Marker = "[TestMarker]";
    private const string MarkerPatchName = "TestMarker";

    // Outside claude-lean, validateOnApply stays on the top level of the schema unless --hide-params names it.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor", "WholeFileWrite" };

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

    // Builds a host (not started) with the Basic tools registered. startupArgs are applied before tool creation.
    // customize runs after the tool registration, so it can RemoveAll or add IToolSchemaPatch registrations the way a
    // host would. Tools are patched at creation, so every option and registration change must happen here.
    private static IHost BuildHost(string[]? startupArgs = null, Action<IServiceCollection>? customize = null)
    {
        SchemaOptions.Configure(startupArgs ?? Array.Empty<string>());

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        customize?.Invoke(services);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        return hostBuilder.Build();
    }

    private static Dictionary<string, (string Description, string Schema)> ListedTools(IHost host)
    {
        var collection = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        return collection.ToDictionary(
            tool => tool.ProtocolTool.Name,
            tool => (tool.ProtocolTool.Description ?? string.Empty, tool.ProtocolTool.InputSchema.GetRawText()),
            StringComparer.Ordinal);
    }

    private static JsonElement ListedSchema(IHost host, string toolName)
    {
        var collection = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        var tool = collection.SingleOrDefault(t => t.ProtocolTool.Name == toolName);
        Assert.That(tool, Is.Not.Null, $"{toolName} should be listed in the tool collection.");

        // Clone so the element outlives any later re-serialisation of the tool.
        return tool!.ProtocolTool.InputSchema.Clone();
    }

    private static HashSet<string> TopLevelProperties(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    [Test]
    public void DefaultPipeline_SchemasMatchUnregisteredFallback()
    {
        using var defaultHost = BuildHost();
        using var fallbackHost = BuildHost(customize: services => services.RemoveAll<IToolSchemaPatch>());

        var defaults = ListedTools(defaultHost);
        var fallback = ListedTools(fallbackHost);

        Assert.That(defaults, Is.Not.Empty);
        Assert.That(fallback.Keys, Is.EquivalentTo(defaults.Keys), "Both hosts should list the same tool names.");
        foreach (var name in defaults.Keys)
        {
            Assert.That(fallback[name].Schema, Is.EqualTo(defaults[name].Schema), $"{name} input schema should match the default pipeline.");
            Assert.That(fallback[name].Description, Is.EqualTo(defaults[name].Description), $"{name} description should match the default pipeline.");
        }
    }

    [Test]
    public void ExtraPatch_RegisteredByHost_ChangesListedSchema()
    {
        using var defaultHost = BuildHost();
        using var markedHost = BuildHost(customize: services => services.AddSingleton<IToolSchemaPatch>(new MarkerPatch()));

        var defaults = ListedTools(defaultHost);
        var marked = ListedTools(markedHost);

        Assert.That(defaults[ProbeToolName].Description, Does.Not.Contain(Marker), "The default host should not carry the test marker.");
        Assert.That(marked[ProbeToolName].Description, Does.EndWith(Marker), $"{ProbeToolName} should carry the host-registered marker.");

        foreach (var name in defaults.Keys.Where(n => n != ProbeToolName))
        {
            Assert.That(marked[name].Schema, Is.EqualTo(defaults[name].Schema), $"{name} input schema should be untouched by the marker patch.");
            Assert.That(marked[name].Description, Is.EqualTo(defaults[name].Description), $"{name} description should be untouched by the marker patch.");
        }
    }

    [Test]
    public void RemovedDefaultPatch_IsNotApplied()
    {
        using var defaultHost = BuildHost(new[] { HiddenOption });
        Assert.That(TopLevelProperties(ListedSchema(defaultHost, ProbeToolName)), Does.Not.Contain(HiddenParameterName),
            "With --hide-params and the default pipeline, the parameter should be hidden from the listed schema.");

        var withoutHiddenPatch = DefaultToolSchemaPatches.All.Where(p => p.Name != HiddenParametersPatchName).ToList();
        Assert.That(withoutHiddenPatch, Has.Count.EqualTo(DefaultToolSchemaPatches.All.Count - 1));

        using var withoutHiddenHost = BuildHost(new[] { HiddenOption }, services =>
        {
            services.RemoveAll<IToolSchemaPatch>();
            foreach (var patch in withoutHiddenPatch)
            {
                services.AddSingleton<IToolSchemaPatch>(patch);
            }
        });
        Assert.That(TopLevelProperties(ListedSchema(withoutHiddenHost, ProbeToolName)), Does.Contain(HiddenParameterName),
            "Without the HiddenParameters patch, the parameter should stay listed even though --hide-params names it.");
    }

    [Test]
    public void Snapshot_ListsPatchNamesInApplyOrder()
    {
        var defaultNames = DefaultToolSchemaPatches.All.Select(p => p.Name).ToList();

        using var defaultHost = BuildHost();
        var defaultController = ActivatorUtilities.CreateInstance<ToolSurfaceController>(defaultHost.Services);
        Assert.That(defaultController.Current.SchemaPatches, Is.EqualTo(defaultNames));

        using var markedHost = BuildHost(customize: services => services.AddSingleton<IToolSchemaPatch>(new MarkerPatch()));
        var markedController = ActivatorUtilities.CreateInstance<ToolSurfaceController>(markedHost.Services);
        Assert.That(markedController.Current.SchemaPatches, Is.EqualTo(defaultNames.Append(MarkerPatchName).ToList()),
            "The host-registered patch has Order 500, so it should run after the four defaults.");
    }

    // Appends the marker to the description of one tool. Order 500 places it after the four defaults.
    private sealed class MarkerPatch : IToolSchemaPatch
    {
        public string Name => MarkerPatchName;

        public int Order => 500;

        public void Apply(McpServerTool tool, MethodInfo toolMethod)
        {
            if (tool.ProtocolTool.Name == ProbeToolName)
            {
                tool.ProtocolTool.Description = (tool.ProtocolTool.Description ?? string.Empty) + " " + Marker;
            }
        }
    }
}
