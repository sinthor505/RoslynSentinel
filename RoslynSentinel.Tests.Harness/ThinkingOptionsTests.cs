using System.Collections.Generic;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ThinkingOptions"/> parsing from the job environment and the trigger decision matrix. FromEnvironment is
/// driven by a dictionary, never the process environment. No live model.
/// </summary>
[TestFixture]
public class ThinkingOptionsTests
{
    [Test]
    public void FromEnvironment_Unset_ReturnsOff()
    {
        var options = ThinkingOptions.FromEnvironment(Reader(new Dictionary<string, string>()));

        Assert.That(options.Mode, Is.EqualTo(ThinkingMode.Off));
        Assert.That(options.Triggers, Is.EqualTo(ThinkingTriggers.Error));
        Assert.That(options.Effort, Is.EqualTo(ThinkingOptions.DefaultEffort));
        Assert.That(options.ReasoningBudget, Is.Null);
    }

    [Test]
    public void FromEnvironment_AllSet_ParsesEach()
    {
        var env = new Dictionary<string, string>
        {
            [ThinkingOptions.ModeVariable] = "triggered",
            [ThinkingOptions.TriggersVariable] = "Error, latchRefusal , FIRSTTURN",
            [ThinkingOptions.EffortVariable] = "medium",
            [ThinkingOptions.BudgetVariable] = "2000",
        };

        var options = ThinkingOptions.FromEnvironment(Reader(env));

        Assert.That(options.Mode, Is.EqualTo(ThinkingMode.Triggered));
        Assert.That(options.Triggers, Is.EqualTo(ThinkingTriggers.Error | ThinkingTriggers.LatchRefusal | ThinkingTriggers.FirstTurn));
        Assert.That(options.Effort, Is.EqualTo("medium"));
        Assert.That(options.ReasoningBudget, Is.EqualTo(2000));
    }

    [TestCase(ThinkingOptions.ModeVariable, "sometimes")]
    [TestCase(ThinkingOptions.TriggersVariable, "bogus")]
    [TestCase(ThinkingOptions.TriggersVariable, "1")]
    [TestCase(ThinkingOptions.TriggersVariable, ",")]
    [TestCase(ThinkingOptions.TriggersVariable, " , ")]
    [TestCase(ThinkingOptions.BudgetVariable, "0")]
    [TestCase(ThinkingOptions.BudgetVariable, "-5")]
    [TestCase(ThinkingOptions.BudgetVariable, "abc")]
    public void FromEnvironment_InvalidValue_Throws(string variable, string value)
    {
        var env = new Dictionary<string, string> { [variable] = value };

        var ex = Assert.Throws<ConfigurationInvalidException>(() => ThinkingOptions.FromEnvironment(Reader(env)));

        Assert.That(ex.Problems[0].EnvName, Is.EqualTo(variable));
    }

    [Test]
    public void FromEnvironment_EmptyStringIsUnset()
    {
        var env = new Dictionary<string, string>
        {
            [ThinkingOptions.ModeVariable] = "",
            [ThinkingOptions.TriggersVariable] = "",
            [ThinkingOptions.EffortVariable] = "",
            [ThinkingOptions.BudgetVariable] = "",
        };

        var options = ThinkingOptions.FromEnvironment(Reader(env));

        Assert.That(options.Mode, Is.EqualTo(ThinkingMode.Off));
        Assert.That(options.Triggers, Is.EqualTo(ThinkingTriggers.Error));
        Assert.That(options.Effort, Is.EqualTo(ThinkingOptions.DefaultEffort));
        Assert.That(options.ReasoningBudget, Is.Null);
    }

    [TestCase("   ")]
    [TestCase("")]
    public void FromEnvironment_BlankEffort_IsUnsetAndKeepsDefault(string value)
    {
        var env = new Dictionary<string, string> { [ThinkingOptions.EffortVariable] = value };

        var options = ThinkingOptions.FromEnvironment(Reader(env));

        Assert.That(options.Effort, Is.EqualTo("low"));
    }

    [Test]
    public void FromEnvironment_CustomEffort_PassesThrough()
    {
        var custom = ThinkingOptions.FromEnvironment(Reader(new Dictionary<string, string> { [ThinkingOptions.EffortVariable] = "turbo" }));
        var known = ThinkingOptions.FromEnvironment(Reader(new Dictionary<string, string> { [ThinkingOptions.EffortVariable] = "low" }));

        Assert.That(custom.Effort, Is.EqualTo("turbo"));
        Assert.That(custom.IsKnownEffort, Is.False);
        Assert.That(known.IsKnownEffort, Is.True);
    }

    [TestCase(ThinkingMode.Off, ThinkingTriggers.Error, ThinkingTriggers.None, ThinkingTriggers.None, false)]
    [TestCase(ThinkingMode.Off, ThinkingTriggers.Error, ThinkingTriggers.Error, ThinkingTriggers.None, false)]
    [TestCase(ThinkingMode.Off, ThinkingTriggers.Error, ThinkingTriggers.Error | ThinkingTriggers.LatchRefusal | ThinkingTriggers.FirstTurn, ThinkingTriggers.None, false)]
    [TestCase(ThinkingMode.Always, ThinkingTriggers.Error, ThinkingTriggers.None, ThinkingTriggers.None, true)]
    [TestCase(ThinkingMode.Always, ThinkingTriggers.Error, ThinkingTriggers.Error, ThinkingTriggers.None, true)]
    [TestCase(ThinkingMode.Triggered, ThinkingTriggers.Error, ThinkingTriggers.Error, ThinkingTriggers.Error, true)]
    [TestCase(ThinkingMode.Triggered, ThinkingTriggers.Error, ThinkingTriggers.LatchRefusal, ThinkingTriggers.None, false)]
    [TestCase(ThinkingMode.Triggered, ThinkingTriggers.Error, ThinkingTriggers.None, ThinkingTriggers.None, false)]
    [TestCase(ThinkingMode.Triggered, ThinkingTriggers.Error | ThinkingTriggers.FirstTurn, ThinkingTriggers.FirstTurn, ThinkingTriggers.FirstTurn, true)]
    [TestCase(ThinkingMode.Triggered, ThinkingTriggers.LatchRefusal | ThinkingTriggers.FirstTurn, ThinkingTriggers.Error | ThinkingTriggers.LatchRefusal | ThinkingTriggers.FirstTurn, ThinkingTriggers.LatchRefusal | ThinkingTriggers.FirstTurn, true)]
    public void Decide_Matrix(ThinkingMode mode, ThinkingTriggers triggers, ThinkingTriggers signals, ThinkingTriggers expectedFired, bool expectedRequested)
    {
        var options = new ThinkingOptions(mode, triggers, "low", null);

        var decision = options.Decide(signals);

        Assert.That(decision.FiredTriggers, Is.EqualTo(expectedFired));
        Assert.That(decision.Requested, Is.EqualTo(expectedRequested));
    }

    [Test]
    public void VariableNames_ArePinned()
    {
        Assert.That(ThinkingOptions.ModeVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_THINKING"));
        Assert.That(ThinkingOptions.TriggersVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS"));
        Assert.That(ThinkingOptions.EffortVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT"));
        Assert.That(ThinkingOptions.BudgetVariable, Is.EqualTo("ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET"));
    }

    private static Func<string, string?> Reader(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out var value) ? value : null;
}
