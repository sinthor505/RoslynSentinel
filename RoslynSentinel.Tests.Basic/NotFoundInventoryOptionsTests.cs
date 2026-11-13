using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// TODO symbol member inventory untested (plan_error_policy_pointer arm I never reached a container); decide default after an eval that exercises containingType/filePath misses
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
    public void Configure_NoArgsOrEnv_IsOff()
    {
        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [TestCase("1")]
    [TestCase("true")]
    [TestCase("TRUE")]
    [TestCase(" True ")]
    public void Configure_EnvVarOnValue_TurnsItOn(string value)
    {
        Environment.SetEnvironmentVariable(Env, value);

        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
    }

    [TestCase("0")]
    [TestCase("false")]
    [TestCase("on")]
    [TestCase("yes")]
    [TestCase("2")]
    [TestCase("")]
    [TestCase("bogus")]
    public void Configure_EnvVarOtherValue_LeavesItOff(string value)
    {
        Environment.SetEnvironmentVariable(Env, value);

        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
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
    public void Configure_CalledAgainWithoutTheEnvVar_TurnsItOffAgain()
    {
        Environment.SetEnvironmentVariable(Env, "1");
        NotFoundInventoryOptions.Configure([]);
        Assert.That(NotFoundInventoryOptions.Enabled, Is.True);

        Environment.SetEnvironmentVariable(Env, null);
        NotFoundInventoryOptions.Configure([]);

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [Test]
    public void OverrideForCurrentContext_WinsUntilDisposed()
    {
        using (NotFoundInventoryOptions.OverrideForCurrentContext(true))
        {
            Assert.That(NotFoundInventoryOptions.Enabled, Is.True);
        }

        Assert.That(NotFoundInventoryOptions.Enabled, Is.False);
    }

    [TestCase("off", true, "off+inventory")]
    [TestCase("off", false, "off")]
    [TestCase("inline", true, "inline+inventory")]
    public void StampMode_AppendsSuffixOnlyWhenEnabled(string mode, bool enabled, string expected)
    {
        Assert.That(NotFoundInventoryOptions.StampMode(mode, enabled), Is.EqualTo(expected));
    }
}
