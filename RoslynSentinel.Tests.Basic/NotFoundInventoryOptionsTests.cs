using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// NotFoundInventoryOptions is static (process-wide state) and reads a process environment variable, so this fixture must not run in parallel with other tests.
[NonParallelizable]
public class NotFoundInventoryOptionsTests
{
    private const string Env = NotFoundInventoryOptions.EnvironmentVariable;

    private string? _savedEnv;

    [SetUp]
    public void SaveAndClearEnv()
    {
        _savedEnv = Environment.GetEnvironmentVariable(Env);
        Environment.SetEnvironmentVariable(Env, null);
        NotFoundInventoryOptions.Configure([]);
    }

    [TearDown]
    public void RestoreEnvAndResetOptions()
    {
        Environment.SetEnvironmentVariable(Env, null);
        NotFoundInventoryOptions.Configure([]);
        Environment.SetEnvironmentVariable(Env, _savedEnv);
    }

    [Test]
    public void Configure_NoArgsOrEnv_IsOn()
    {
        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [TestCase("1")]
    [TestCase("true")]
    [TestCase("TRUE")]
    [TestCase(" True ")]
    [TestCase("on")]
    [TestCase("yes")]
    public void Configure_EnvVarOnValue_TurnsItOn(string value)
    {
        Environment.SetEnvironmentVariable(Env, value);

        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [TestCase("0")]
    [TestCase("false")]
    public void Configure_EnvVarOffValue_TurnsItOff(string value)
    {
        Environment.SetEnvironmentVariable(Env, value);

        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [TestCase("2")]
    [TestCase("bogus")]
    public void Configure_EnvVarInvalidValue_KeepsDefaultOnAndReportsProblem(string value)
    {
        Environment.SetEnvironmentVariable(Env, value);

        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);

        var problems = StartupSettings.ConfigureGroup(typeof(NotFoundInventoryOptions), []);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0].Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
    }

    [Test]
    public void Configure_BareFlag_TurnsItOn()
    {
        NotFoundInventoryOptions.Configure(["--notfound-inventory"]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [Test]
    public void Configure_FlagValueOverridesEnvVar()
    {
        Environment.SetEnvironmentVariable(Env, "1");

        NotFoundInventoryOptions.Configure(["--notfound-inventory=false"]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [Test]
    public void Configure_CalledAgainWithoutTheEnvVar_TurnsItOnAgain()
    {
        Environment.SetEnvironmentVariable(Env, "0");
        NotFoundInventoryOptions.Configure([]);
        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);

        Environment.SetEnvironmentVariable(Env, null);
        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [Test]
    public void Configure_FlagFalseValue_TurnsItOffEvenWithNoEnvVar()
    {
        NotFoundInventoryOptions.Configure(["--notfound-inventory=false"]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [Test]
    public void OverrideForCurrentContext_WinsUntilDisposed()
    {
        using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
        {
            Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
        }

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [TestCase("off", true, "off+inventory")]
    [TestCase("off", false, "off")]
    [TestCase("inline", true, "inline+inventory")]
    public void StampMode_AppendsSuffixOnlyWhenEnabled(string mode, bool enabled, string expected)
    {
        Assert.That(NotFoundInventoryOptions.StampMode(mode, enabled), Is.EqualTo(expected));
    }
}
