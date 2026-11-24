// Parity oracle for plan_tool_surface_controller stage D (parameter rules as a controller input). It pins TODAY's
// hidden-parameter stripping: for every listed tool, the schema the host emits with the HiddenParameters patch applied
// through the IToolSchemaPatch pipeline must equal a frozen copy of the pre-refactor stripping applied to the same tool
// built without that patch, and the controller snapshot must record the same removed names. It must pass on the code
// as it is before the refactor lands. Same in-process host shape as ToolSchemaPatchPipelineTests.
using System.Text.Json;
using System.Text.Json.Nodes;

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
public class HiddenParametersParityTests
{
    private const string HiddenParametersPatchName = "HiddenParameters";

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // Same reset as ToolSchemaPatchPipelineTests: profile and explicit hidden list, hidden-name registry, validator schema cache.
        SchemaOptions.Configure(Array.Empty<string>());
        SchemaOptions.ConfigureHiddenParameters(Array.Empty<string>());
        HiddenSchemaParams.Clear();
        ToolArgumentValidator.ClearSchemaCacheForTests();
    }

    // Startup configs x host flavor x mode set. The last config's "validateOnAply" is kept verbatim from the stage D brief.
    private static IEnumerable<TestCaseData> ParityCases()
    {
        var configs = new (string Label, string[] Args)[]
        {
            ("no args", Array.Empty<string>()),
            ("lean profile", new[] { "--schema-profile=lean" }),
            ("hide validateOnApply,returnDiff", new[] { "--hide-params=validateOnApply,returnDiff" }),
            ("lean profile + hide dryRun,validateOnAply", new[] { "--schema-profile=lean", "--hide-params=dryRun,validateOnAply" }),
        };
        var modeSets = new (string Label, string[] Modes)[]
        {
            ("Workspace,Refactor,WholeFileWrite", new[] { "Workspace", "Refactor", "WholeFileWrite" }),
            ("claude-lean", new[] { "claude-lean" }),
        };

        foreach (bool advanced in new[] { false, true })
        {
            string flavor = advanced ? "Advanced" : "Basic";
            foreach (var config in configs)
            {
                foreach (var modeSet in modeSets)
                {
                    yield return new TestCaseData(config.Args, advanced, modeSet.Modes)
                        .SetName($"{flavor} / {config.Label} / {modeSet.Label}");
                }
            }
        }
    }

    // Builds a host (not started). Unlike the pipeline fixture this does not touch SchemaOptions; Capture configures once per case.
    private static IHost BuildParityHost(bool advanced, string[] modes, Action<IServiceCollection>? customize)
    {
        var services = new ServiceCollection();
        if (advanced)
        {
            services.AddRoslynSentinelEnginesAdvanced();
        }
        else
        {
            services.AddRoslynSentinelEnginesBasic();
        }

        var activeModes = new HashSet<string>(modes, StringComparer.OrdinalIgnoreCase);
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mcpBuilder = services.AddMcpServer();
        if (advanced)
        {
            mcpBuilder.AddRoslynSentinelToolsAdvanced(services, activeModes, include);
        }
        else
        {
            mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, include);
        }

        customize?.Invoke(services);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        return hostBuilder.Build();
    }

    // Builds host A (default registration) and host B (HiddenParameters patch removed) for one case and captures the data both tests
    // compare. Tool build is lazy and reads SchemaOptions, so A's collection and controller Current are read before B is built, and
    // the hidden list is read right after Configure. Nothing reconfigures between the two hosts.
    private static (Dictionary<string, string> Listed, Dictionary<string, (string Expected, List<string> Removed)> Oracle, ToolSurfaceSnapshot Snapshot) Capture(
        string[] startupArgs, bool advanced, string[] modes)
    {
        SchemaOptions.Configure(startupArgs);
#pragma warning disable CS0618 // obsolete from stage D Step 7; this oracle keeps reading it
        IReadOnlyList<string> hidden = SchemaOptions.EffectiveHiddenParameters();
#pragma warning restore CS0618

        using var hostA = BuildParityHost(advanced, modes, customize: null);
        var listed = hostA.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText(), StringComparer.Ordinal);
        ToolSurfaceSnapshot snapshot = hostA.Services.GetRequiredService<ToolSurfaceController>().Current;

        var withoutHiddenPatch = DefaultToolSchemaPatches.All.Where(p => p.Name != HiddenParametersPatchName).ToList();
        using var hostB = BuildParityHost(advanced, modes, services =>
        {
            services.RemoveAll<IToolSchemaPatch>();
            foreach (var patch in withoutHiddenPatch)
            {
                services.AddSingleton<IToolSchemaPatch>(patch);
            }
        });

        var oracle = new Dictionary<string, (string Expected, List<string> Removed)>(StringComparer.Ordinal);
        foreach (var tool in hostB.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!)
        {
            string expected = LegacyStrip(tool.ProtocolTool.InputSchema.Clone(), hidden, out List<string> removed);
            oracle[tool.ProtocolTool.Name] = (expected, removed);
        }

        return (listed, oracle, snapshot);
    }

    // Frozen oracle: a copy of the top-level stripping that McpToolSchemaPatcher.ApplyHiddenParameters performs today, without the
    // registration side effects. It returns the schema text the stripping would emit (the original text when nothing is removed).
    // Do not edit it to follow later refactors: a refactor that changes the emitted schema must fail this comparison, not move the oracle.
    private static string LegacyStrip(JsonElement schema, IReadOnlyList<string> hidden, out List<string> removed)
    {
        removed = new List<string>();
        string original = schema.GetRawText();
        if (hidden.Count == 0)
        {
            return original;
        }

        JsonNode? schemaNode = JsonNode.Parse(original);
        if (schemaNode is not JsonObject schemaObj ||
            !schemaObj.TryGetPropertyValue("properties", out JsonNode? propertiesNode) ||
            propertiesNode is not JsonObject propertiesObj)
        {
            return original;
        }

        foreach (string name in hidden)
        {
            if (propertiesObj.Remove(name))
            {
                removed.Add(name);
            }
        }

        if (removed.Count == 0)
        {
            return original;
        }

        if (schemaObj.TryGetPropertyValue("required", out JsonNode? requiredNode) &&
            requiredNode is JsonArray requiredArray)
        {
            for (int i = requiredArray.Count - 1; i >= 0; i--)
            {
                if (requiredArray[i] is JsonValue value &&
                    value.TryGetValue(out string? requiredName) &&
                    requiredName is not null &&
                    removed.Contains(requiredName))
                {
                    requiredArray.RemoveAt(i);
                }
            }
        }

        return JsonSerializer.SerializeToElement(schemaNode).GetRawText();
    }

    [TestCaseSource(nameof(ParityCases))]
    public void ListedSchemas_EqualLegacyStripOfThePipelineWithoutTheHiddenPatch(string[] startupArgs, bool advanced, string[] modes)
    {
        var (listed, oracle, _) = Capture(startupArgs, advanced, modes);

        Assert.That(listed, Is.Not.Empty);
        Assert.That(listed.Keys, Is.EquivalentTo(oracle.Keys), "Both hosts should list the same tool names.");
        foreach (var name in listed.Keys)
        {
            Assert.That(listed[name], Is.EqualTo(oracle[name].Expected),
                $"{name} listed schema should equal the legacy strip of the schema built without the HiddenParameters patch.");
        }
    }

    [TestCaseSource(nameof(ParityCases))]
    public void SnapshotHiddenParameters_EqualLegacyRemovedNames(string[] startupArgs, bool advanced, string[] modes)
    {
        var (listed, oracle, snapshot) = Capture(startupArgs, advanced, modes);

        Assert.That(listed, Is.Not.Empty);
        foreach (var name in listed.Keys)
        {
            var row = snapshot.Find(name);
            Assert.That(row, Is.Not.Null, $"{name} should have a row in the controller snapshot.");

            var actual = row!.HiddenParameters.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var expected = oracle[name].Removed.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(actual, Is.EqualTo(expected), $"{name} snapshot HiddenParameters should equal the legacy removed names.");
        }
    }
}
