// Plan: plan_startup_configuration_registry.md, test T8. McpServerStatus reports StartupSettings.LastReport in its
// Configuration block: problems, halt state and the undeclared-env count always; the setting rows and the undeclared
// names only with settings != none; the hint only when the names are omitted and the count is above 0. The tool is
// called in-process after ConfigureAll over a fixture setting list (flags --test-fixture-*, env TESTFIXTURE_*).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>Fixture group for the status tests: one setting per source and kind the status block distinguishes.</summary>
[StartupSettingGroup("Status settings fixture")]
public static class StatusSettingsFixture
{
    public static readonly StartupSetting<int> CountSetting = StartupSetting.Int(
        "--test-fixture-status-count", "TESTFIXTURE_STATUS_COUNT", 7, "Status fixture count.", SettingScope.Server | SettingScope.ServerStdio);

    public static readonly StartupSetting<bool> SwitchSetting = StartupSetting.Bool(
        "--test-fixture-status-switch", null, false, "Status fixture switch.", SettingScope.Server);

    public static readonly StartupSetting<string?> NameSetting = StartupSetting.String(
        null, "TESTFIXTURE_STATUS_NAME", "dflt", "Status fixture name.", SettingScope.Server);

    public static readonly StartupSetting<string?> ExternalSetting = StartupSetting.String(
        null, "TESTFIXTURE_STATUS_EXTERNAL", null, "Status fixture external variable.", SettingScope.Server, SettingKind.External);
}

// StartupSettings.EnvironmentReader, EnvironmentNames and LastReport are process-wide static state, and the fixture
// group's setting values are static too, so this fixture must not overlap another fixture that reads or sets them.
[TestFixture]
[NonParallelizable]
public class ServerStatusSettingsTests
{
    // The env-var declaration guard (plan T10) treats a whole literal ROSLYNSENTINEL_<NAME> as a name; building this one
    // by concatenation keeps the guard from reading an intentionally undeclared name as a declaration to check.
    private static readonly string UndeclaredName = "ROSLYNSENTINEL" + "_TEST_UNDECLARED";

    private const string ExternalEnv = "TESTFIXTURE_STATUS_EXTERNAL";
    private const string NameEnv = "TESTFIXTURE_STATUS_NAME";

    private Func<string, string?> _savedReader = null!;
    private Func<IEnumerable<string>> _savedNames = null!;
    private Dictionary<string, string?> _env = null!;
    private List<string> _envNames = null!;
    private IReadOnlyList<StartupSetting> _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _savedReader = StartupSettings.EnvironmentReader;
        _savedNames = StartupSettings.EnvironmentNames;

        _env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NameEnv] = "alpha",
            [ExternalEnv] = "secret-value",
        };
        _envNames = [NameEnv, ExternalEnv, UndeclaredName];
        StartupSettings.EnvironmentReader = name => _env.TryGetValue(name, out var v) ? v : null;
        StartupSettings.EnvironmentNames = () => _envNames;

        _settings = StartupSettings.Discover([typeof(StatusSettingsFixture)]);
        StartupSettings.ConfigureAll(["--test-fixture-status-count=9"], _settings, SettingScope.Server, haltOnErrors: false);
    }

    [TearDown]
    public void TearDown()
    {
        // Put the fixture group back on defaults and LastReport back to empty before the real delegates return.
        _env.Clear();
        _envNames.Clear();
        StartupSettings.ConfigureAll([], _settings, SettingScope.Server, haltOnErrors: false);
        StartupSettings.ConfigureAll([], Array.Empty<StartupSetting>(), SettingScope.None, haltOnErrors: false);

        StartupSettings.EnvironmentReader = _savedReader;
        StartupSettings.EnvironmentNames = _savedNames;
    }

    private static IEnumerable<McpServerStatusSetting> Rows(McpServerStatusConfiguration configuration) =>
        configuration.SettingGroups!.SelectMany(g => g.Settings);

    private static McpServerStatusConfiguration StatusConfiguration(McpServerStatusSettingsListing listing)
    {
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tools = new ServerStatusTools(
            manager,
            new ActiveToolSurface("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason", settings: listing);
        Assert.That(result.IsError, Is.False);
        return result.SuccessData!.Configuration!;
    }

    [Test]
    public void SettingsNone_OmitsRowsAndNames_AndCarriesTheHint()
    {
        var configuration = StatusConfiguration(McpServerStatusSettingsListing.none);

        Assert.That(configuration.SettingGroups, Is.Null);
        Assert.That(configuration.UndeclaredEnv, Is.Null, "names ride only with settings != none");
        Assert.That(configuration.UndeclaredEnvCount, Is.EqualTo(1), "the count is always present");
        Assert.That(configuration.UndeclaredEnvHint, Is.EqualTo(StartupSettings.UndeclaredEnvHint));
        Assert.That(configuration.Problems, Is.Empty);
        Assert.That(configuration.Halted, Is.False);
    }

    [Test]
    public void SettingsNonDefault_ListsOnlyRowsNotAtDefault_AndTheUndeclaredNames()
    {
        var configuration = StatusConfiguration(McpServerStatusSettingsListing.nonDefault);

        var rows = Rows(configuration).ToDictionary(r => r.Flag ?? r.Env);
        Assert.That(rows.Keys, Is.EquivalentTo(new[]
        {
            "--test-fixture-status-count", "TESTFIXTURE_STATUS_NAME", "TESTFIXTURE_STATUS_EXTERNAL",
        }));
        Assert.That(rows["--test-fixture-status-count"].Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(rows["--test-fixture-status-count"].Value, Is.EqualTo("9"));
        Assert.That(rows["--test-fixture-status-count"].Flag, Is.EqualTo("--test-fixture-status-count"));
        Assert.That(rows["--test-fixture-status-count"].Env, Is.EqualTo("TESTFIXTURE_STATUS_COUNT"));
        var countGroup = configuration.SettingGroups!.Single(g => g.Settings.Any(r => r.Flag == "--test-fixture-status-count"));
        Assert.That(countGroup.Scopes, Is.EqualTo(new[] { SettingScope.Server, SettingScope.ServerStdio }),
            "one entry per flag of Scope, never a combined flags string");
        Assert.That(rows["TESTFIXTURE_STATUS_NAME"].Source, Is.EqualTo(SettingSource.Env));
        Assert.That(rows["TESTFIXTURE_STATUS_NAME"].Value, Is.EqualTo("alpha"));
        Assert.That(rows["TESTFIXTURE_STATUS_EXTERNAL"].Source, Is.EqualTo(SettingSource.Env));

        Assert.That(configuration.UndeclaredEnv, Is.EqualTo(new[] { UndeclaredName }));
        Assert.That(configuration.UndeclaredEnvCount, Is.EqualTo(1));
        Assert.That(configuration.UndeclaredEnvHint, Is.Null, "the hint is only for an omitted name list");
    }

    [Test]
    public void SettingsAll_ListsEveryRow_WithDefaultsAtTheirDefaultText()
    {
        var configuration = StatusConfiguration(McpServerStatusSettingsListing.all);

        var rows = Rows(configuration).ToDictionary(r => r.Flag ?? r.Env);
        Assert.That(rows.Keys, Is.EquivalentTo(new[]
        {
            "--test-fixture-status-count", "--test-fixture-status-switch",
            "TESTFIXTURE_STATUS_NAME", "TESTFIXTURE_STATUS_EXTERNAL",
        }));
        Assert.That(rows["--test-fixture-status-switch"].Source, Is.EqualTo(SettingSource.Default));
        Assert.That(rows["--test-fixture-status-switch"].Value, Is.EqualTo("false"));
        Assert.That(rows["--test-fixture-status-switch"].Env, Is.Null, "a flag-only setting has no env name");
        Assert.That(rows["TESTFIXTURE_STATUS_NAME"].Env, Is.EqualTo(NameEnv));
        Assert.That(configuration.UndeclaredEnv, Is.EqualTo(new[] { UndeclaredName }));
        Assert.That(configuration.UndeclaredEnvHint, Is.Null);
    }

    [Test]
    public void SettingsAll_GroupsRowsByScope()
    {
        var configuration = StatusConfiguration(McpServerStatusSettingsListing.all);
        var groups = configuration.SettingGroups!;

        Assert.That(groups.All(g => g.Scopes.Count > 0), Is.True, "every group names at least one scope");
        Assert.That(groups.Select(g => string.Join(",", g.Scopes)).Distinct().Count(), Is.EqualTo(groups.Count),
            "no two groups share a scope set");

        var countGroup = groups.Single(g => g.Settings.Any(r => r.Flag == "--test-fixture-status-count"));
        Assert.That(countGroup.Scopes, Is.EqualTo(new[] { SettingScope.Server, SettingScope.ServerStdio }));

        var serverGroup = groups.Single(g => g.Settings.Any(r => r.Flag == "--test-fixture-status-switch"));
        Assert.That(serverGroup.Scopes, Is.EqualTo(new[] { SettingScope.Server }));
        Assert.That(serverGroup.Settings.Select(r => r.Flag ?? r.Env), Is.EquivalentTo(new[]
        {
            "--test-fixture-status-switch", "TESTFIXTURE_STATUS_NAME", "TESTFIXTURE_STATUS_EXTERNAL",
        }));
    }

    [Test]
    public void ExternalSetting_ReportsSetOrUnset_NeverTheValue()
    {
        var whileSet = Rows(StatusConfiguration(McpServerStatusSettingsListing.all))
            .Single(r => r.Env == ExternalEnv);
        Assert.That(whileSet.Value, Is.EqualTo("set"));
        Assert.That(whileSet.Value, Does.Not.Contain("secret"));

        // External settings re-read the environment on every access, so removing the variable flips the row without a new ConfigureAll.
        _env.Remove(ExternalEnv);
        var configuration = StatusConfiguration(McpServerStatusSettingsListing.all);
        var whileUnset = Rows(configuration).Single(r => r.Env == ExternalEnv);
        Assert.That(whileUnset.Value, Is.EqualTo("unset"));
        Assert.That(whileUnset.Source, Is.EqualTo(SettingSource.Default));

        var nonDefault = Rows(StatusConfiguration(McpServerStatusSettingsListing.nonDefault));
        Assert.That(nonDefault.Select(r => r.Env), Does.Not.Contain(ExternalEnv));
    }

    [Test]
    public void NoUndeclaredEnv_HasNoHint_AndAnEmptyNameListWhenListed()
    {
        _envNames.Remove(UndeclaredName);
        StartupSettings.ConfigureAll(["--test-fixture-status-count=9"], _settings, SettingScope.Server, haltOnErrors: false);

        var none = StatusConfiguration(McpServerStatusSettingsListing.none);
        Assert.That(none.UndeclaredEnvCount, Is.EqualTo(0), "the count is present even when zero");
        Assert.That(none.UndeclaredEnv, Is.Null);
        Assert.That(none.UndeclaredEnvHint, Is.Null, "the hint needs a count above 0");

        var all = StatusConfiguration(McpServerStatusSettingsListing.all);
        Assert.That(all.UndeclaredEnv, Is.Empty);
        Assert.That(all.UndeclaredEnvHint, Is.Null);
    }
}
