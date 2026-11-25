using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[StartupSettingGroup("Registry fixture")]
public static class StartupRegistryFixture
{
    public static readonly StartupSetting<int> CountSetting = StartupSetting.Int("--test-fixture-count", "TESTFIXTURE_COUNT", 2, "Registry fixture count.", SettingScope.Server, minValue: 1);
    public static readonly StartupSetting<bool> FlagSetting = StartupSetting.Bool("--test-fixture-flag", null, false, "Registry fixture flag.", SettingScope.Server);
}

[StartupSettingGroup("Live fixture")]
public static class StartupLiveFixture
{
    public static readonly StartupSetting<int> LiveSetting = StartupSetting.Int(null, "TESTFIXTURE_LIVE", 1, "Live fixture value.", SettingScope.Server, SettingKind.Live);
}

[StartupSettingGroup("Ordered fixture", PostConfigure = nameof(CheckOrder))]
public static class StartupOrderedFixture
{
    public static readonly StartupSetting<int> LowSetting = StartupSetting.Int("--test-fixture-low", "TESTFIXTURE_LOW", 1, "Ordered fixture low bound.", SettingScope.Server);
    public static readonly StartupSetting<int> HighSetting = StartupSetting.Int("--test-fixture-high", "TESTFIXTURE_HIGH", 10, "Ordered fixture high bound.", SettingScope.Server);

    public static IReadOnlyList<ConfigurationProblem> CheckOrder()
    {
        if (LowSetting.Value <= HighSetting.Value)
        {
            return Array.Empty<ConfigurationProblem>();
        }

        return new[] { new ConfigurationProblem(ConfigurationProblemKind.InvalidValue, null, null, null, null, "low <= high") };
    }
}

[StartupSettingGroup("Overlap flag first")]
public static class StartupOverlapFlagFirstFixture
{
    public static readonly StartupSetting<bool> SharedSetting = StartupSetting.Bool("--test-fixture-dup", null, false, "Duplicate flag, first.", SettingScope.Server);
}

[StartupSettingGroup("Overlap flag second")]
public static class StartupOverlapFlagSecondFixture
{
    public static readonly StartupSetting<bool> SharedSetting = StartupSetting.Bool("--test-fixture-dup", null, false, "Duplicate flag, second.", SettingScope.Server | SettingScope.AgentLoop);
}

[StartupSettingGroup("Overlap env first")]
public static class StartupOverlapEnvFirstFixture
{
    public static readonly StartupSetting<int> SharedSetting = StartupSetting.Int(null, "TESTFIXTURE_DUP", 1, "Duplicate env, first.", SettingScope.TestHarness);
}

[StartupSettingGroup("Overlap env second")]
public static class StartupOverlapEnvSecondFixture
{
    public static readonly StartupSetting<int> SharedSetting = StartupSetting.Int(null, "TESTFIXTURE_DUP", 2, "Duplicate env, second.", SettingScope.TestHarness | SettingScope.Server);
}

[StartupSettingGroup("Disjoint server")]
public static class StartupDisjointServerFixture
{
    public static readonly StartupSetting<int> SharedSetting = StartupSetting.Int("--test-fixture-shared", null, 1, "Shared flag for the server scope.", SettingScope.Server);
}

[StartupSettingGroup("Disjoint plan step")]
public static class StartupDisjointPlanStepFixture
{
    public static readonly StartupSetting<int> SharedSetting = StartupSetting.Int("--test-fixture-shared", null, 2, "Shared flag for the runner scope.", SettingScope.PlanStepRunner);
}

// Registry tests mutate StartupSettings static state (EnvironmentReader, EnvironmentNames, LastReport) and
// the shared fixture setting values, so they must not run in parallel.
[NonParallelizable]
public class StartupSettingRegistryTests
{
    private Func<string, string?>? _savedReader;
    private Func<IEnumerable<string>>? _savedNames;

    [SetUp]
    public void SaveAndIsolateRegistryState()
    {
        _savedReader = StartupSettings.EnvironmentReader;
        _savedNames = StartupSettings.EnvironmentNames;
        StartupSettings.EnvironmentReader = _ => null;
        StartupSettings.EnvironmentNames = () => Array.Empty<string>();
    }

    [TearDown]
    public void RestoreRegistryState()
    {
        // LastReport has no public setter and is left as the last ConfigureAll call set it.
        StartupSettings.EnvironmentReader = _savedReader!;
        StartupSettings.EnvironmentNames = _savedNames!;
    }

    private static IReadOnlyList<StartupSetting> Settings(params Type[] groupTypes) => StartupSettings.Discover(groupTypes);

    private static IReadOnlyList<StartupSetting> RegistrySettings() => Settings(typeof(StartupRegistryFixture));

    // ---- T3: unknown flags ----

    [Test]
    public void UnknownFlag_IsProblem()
    {
        var report = StartupSettings.ConfigureAll(
            new[] { "--test-fixture-count=3", "--test-fixture-bogus", "--test-fixture-flag", "true", "--not-declared=1" },
            RegistrySettings(), SettingScope.Server, haltOnErrors: false);

        Assert.That(report.Problems.Select(p => p.Kind).ToArray(),
            Is.EqualTo(new[] { ConfigurationProblemKind.UnknownFlag, ConfigurationProblemKind.UnknownFlag }));
        Assert.That(report.Problems.Select(p => p.Flag), Is.EquivalentTo(new[] { "--test-fixture-bogus", "--not-declared" }));
        Assert.That(report.Problems[0].SettingName, Is.Null, "an unknown flag belongs to no setting");
    }

    [Test]
    public void UnknownFlag_ConsumedValueTokenIsNotAFlag()
    {
        var report = StartupSettings.ConfigureAll(
            new[] { "--test-fixture-count", "4", "--test-fixture-flag", "no" },
            RegistrySettings(), SettingScope.Server, haltOnErrors: false);

        Assert.That(report.Problems, Is.Empty);
        Assert.That(StartupRegistryFixture.CountSetting.Value, Is.EqualTo(4));
        Assert.That(StartupRegistryFixture.FlagSetting.Value, Is.False);
    }

    [Test]
    public void UnknownFlag_IsCaseSensitive()
    {
        var report = StartupSettings.ConfigureAll(
            new[] { "--TEST-FIXTURE-COUNT=3" }, RegistrySettings(), SettingScope.Server, haltOnErrors: false);

        Assert.That(report.Problems, Has.Count.EqualTo(1));
        Assert.That(report.Problems[0].Kind, Is.EqualTo(ConfigurationProblemKind.UnknownFlag));
    }

    // ---- T4: undeclared environment names are listed, never problems ----

    [Test]
    public void UndeclaredEnv_IsListedNotProblem()
    {
        // Built by concatenation so the T10 env-var guard does not read it as a declared ROSLYNSENTINEL_* name.
        var undeclared = "ROSLYNSENTINEL" + "_TEST_UNDECLARED";
        StartupSettings.EnvironmentNames = () => new[] { undeclared, "TESTFIXTURE_COUNT", "PATH" };

        var report = StartupSettings.ConfigureAll(Array.Empty<string>(), RegistrySettings(), SettingScope.Server, haltOnErrors: true);

        Assert.That(report.UndeclaredEnv, Is.EqualTo(new[] { undeclared }));
        Assert.That(report.Problems, Is.Empty);
        Assert.That(report.Halted, Is.False);
    }

    // ---- T5: duplicate declarations ----

    [Test]
    public void DuplicateName_OverlappingScopes_Throws()
    {
        var ex = Assert.Throws<StartupSettingDeclarationException>(() =>
            StartupSettings.Discover(new[] { typeof(StartupOverlapFlagFirstFixture), typeof(StartupOverlapFlagSecondFixture) }));

        Assert.That(ex!.Message, Does.Contain("--test-fixture-dup"));
    }

    [Test]
    public void DuplicateEnvName_OverlappingScopes_Throws()
    {
        var ex = Assert.Throws<StartupSettingDeclarationException>(() =>
            StartupSettings.Discover(new[] { typeof(StartupOverlapEnvFirstFixture), typeof(StartupOverlapEnvSecondFixture) }));

        Assert.That(ex!.Message, Does.Contain("TESTFIXTURE_DUP"));
    }

    [Test]
    public void DuplicateName_DisjointScopes_Allowed()
    {
        var settings = Settings(typeof(StartupDisjointServerFixture), typeof(StartupDisjointPlanStepFixture));
        Assert.That(settings, Has.Count.EqualTo(2));

        var report = StartupSettings.ConfigureAll(new[] { "--test-fixture-shared=5" }, settings, SettingScope.PlanStepRunner, haltOnErrors: false);

        Assert.That(report.Problems, Is.Empty, "a flag declared in another scope is known, not unknown");
        Assert.That(StartupDisjointPlanStepFixture.SharedSetting.Value, Is.EqualTo(5));
    }

    // ---- T6: live settings and PostConfigure ----

    [Test]
    public void LiveSetting_RereadsEnv()
    {
        var env = new Dictionary<string, string?> { ["TESTFIXTURE_LIVE"] = "4" };
        StartupSettings.EnvironmentReader = name => env.GetValueOrDefault(name);
        StartupSettings.ConfigureAll(Array.Empty<string>(), Settings(typeof(StartupLiveFixture)), SettingScope.Server, haltOnErrors: false);
        var live = StartupLiveFixture.LiveSetting;

        Assert.That(live.Value, Is.EqualTo(4));

        env["TESTFIXTURE_LIVE"] = "9";
        Assert.That(live.Value, Is.EqualTo(9), "a Live value is read again on every access");

        env["TESTFIXTURE_LIVE"] = "bogus";
        Assert.That(live.Value, Is.EqualTo(1), "an invalid live value falls back to the default");
        Assert.That(live.Source, Is.EqualTo(SettingSource.Invalid));
    }

    [Test]
    public void PostConfigure_RunsAndReportsProblems()
    {
        StartupSettings.EnvironmentReader = name => name == "TESTFIXTURE_LOW" ? "20" : null;

        var report = StartupSettings.ConfigureAll(Array.Empty<string>(), Settings(typeof(StartupOrderedFixture)), SettingScope.Server, haltOnErrors: false);

        Assert.That(report.Problems, Has.Count.EqualTo(1));
        Assert.That(report.Problems[0].Kind, Is.EqualTo(ConfigurationProblemKind.PostConfigure));
        Assert.That(report.Problems[0].Expected, Is.EqualTo("low <= high"));
        Assert.That(StartupOrderedFixture.LowSetting.Value, Is.EqualTo(1), "a PostConfigure problem resets the whole group to defaults");
        Assert.That(StartupOrderedFixture.LowSetting.Source, Is.EqualTo(SettingSource.Default));
    }

    [Test]
    public void PostConfigure_ValidOrder_NoProblem()
    {
        StartupSettings.EnvironmentReader = name => name == "TESTFIXTURE_LOW" ? "3" : null;

        var report = StartupSettings.ConfigureAll(Array.Empty<string>(), Settings(typeof(StartupOrderedFixture)), SettingScope.Server, haltOnErrors: false);

        Assert.That(report.Problems, Is.Empty);
        Assert.That(StartupOrderedFixture.LowSetting.Value, Is.EqualTo(3));
    }

    // ---- T6b: halt latch and ThrowIfInvalid ----

    [TestCase("--test-fixture-count=0", true, true)]
    [TestCase("--test-fixture-count=0", false, false)]
    [TestCase("--test-fixture-count=3", true, false)]
    [TestCase("--test-fixture-count=3", false, false)]
    public void Halted_FollowsHaltOnErrors(string arg, bool haltOnErrors, bool expectedHalted)
    {
        var report = StartupSettings.ConfigureAll(new[] { arg }, RegistrySettings(), SettingScope.Server, haltOnErrors);

        Assert.That(report.Problems.Count > 0, Is.EqualTo(arg == "--test-fixture-count=0"));
        Assert.That(report.Halted, Is.EqualTo(expectedHalted));
        Assert.That(StartupSettings.LastReport, Is.SameAs(report));
    }

    [Test]
    public void ThrowIfInvalid_NamesSettings()
    {
        var report = StartupSettings.ConfigureAll(new[] { "--test-fixture-count=0" }, RegistrySettings(), SettingScope.Server, haltOnErrors: false);

        var ex = Assert.Throws<ConfigurationInvalidException>(() => StartupSettings.ThrowIfInvalid(report));

        Assert.That(ex.Problems[0].SettingName, Is.EqualTo("StartupRegistryFixture.Count"));
        Assert.That(ex.Message, Does.Contain("--test-fixture-count"));
    }

    [Test]
    public void ThrowIfInvalid_CleanReport_DoesNotThrow()
    {
        var report = StartupSettings.ConfigureAll(Array.Empty<string>(), RegistrySettings(), SettingScope.Server, haltOnErrors: false);

        Assert.DoesNotThrow(() => StartupSettings.ThrowIfInvalid(report));
    }
}
