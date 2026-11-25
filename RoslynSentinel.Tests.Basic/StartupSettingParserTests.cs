using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Parser rules (plan T1, T2). StartupSetting<T>.Read is stateless and takes the environment reader as a
// parameter, so these tests touch no StartupSettings static state and need no [NonParallelizable].

public enum StartupFixtureMode
{
    None,
    SingleAgent,
    Multi_Step,
}

[StartupSettingGroup("Parser fixture")]
public static class StartupParserFixture
{
    public static readonly StartupSetting<bool> BoolSetting = StartupSetting.Bool("--test-fixture-bool", "TESTFIXTURE_BOOL", false, "Parser fixture bool.", SettingScope.Server);
    public static readonly StartupSetting<int> IntSetting = StartupSetting.Int("--test-fixture-int", "TESTFIXTURE_INT", 5, "Parser fixture int.", SettingScope.Server);
    public static readonly StartupSetting<int> MinSetting = StartupSetting.Int("--test-fixture-min", null, 3, "Parser fixture int with a minimum.", SettingScope.Server, minValue: 1);
    public static readonly StartupSetting<int?> NullableIntSetting = StartupSetting.NullableInt("--test-fixture-nullable", null, "Parser fixture nullable int.", SettingScope.Server);
    public static readonly StartupSetting<StartupFixtureMode> ModeSetting = StartupSetting.Enum("--test-fixture-mode", "TESTFIXTURE_MODE", StartupFixtureMode.None, "Parser fixture enum.", SettingScope.Server);
}

public class StartupSettingParserTests
{
    private static string? NoEnv(string name) => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    // ---- T1: valid forms ----

    [Test]
    public void ValidForms_BareBoolFlag_IsTrue()
    {
        var read = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool" }), NoEnv);

        Assert.That(read.Value, Is.True);
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(read.Problem, Is.Null);
    }

    [Test]
    public void ValidForms_EqualsForm_ReadsValue()
    {
        var read = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int=7" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(7));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
    }

    [Test]
    public void ValidForms_SpaceForm_ReadsValue()
    {
        var read = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int", "7" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(7));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
    }

    [Test]
    public void ValidForms_FlagBeatsEnv()
    {
        var env = Env(("TESTFIXTURE_INT", "3"));

        var fromFlag = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int=9" }), env);
        var fromEnv = StartupParserFixture.IntSetting.Read(StartupArgs.Empty, env);

        Assert.That(fromFlag.Value, Is.EqualTo(9));
        Assert.That(fromFlag.Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(fromEnv.Value, Is.EqualTo(3));
        Assert.That(fromEnv.Source, Is.EqualTo(SettingSource.Env));
    }

    [Test]
    public void ValidForms_RepeatedFlag_LastWins()
    {
        var read = StartupParserFixture.IntSetting.Read(
            StartupArgs.Parse(new[] { "--test-fixture-int=1", "--test-fixture-int", "2" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(2));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
    }

    [TestCase("true", true)]
    [TestCase("1", true)]
    [TestCase("on", true)]
    [TestCase("yes", true)]
    [TestCase("TRUE", true)]
    [TestCase("On", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("off", false)]
    [TestCase("no", false)]
    [TestCase("NO", false)]
    public void ValidForms_BoolLiterals_CaseInsensitive(string raw, bool expected)
    {
        var read = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool=" + raw }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(expected));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(read.Problem, Is.Null);
    }

    [TestCase("single-agent", StartupFixtureMode.SingleAgent)]
    [TestCase("SINGLEAGENT", StartupFixtureMode.SingleAgent)]
    [TestCase("single_agent", StartupFixtureMode.SingleAgent)]
    [TestCase("multi-step", StartupFixtureMode.Multi_Step)]
    [TestCase("MultiStep", StartupFixtureMode.Multi_Step)]
    public void ValidForms_EnumNames_IgnoreCaseDashAndUnderscore(string raw, StartupFixtureMode expected)
    {
        var read = StartupParserFixture.ModeSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-mode=" + raw }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(expected));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Flag));
    }

    [Test]
    public void ValidForms_EmptyValue_IsUnset()
    {
        var emptyFlag = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int=" }), NoEnv);
        var blankFlag = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int=   " }), NoEnv);
        var blankEnv = StartupParserFixture.IntSetting.Read(StartupArgs.Empty, Env(("TESTFIXTURE_INT", "  ")));

        Assert.That(emptyFlag.Value, Is.EqualTo(5));
        Assert.That(emptyFlag.Source, Is.EqualTo(SettingSource.Default));
        Assert.That(emptyFlag.Problem, Is.Null);
        Assert.That(blankFlag.Source, Is.EqualTo(SettingSource.Default));
        Assert.That(blankFlag.Problem, Is.Null);
        Assert.That(blankEnv.Value, Is.EqualTo(5));
        Assert.That(blankEnv.Source, Is.EqualTo(SettingSource.Default));
        Assert.That(blankEnv.Problem, Is.Null);
    }

    [Test]
    public void ValidForms_EmptyFlag_FallsThroughToEnv()
    {
        var read = StartupParserFixture.IntSetting.Read(
            StartupArgs.Parse(new[] { "--test-fixture-int=" }), Env(("TESTFIXTURE_INT", "4")));

        Assert.That(read.Value, Is.EqualTo(4));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Env));
    }

    [Test]
    public void ValidForms_BoolNextToken_ConsumedOnlyWhenBoolLiteral()
    {
        var literal = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool", "no" }), NoEnv);
        var nextFlag = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool", "--test-fixture-int=2" }), NoEnv);
        var notLiteral = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool", "maybe" }), NoEnv);

        Assert.That(literal.Value, Is.False, "the bool literal after a bare bool flag is its value");
        Assert.That(literal.Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(nextFlag.Value, Is.True, "a following --flag is never consumed as the value");
        Assert.That(notLiteral.Value, Is.True, "a non-literal token is not consumed, so the bare flag means true");
        Assert.That(notLiteral.Problem, Is.Null);
    }

    [Test]
    public void ValidForms_MinValue_AcceptsBoundaryAndAbove()
    {
        var atMinimum = StartupParserFixture.MinSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-min=1" }), NoEnv);
        var above = StartupParserFixture.MinSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-min=10" }), NoEnv);

        Assert.That(atMinimum.Value, Is.EqualTo(1));
        Assert.That(atMinimum.Source, Is.EqualTo(SettingSource.Flag));
        Assert.That(above.Value, Is.EqualTo(10));
        Assert.That(above.Source, Is.EqualTo(SettingSource.Flag));
    }

    [Test]
    public void ValidForms_TrimmedValues()
    {
        var flag = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int= 12 " }), NoEnv);
        var env = StartupParserFixture.IntSetting.Read(StartupArgs.Empty, Env(("TESTFIXTURE_INT", " 8 ")));

        Assert.That(flag.Value, Is.EqualTo(12));
        Assert.That(env.Value, Is.EqualTo(8));
        Assert.That(env.Source, Is.EqualTo(SettingSource.Env));
    }

    [Test]
    public void ValidForms_NullableIntUnset_IsNull()
    {
        var read = StartupParserFixture.NullableIntSetting.Read(StartupArgs.Empty, NoEnv);

        Assert.That(read.Value, Is.Null);
        Assert.That(read.Source, Is.EqualTo(SettingSource.Default));
        Assert.That(read.Problem, Is.Null);
    }

    // ---- T2: invalid values keep the default and report a problem ----

    [Test]
    public void InvalidValue_MissingValue_KeepsDefault()
    {
        var atEnd = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int" }), NoEnv);
        var beforeFlag = StartupParserFixture.IntSetting.Read(
            StartupArgs.Parse(new[] { "--test-fixture-int", "--test-fixture-bool" }), NoEnv);

        foreach (var read in new[] { atEnd, beforeFlag })
        {
            Assert.That(read.Value, Is.EqualTo(5));
            Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
            Assert.That(read.Problem, Is.Not.Null);
            Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.MissingValue));
            Assert.That(read.Problem.Flag, Is.EqualTo("--test-fixture-int"));
            Assert.That(read.Problem.RawValue, Is.Null, "a bare value flag has no raw text");
        }
    }

    [Test]
    public void InvalidValue_Unparseable_KeepsDefaultWithRawValue()
    {
        var read = StartupParserFixture.IntSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-int=abc" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(5));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem, Is.Not.Null);
        Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
        Assert.That(read.Problem.RawValue, Is.EqualTo("abc"));
        Assert.That(read.Problem.Expected, Is.EqualTo("integer"));
    }

    [Test]
    public void InvalidValue_BelowMinimum_KeepsDefault()
    {
        var read = StartupParserFixture.MinSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-min=0" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(3));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem, Is.Not.Null);
        Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
        Assert.That(read.Problem.RawValue, Is.EqualTo("0"));
        Assert.That(read.Problem.Expected, Is.EqualTo("integer >= 1"));
    }

    [Test]
    public void InvalidValue_UnknownBoolLiteral_KeepsDefault()
    {
        var read = StartupParserFixture.BoolSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-bool=maybe" }), NoEnv);

        Assert.That(read.Value, Is.False);
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
        Assert.That(read.Problem.RawValue, Is.EqualTo("maybe"));
    }

    [Test]
    public void InvalidValue_EnvUnparseable_KeepsDefaultWithEnvName()
    {
        var read = StartupParserFixture.IntSetting.Read(StartupArgs.Empty, Env(("TESTFIXTURE_INT", "xyz")));

        Assert.That(read.Value, Is.EqualTo(5));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
        Assert.That(read.Problem.EnvName, Is.EqualTo("TESTFIXTURE_INT"));
        Assert.That(read.Problem.RawValue, Is.EqualTo("xyz"));
    }

    [Test]
    public void InvalidValue_UnknownEnumName_KeepsDefault()
    {
        var read = StartupParserFixture.ModeSetting.Read(StartupArgs.Parse(new[] { "--test-fixture-mode=bogus" }), NoEnv);

        Assert.That(read.Value, Is.EqualTo(StartupFixtureMode.None));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem!.Kind, Is.EqualTo(ConfigurationProblemKind.InvalidValue));
        Assert.That(read.Problem.RawValue, Is.EqualTo("bogus"));
    }
}
