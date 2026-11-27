using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

// McpServerStatus is the only tool tagged Whole (plan_inline_budget_classes Q4). Its listing and settings views
// must still fit the Whole threshold, or the marshaller offloads a response that callers need whole.
[TestFixture]
[NonParallelizable]
public class McpServerStatusWireSizeTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        // Same call as the host (ServerHttp.Startup): ConfigureAll(args, <Server.Advanced assembly>, Server).
        // ToolClassRegistry is public and lives in that assembly, so its assembly is ServerHttp's.
        StartupSettings.ConfigureAll([], typeof(ToolClassRegistry).Assembly, SettingScope.Server);
    }

    [TearDown]
    public void TearDown()
    {
        // LastReport has a private setter, so the previous value cannot be written back. An empty ConfigureAll
        // (no settings in scope) installs the same empty report the process starts with, as ServerStatusSettingsTests does.
        StartupSettings.ConfigureAll([], Array.Empty<StartupSetting>(), SettingScope.None, haltOnErrors: false);
        _manager.Dispose();
    }

    [Test]
    public void ListingAndSettingsFitWholeThreshold()
    {
        var tools = new ServerStatusTools(
            _manager,
            LeanSurface(),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = tools.McpServerStatusTool(
            "test reason",
            toolListing: McpServerStatusToolListing.all,
            settings: McpServerStatusSettingsListing.all);

        string wire = JsonSerializer.Serialize(result, SentinelToolFactory.WireSerializerOptions);
        int length = wire.Length;

        // Sanity first: the envelope must be over the default threshold, or the Whole assert below could pass vacuously.
        Assert.That(
            length,
            Is.GreaterThan(LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default)),
            $"fixture sanity: McpServerStatus toolListing=all settings=all is {length} chars, expected over the Default offload threshold");

        Assert.That(
            length,
            Is.LessThanOrEqualTo(LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Whole)),
            $"McpServerStatus toolListing=all settings=all serialized to {length} chars, over the Whole offload threshold of {LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Whole)} chars");
    }

    [Test]
    public void ToolIsTaggedWhole()
    {
        var method = typeof(ServerStatusTools).GetMethod(nameof(ServerStatusTools.McpServerStatusTool));
        Assert.That(method, Is.Not.Null, "ServerStatusTools.McpServerStatusTool must exist");

        var attribute = method!.GetCustomAttribute<InlineBudgetAttribute>();
        Assert.That(attribute, Is.Not.Null, "McpServerStatus carries [InlineBudget]");
        Assert.That(attribute!.BudgetClass, Is.EqualTo(InlineBudgetClass.Whole));
        Assert.That(LargeResultHelper.ClassFor(method, errorOnly: false), Is.EqualTo(InlineBudgetClass.Whole));
    }

    // Every declared tool class is passed in, so toolListing=all covers the whole registry as in production.
    // The allow-list is the claude-lean set, so the active surface is claude-lean sized.
    private static ActiveToolSurface LeanSurface()
    {
        var allClasses = ToolClassRegistry.BasicToolClassTypes
            .Concat(ToolClassRegistry.AdvancedToolClassTypes)
            .Select(t => t.Name)
            .ToHashSet();
        var none = new HashSet<string>();

        return new ActiveToolSurface(
            "claude-lean",
            new HashSet<string> { "claude-lean" },
            none,
            none,
            allClasses,
            ToolClassRegistry.BuildClassToModes(),
            allowedToolNames: ToolClassRegistry.ClaudeLeanToolNames.ToHashSet());
    }
}
