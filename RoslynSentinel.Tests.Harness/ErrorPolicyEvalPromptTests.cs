using System.IO;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Unit-level checks of the coding-agent prompt composition used by the ModelEval harness. Never calls a model.
/// Uses the explicit-argument overloads so the static <see cref="ErrorPolicyOptions"/> state is never mutated.
/// </summary>
[TestFixture]
public sealed class ErrorPolicyEvalPromptTests
{
    private const string ErrorHandlingHeading = "## Error handling";

    [Test]
    public void ComposePrompt_ModeOffNoFile_YieldsTodaysCodingAgentPrompt()
    {
        string result = ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, ErrorPolicyMode.Off, null);

        Assert.That(result, Is.EqualTo(AgentSystemPrompts.CodingAgent));
        Assert.That(result, Does.Not.Contain(ErrorHandlingHeading));
    }

    [Test]
    public void ComposePrompt_EnvironmentDefault_MatchesCodingAgentWhenNoPolicyConfigured()
    {
        if (ErrorPolicyOptions.Mode != ErrorPolicyMode.Off || ErrorPolicyOptions.PolicyFilePath is not null)
        {
            Assert.Ignore("an error policy is configured in this process");
        }

        string result = ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent);

        Assert.That(result, Is.EqualTo(AgentSystemPrompts.CodingAgent));
    }

    [Test]
    public void ComposePrompt_PointerWithPolicyFile_StartsWithCodingAgentAndAddsBlock()
    {
        string policyPath = Path.Combine(Path.GetTempPath(), "errorpolicy-" + Path.GetRandomFileName() + ".md");
        try
        {
            File.WriteAllText(policyPath,
                "### FileNotFound.Lookup\n" +
                "Use the lookup tool before retrying.\n");

            string result = ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, ErrorPolicyMode.Pointer, policyPath);

            Assert.That(result, Does.StartWith(AgentSystemPrompts.CodingAgent.TrimEnd()));
            Assert.That(result, Does.Contain("### Error handling: FileNotFound.Lookup"));
        }
        finally
        {
            if (File.Exists(policyPath))
            {
                File.Delete(policyPath);
            }
        }
    }
}
