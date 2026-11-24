// Umbrella-include pin for plan_tool_surface_controller_stage_f.md Step 20: --include-tools naming a mode (tool-set) rather
// than a tool class is ignored, and the startup warning names it. The served surface is compared through the parity argv
// helper, which goes through ToolSurfaceGolden.ListToolsAsync.
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions and ReplaceSnippetOptions and the ROSLYNSENTINEL_* environment
public class UmbrellaIncludeToolsTests
{
    // Same set as ToolSurfaceParityTests.AllModes (ServerStdio.cs builds it the same way).
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
        // Both Configure calls read the environment, which SetUp has cleared, so they land on the built-in defaults.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        ReplaceSnippetOptions.Configure([]);
    }

    [Test]
    public async Task RefactoringTools_IsIgnored_AsTheStartupWarningSays()
    {
        const string umbrellaArgv = "--include-tools=WorkspaceTools,RefactoringTools";

        var withUmbrella = await ToolSurfaceParityTests.ListToolsForArgvAsync(umbrellaArgv, basic: true);
        var workspaceOnly = await ToolSurfaceParityTests.ListToolsForArgvAsync("--include-tools=WorkspaceTools", basic: true);

        Assert.That(
            withUmbrella.Select(t => t.Name).ToList(),
            Is.Not.Empty.And.EqualTo(workspaceOnly.Select(t => t.Name).ToList()),
            "RefactoringTools is not a tool class, so it must serve exactly the WorkspaceTools surface");

        ServerStartupHelpers.ParseArgs(
            [umbrellaArgv], AllModes, out _, out _, out _, out _, out var includeTools, out _, out _);
        var warning = ServerStartupHelpers.DescribeUnknownIncludeTools(ToolClassRegistry.ModeToToolClasses, includeTools);

        Assert.That(warning, Is.Not.Null.And.Contain("RefactoringTools"));
    }
}
