// Parity goldens for the in-process eval registrations (plan_tool_surface_controller_stage_f.md Step 4).
// The cases are the surfaces of EvalSurfaceCallabilityTests.SurfaceCases() plus the EditSnippets arm of WholeFileRewriteAgentTests.
// Each case replicates the registration path of ModelEvalMcpEnvironment.InitializeAsync (the harness ParameterSurface first, then
// the tool registration). The test pins that registration path, not the whole eval world (the world also has a chain host, a
// workspace and a model client that these cases do not build).
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Pins the tool surface each in-process eval registration serves against golden files in <c>ToolSurfaceGolden/</c> beside this file.
/// Deliberate duplication of the Harness fixture ToolSurfaceParityTests (RoslynSentinel.Tests.Harness): the same SetUp, TearDown,
/// environment and static reset, kept verbatim so both fixtures stay isolated the same way. Update the goldens with
/// ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN=1 (see ToolSurfaceGolden).
/// </summary>
[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions and ReplaceSnippetOptions and the ROSLYNSENTINEL_* environment
public class EvalToolSurfaceParityTests
{
    // The one world that serves the full tool schema (validateOnApply listed); every other case hides it, as the harness does by default.
    private const string FullToolSchemaCaseName = "eval-Harness default, toolset=full";

    // Environment variables that feed the process statics reset below; saved in SetUp and restored in TearDown.
    private readonly Dictionary<string, string?> _savedEnvironment = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        _savedEnvironment.Clear();
        foreach (var name in ControlledEnvironmentVariables())
        {
            _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        ResetStatics();
    }

    [TearDown]
    public void TearDown()
    {
        // Statics first, while the variables are still cleared, so they return to their defaults rather than to the caller's environment.
        ResetStatics();
        foreach (var (name, value) in _savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _savedEnvironment.Clear();
    }

    private static IEnumerable<string> ControlledEnvironmentVariables()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "ROSLYNSENTINEL_SCHEMA_PROFILE", "ROSLYNSENTINEL_HIDE_PARAMS" };
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("ROSLYNSENTINEL_REPLACE_SNIPPET_", StringComparison.Ordinal))
            {
                names.Add(key);
            }
        }

        return names;
    }

    private static void ResetStatics()
    {
        // Same reset as HiddenParametersParityTests, plus ReplaceSnippetOptions (its limits appear in the ReplaceSnippet schema text).
        // Both Configure calls read the environment, which SetUp has cleared, so they land on the built-in defaults.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        ReplaceSnippetOptions.Configure([]);
    }

    /// <summary>
    /// Every eval surface: the EvalSurfaceCallabilityTests cases under an "eval-" golden name, then the EditSnippets arm.
    /// </summary>
    public static IEnumerable<TestCaseData> SurfaceCases()
    {
        foreach (var source in EvalSurfaceCallabilityTests.SurfaceCases())
        {
            var caseName = "eval-" + (string)source.Arguments[0]!;
            var register = (Action<IMcpServerBuilder, IServiceCollection>)source.Arguments[1]!;
            yield return new TestCaseData(caseName, register).SetName(caseName);
        }

        yield return new TestCaseData("eval-EditSnippets-arm", (Action<IMcpServerBuilder, IServiceCollection>)RegisterEditSnippetsArm)
            .SetName("eval-EditSnippets-arm");
    }

    [Test]
    [TestCaseSource(nameof(SurfaceCases))]
    public async Task EvalSurface_MatchesGolden(string caseName, Action<IMcpServerBuilder, IServiceCollection> register)
    {
        var fullToolSchema = caseName == FullToolSchemaCaseName;

        var tools = await ToolSurfaceGolden.ListToolsAsync((mcpBuilder, services) =>
        {
            // ParameterSurface before the tool registration, as in ModelEvalMcpEnvironment.InitializeAsync.
            RegisterHarnessParameterSurface(services, fullToolSchema);
            register(mcpBuilder, services);
        });

        Assert.Multiple(() =>
        {
            Assert.That(tools, Is.Not.Empty);
            Assert.That(tools.Select(t => t.Name).GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
                Is.Empty, "tool names must be unique");
        });

        ToolSurfaceGolden.AssertMatchesGolden(caseName, tools);
    }

    /// <summary>
    /// Replicates ModelEvalMcpEnvironment.InitializeAsync lines 118-128 (the ParameterSurface block): the startup profile rules
    /// without the EnvOrFlag layer, plus the harness rule that hides validateOnApply unless fullToolSchema is set.
    /// </summary>
    private static void RegisterHarnessParameterSurface(IServiceCollection services, bool fullToolSchema)
    {
        var rules = RoslynSentinel.Common.ToolParameterSurfaceOptions.FromStartup().Visibility
            .Where(r => r.Source != RoslynSentinel.Common.ParameterRuleSource.EnvOrFlag).ToList();
        if (!fullToolSchema)
        {
            rules.Add(new RoslynSentinel.Common.ParameterVisibilityRule(null, "validateOnApply", RoslynSentinel.Common.ParameterVisibility.Hide,
                RoslynSentinel.Common.ParameterRuleSource.Harness, "ModelEvalMcpEnvironment"));
        }

        services.AddSingleton(new RoslynSentinel.Common.ParameterSurface(new RoslynSentinel.Common.ToolParameterSurfaceOptions(rules)));
    }

    /// <summary>
    /// The EditSnippets arm, copied from WholeFileRewriteAgentTests.ConfigureToolSurface (WholeFileRewriteAgentTests.cs:350-358):
    /// the ActiveModes literal (WholeFileRewriteAgentTests.cs:279-282, "Refactor", "Workspace"), plus "EditSnippetsExperiment", on the
    /// Basic registration with ReplaceSnippet hidden. The brief named the Advanced method; the eval arm registers Basic, and this
    /// pins what the eval serves.
    /// </summary>
    private static void RegisterEditSnippetsArm(IMcpServerBuilder mcpBuilder, IServiceCollection services)
    {
        var activeModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Refactor", "Workspace", "EditSnippetsExperiment" };
        mcpBuilder.AddRoslynSentinelToolsBasic(
            services,
            activeModes,
            hideTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ReplaceSnippet" });
    }
}
