// Parity oracle for plan_tool_surface_controller_stage_f.md: the tool surface a registration serves, pinned in golden files
// (ToolSurfaceGolden/ beside this file) so the registration rewrite of stage F cannot change what a client sees.
// Update the goldens with ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN=1 (see ToolSurfaceGolden).
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions and ReplaceSnippetOptions and the ROSLYNSENTINEL_* environment
public class ToolSurfaceParityTests
{
    // The same set ServerStdio.cs builds as AllModes (a private field there): every key of ToolClassRegistry.ModeToToolClasses.
    private static readonly HashSet<string> AllModes =
        new(ToolClassRegistry.ModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);

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
    /// Serves the tools of one launch: <paramref name="argv"/> (space-separated, e.g. <c>--mode=all</c>) goes through
    /// <see cref="ServerStartupHelpers.ParseArgs"/> and <see cref="ServerStartupHelpers.ParseHideTools"/> as in ServerStdio.Startup, then
    /// through <c>AddRoslynSentinelToolsAdvanced</c> (or <c>AddRoslynSentinelToolsBasic</c> when <paramref name="basic"/>) with the parsed
    /// modes, include, exclude and hide sets. Static-setting flags (--schema-profile, --hide-params) are not applied: no case may use them.
    /// </summary>
    internal static Task<IReadOnlyList<Tool>> ListToolsForArgvAsync(string argv, bool basic = false)
    {
        var args = argv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ServerStartupHelpers.ParseArgs(
            args, AllModes, out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);
        var hideTools = ServerStartupHelpers.ParseHideTools(args);

        return ToolSurfaceGolden.ListToolsAsync((mcpBuilder, services) =>
        {
            if (basic)
            {
                mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, includeTools, excludeTools, hideTools);
            }
            else
            {
                mcpBuilder.AddRoslynSentinelToolsAdvanced(services, activeModes, includeTools, excludeTools, hideTools);
            }
        });
    }

    [Test]
    public async Task ModeAll_Advanced_MatchesGolden()
    {
        var tools = await ListToolsForArgvAsync("--mode=all");

        Assert.Multiple(() =>
        {
            Assert.That(tools, Is.Not.Empty);
            Assert.That(tools.Select(t => t.Name).GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
                Is.Empty, "tool names must be unique");
        });

        ToolSurfaceGolden.AssertMatchesGolden("mode-all-advanced", tools);
    }
}
