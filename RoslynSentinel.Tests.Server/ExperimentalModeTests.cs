// Coverage for proposal_expose_dormant_codetransform_engines.md, Phase 1: the opt-in "Experimental" mode
// hosts CodeTransformTools, "all" never expands to it, and the class resolves from the real production
// registration path. No solution load is needed.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ToolClassRegistry")] // sentinel:auto-category
public class ExperimentalModeTests
{
    [TestCase("all")]
    [TestCase("ALL")]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ParseArgs_All_DoesNotExpandToExperimental(string all)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={all}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Not.Contain("Experimental"));
        Assert.That(activeModes, Does.Contain("Claude"));
    }

    [TestCase("Experimental")]
    [TestCase("experimental")]
    [TestCase("all,Experimental")]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ParseArgs_NamedExplicitly_ActivatesExperimental(string modeArg)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs([$"--mode={modeArg}"], allModes, out _, out var activeModes, out _, out _, out _, out _, out _);

        Assert.That(activeModes, Does.Contain("Experimental").IgnoreCase);
    }

    [Test]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ResolveActiveToolClasses_ExperimentalAlone_YieldsCodeTransformToolsOnly()
    {
        var classes = ServerStartupHelpers.ResolveActiveToolClasses(
            new HashSet<string>(["Experimental"], StringComparer.OrdinalIgnoreCase),
            ToolClassRegistry.AdvancedModeToToolClasses,
            new HashSet<string>(),
            new HashSet<string>());

        Assert.That(classes, Is.EquivalentTo(new[] { "CodeTransformTools" }));
    }

    [Test]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void ResolveActiveToolClasses_ClaudeMode_DoesNotIncludeCodeTransformTools()
    {
        var classes = ServerStartupHelpers.ResolveActiveToolClasses(
            new HashSet<string>(["Claude"], StringComparer.OrdinalIgnoreCase),
            ToolClassRegistry.AdvancedModeToToolClasses,
            new HashSet<string>(),
            new HashSet<string>());

        Assert.That(classes, Does.Not.Contain("CodeTransformTools"));
    }

    [Test]
    [Category("ServerStartupHelpers")] // sentinel:auto-category
    public void IncludeTools_CodeTransformTools_IsNormalizedToTheRegisteredName()
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

        ServerStartupHelpers.ParseArgs(
            ["--mode=Claude", "--include-tools=codetransformtools"], allModes,
            out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);
        var classes = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes, ToolClassRegistry.AdvancedModeToToolClasses, includeTools, excludeTools);

        Assert.That(classes, Does.Contain("CodeTransformTools").IgnoreCase);
    }

    [Test]
    public void CodeTransformTools_ResolvesFromProductionRegistration_WhenExperimentalIsActive()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRoslynSentinelEnginesAdvanced();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(
            services, new HashSet<string>(["Experimental"], StringComparer.OrdinalIgnoreCase));

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetService<CodeTransformTools>();

        Assert.That(tools, Is.Not.Null, "CodeTransformTools did not resolve; check its 25 constructor dependencies are registered.");
    }

    [Test]
    public void CodeTransformCatalogues_ListTheDispatchedTransformCounts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TransformNames(CodeTransformCatalogues.ApplyFileCodeTransformOptions()), Has.Length.EqualTo(25));
            Assert.That(TransformNames(CodeTransformCatalogues.ApplyMethodCodeTransformOptions()), Has.Length.EqualTo(17));
            Assert.That(TransformNames(CodeTransformCatalogues.ApplyClassCodeTransformOptions()), Has.Length.EqualTo(13));
        });
    }

    private static string[] TransformNames(ToolOptionsResult options) =>
        (string[])options.StructuredOptions!["transforms"];
}
