// --list-tools must write exactly the names the real server serves for the same registration. Lives in Tests.Harness
// beside ToolSurfaceGolden, which Tests.Server cannot see.
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Checks that <c>ConsoleMode.ListTools</c> (the <c>--list-tools</c> manifest, written with <c>full: true</c>) names exactly the
/// tools a client is served for the same argv: no duplicates, and the same ordinal set of names. Order is not compared, because
/// <c>ConsoleMode.ExtractToolManifest</c> sorts with a culture-sensitive comparer while the server serves registration order.
/// </summary>
[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions and ReplaceSnippetOptions and the ROSLYNSENTINEL_* environment
public class ToolManifestListToolsTests
{
    // Deliberate duplication of ToolSurfaceParityTests (private there): the same AllModes set, environment variables and static
    // resets, so this fixture's registrations see the same process state as the parity goldens.
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
        // Same reset as ToolSurfaceParityTests; both Configure calls read the environment, which SetUp has cleared.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
        ReplaceSnippetOptions.Configure([]);
    }

    [Test]
    [TestCase("--mode=all")]
    [TestCase("--mode=claude-lean")]
    public async Task ListTools_WritesTheServedStartupNames(string argv)
    {
        // Same argv handling as ToolSurfaceParityTests.ListToolsForArgvAsync (Advanced path).
        var args = argv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ServerStartupHelpers.ParseArgs(
            args, AllModes, out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);
        var hideTools = ServerStartupHelpers.ParseHideTools(args);

        Func<IMcpServerBuilder, IServiceCollection, IMcpServerBuilder> registerTools =
            (b, s) => b.AddRoslynSentinelToolsAdvanced(s, activeModes, includeTools, excludeTools, hideTools);

        var scratchFolder = Path.Combine(ScratchRoot.Resolve(), "stage-f");
        Directory.CreateDirectory(scratchFolder);
        var caseName = argv.Replace("--", string.Empty, StringComparison.Ordinal).Replace('=', '-');
        var outputPath = Path.Combine(scratchFolder, "list-tools-" + caseName + ".json");

        ConsoleMode.ListTools(registerTools, outputPath, full: true);

        using var manifest = JsonDocument.Parse(File.ReadAllText(outputPath));
        var writtenNames = manifest.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        // A fresh registration, served through an in-process host.
        var served = await ToolSurfaceGolden.ListToolsAsync((b, s) => registerTools(b, s));
        var servedNames = served.Select(t => t.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(writtenNames, Is.Not.Empty);
            Assert.That(writtenNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
                Is.Empty, "manifest names must be unique");
            Assert.That(
                writtenNames.Order(StringComparer.Ordinal).ToList(),
                Is.EqualTo(servedNames.Order(StringComparer.Ordinal).ToList()),
                "the manifest must name exactly the tools the server serves (ordinal set comparison)");
        });
    }
}
