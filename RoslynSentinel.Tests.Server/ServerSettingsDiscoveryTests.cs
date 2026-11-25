// Plan: plan_startup_configuration_registry.md, tests T12 and T13. The server discovers its setting groups from the
// Server.Advanced assembly (Program.Main and the host builders do the same), and --list-options prints exactly the
// Server/ServerStdio-scope part of that registry.
//
// Discover only reflects over the assemblies and caches group instances; it neither reads StartupSettings.EnvironmentReader
// nor sets LastReport, so this fixture needs no [NonParallelizable].

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ServerSettingsDiscoveryTests
{
    private static IReadOnlyList<StartupSetting> DiscoverServerAssembly() => StartupSettings.Discover(typeof(ServerStdio).Assembly);

    [Test]
    public void Discover_FindsServerGroups()
    {
        var settings = DiscoverServerAssembly();
        var names = settings.Select(s => s.Name).ToList();

        // Server.Advanced itself.
        Assert.That(names, Has.Some.StartWith("ServerHostSettings."), "Server.Advanced group (ServerHostSettings) missing");
        Assert.That(names, Does.Contain("ServerHostSettings.Transport"));

        // Common (reached through Server.Advanced's references).
        Assert.That(names, Has.Some.StartWith("LlmOptions."), "Common group (LlmOptions) missing");
        Assert.That(names, Has.Some.StartWith("ErrorPolicyOptions."), "Common group (ErrorPolicyOptions) missing");

        // External environment group (kind External).
        Assert.That(names, Has.Some.StartWith("ExternalEnvironmentSettings."), "External group missing");
        Assert.That(names, Does.Contain("ExternalEnvironmentSettings.ClaudeCodeSessionId"));
        Assert.That(
            settings.Where(s => s.Name.StartsWith("ExternalEnvironmentSettings.", StringComparison.Ordinal)).Select(s => s.Kind),
            Is.All.EqualTo(SettingKind.External));

        // AgentLoop groups: Discover uses GetReferencedAssemblies, which omits a project reference whose types are never used.
        Assert.That(names, Has.Some.StartWith("ThinkingOptions."), "AgentLoop group (ThinkingOptions) missing");
        Assert.That(names, Has.Some.StartWith("LoopAssistOptions."), "AgentLoop group (LoopAssistOptions) missing");

        // Names are the registry's identity: unique across the whole discovery.
        Assert.That(names, Is.Unique);
    }

    [Test]
    public void ListOptions_RowsMatchRegistry()
    {
        // The same filter Program.Main applies for --list-options.
        var serverScope = DiscoverServerAssembly()
            .Where(s => (s.Scope & (SettingScope.Server | SettingScope.ServerStdio)) != SettingScope.None)
            .ToList();
        Assert.That(serverScope, Is.Not.Empty);

        var text = ConfigurationReferenceRenderer.RenderConsole(serverScope);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        // A row is "<flag, env | flag | env>  <type>  default <text>  <description>": the id ends at the first double space.
        var rowIds = lines.Select(l => l[..l.IndexOf("  ", StringComparison.Ordinal)]).ToList();
        var expectedIds = serverScope.Select(s =>
            s.Flag is not null && s.EnvName is not null ? $"{s.Flag}, {s.EnvName}" : s.Flag ?? s.EnvName ?? s.Name).ToList();

        Assert.That(lines.Length, Is.EqualTo(serverScope.Count), "one console row per registry setting");
        Assert.That(rowIds.ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds), Is.True,
            "console row ids must equal the Server/ServerStdio-scope registry ids");

        // Settings outside the server scopes (for example AgentLoop-only) never appear.
        var outsideServer = DiscoverServerAssembly()
            .Where(s => (s.Scope & (SettingScope.Server | SettingScope.ServerStdio)) == SettingScope.None)
            .Select(s => s.Flag ?? s.EnvName ?? s.Name)
            .ToList();
        Assert.That(rowIds.Where(id => outsideServer.Any(o => id == o || id.StartsWith(o + ", ", StringComparison.Ordinal))), Is.Empty);
    }
}
