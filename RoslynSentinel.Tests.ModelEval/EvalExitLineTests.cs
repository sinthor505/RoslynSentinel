using System;
using System.Text;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.LiveModel;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public sealed class EvalExitLineTests
{
    [Test]
    public void Apply_Enabled_AppendsTheLineAtTheEnd()
    {
        string result = EvalExitLine.Apply(AgentSystemPrompts.CodingAgent, true);

        Assert.That(result, Does.EndWith(EvalExitLine.Line));
        Assert.That(result, Does.StartWith(AgentSystemPrompts.CodingAgent.TrimEnd()));
        Assert.That(result.Split(EvalExitLine.Line).Length - 1, Is.EqualTo(1));
    }

    [Test]
    public void Apply_Disabled_IsByteIdentical()
    {
        string result = EvalExitLine.Apply(AgentSystemPrompts.CodingAgent, false);

        Assert.That(Encoding.UTF8.GetBytes(result), Is.EqualTo(Encoding.UTF8.GetBytes(AgentSystemPrompts.CodingAgent)));
    }

    [Test]
    public void ComposeScenarioPrompt_SwitchOffByDefault_MatchesErrorPolicyWiringComposePrompt()
    {
        // Mutates process-wide state (the environment variable); the previous value is restored in finally.
        var previous = Environment.GetEnvironmentVariable(EvalExitLine.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, null);

            Assert.That(
                EvalExitLine.ComposeScenarioPrompt(AgentSystemPrompts.CodingAgent),
                Is.EqualTo(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, previous);
        }
    }

    [Test]
    public void ComposeScenarioPrompt_SwitchOn_EndsWithTheLine()
    {
        // Mutates process-wide state (the environment variable); the previous value is restored in finally.
        var previous = Environment.GetEnvironmentVariable(EvalExitLine.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, "1");
            Assert.That(
                EvalExitLine.ComposeScenarioPrompt(AgentSystemPrompts.CodingAgent),
                Does.EndWith(EvalExitLine.Line));

            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, "0");
            Assert.That(
                EvalExitLine.ComposeScenarioPrompt(AgentSystemPrompts.CodingAgent),
                Does.Not.Contain(EvalExitLine.Line));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, previous);
        }
    }

    [Test]
    public void StampMode_AppendsSuffixOnlyWhenEnabled()
    {
        Assert.That(EvalExitLine.StampMode("off", true), Is.EqualTo("off+exitline"));
        Assert.That(EvalExitLine.StampMode("off", false), Is.EqualTo("off"));
    }

    [Test]
    public void BuildProcessStamp_ExitLineSwitchOn_SuffixesModeAndOffLeavesItAlone()
    {
        // Mutates process-wide state (the environment variable); the previous value is restored in finally.
        var previous = Environment.GetEnvironmentVariable(EvalExitLine.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, "1");
            var on = ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false));
            Assert.That(on, Is.Not.Null);
            Assert.That(on.Value.ErrorPolicyMode, Does.Contain(EvalExitLine.StampSuffix));

            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, null);
            var off = ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false));
            Assert.That(off, Is.Not.Null);
            Assert.That(off.Value.ErrorPolicyMode, Does.Not.Contain(EvalExitLine.StampSuffix));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, previous);
        }

    }

    // TODO plan_error_policy_pointer arm I: temporary A/B switch; resolve at plan close
    [Test]
    public void BuildProcessStamp_InventorySwitchOn_AddsTheInventorySuffixAfterTheExitLineOne()
    {
        // Mutates process-wide state (the environment variable); the previous value is restored in finally. The inventory switch is an AsyncLocal override.
        var previous = Environment.GetEnvironmentVariable(EvalExitLine.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, "1");
            using (NotFoundInventoryOptions.OverrideForCurrentContext(true))
            {
                Assert.That(ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false))!.Value.ErrorPolicyMode, Does.EndWith("+exitline+inventory"));
            }

            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, null);
            using (NotFoundInventoryOptions.OverrideForCurrentContext(true))
            {
                var on = ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false))!.Value.ErrorPolicyMode;
                Assert.That(on, Does.EndWith(NotFoundInventoryOptions.StampSuffix));
                Assert.That(on, Does.Not.Contain(EvalExitLine.StampSuffix));
            }

            using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
            {
                Assert.That(ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false))!.Value.ErrorPolicyMode, Does.Not.Contain("inventory"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalExitLine.EnvironmentVariable, previous);
        }
    }
}
