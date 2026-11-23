using System;
using System.IO;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free checks of EvalContractOptions: env-var resolution, the composed system prompt, the rendered
/// task placement and the contract stamp. Never calls a model.
/// </summary>
[TestFixture]
// Current_ReadsTheEnvironmentOnEveryCall sets the process environment variables, so the fixture must not run in parallel.
// The test restores both variables in finally.
[NonParallelizable]
public sealed class EvalContractOptionsTests
{
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static ScenarioTask SampleTask() => new(
        Goal: "Fix the null check in Parser.Read",
        Files: "RoslynSentinel.Engines/Parser.cs",
        IfFails: "leave the file unchanged and list what you checked",
        DoneWhen: "RunTest passes for ParserTests");

    [Test]
    public void Resolve_Unset_IsLegacyAndSystem()
    {
        var resolved = EvalContractOptions.Resolve(null, null);

        Assert.That(resolved.Variant, Is.EqualTo(ContractVariant.Legacy));
        Assert.That(resolved.Placement, Is.EqualTo(EndingsPlacement.System));
    }

    [Test]
    public void Resolve_StandingUser_ReturnsStandingSystemAndUser()
    {
        var resolved = EvalContractOptions.Resolve("standing", "user");

        Assert.That(resolved.Variant, Is.EqualTo(ContractVariant.Standing));
        Assert.That(resolved.Placement, Is.EqualTo(EndingsPlacement.SystemAndUser));
    }

    [Test]
    public void Resolve_UserEndingsWithLegacy_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => EvalContractOptions.Resolve(null, "user"));

        Assert.That(ex!.Message, Does.Contain(EvalContractOptions.EndingsEnvVar));
        Assert.That(ex.Message, Does.Contain(EvalContractOptions.ContractEnvVar));
    }

    [Test]
    public void Resolve_UnknownContract_ThrowsNamingTheVariable()
    {
        // Resolve takes values, not variable names; the variable name comes from the "{ContractEnvVar}: " prefix it adds.
        var ex = Assert.Throws<ArgumentException>(() => EvalContractOptions.Resolve("bogus", null));

        Assert.That(ex!.Message, Does.StartWith(EvalContractOptions.ContractEnvVar + ":"));
        Assert.That(ex.Message, Does.Contain("bogus"));
    }

    [Test]
    public void ComposeSystemPrompt_LegacyOffNoFile_EqualsCodingAgent()
    {
        string prompt = EvalContractOptions.ComposeSystemPrompt(ContractVariant.Legacy, ErrorPolicyMode.Off, null);

        Assert.That(prompt, Is.EqualTo(AgentSystemPrompts.CodingAgent));
    }

    [Test]
    public void ComposeSystemPrompt_StandingOffNoFile_EqualsContract()
    {
        string prompt = EvalContractOptions.ComposeSystemPrompt(ContractVariant.Standing, ErrorPolicyMode.Off, null);

        Assert.That(prompt, Is.EqualTo(AgentSystemPrompts.CodingAgentContract));
    }

    [Test]
    public void ComposeSystemPrompt_StandingWithPolicyFile_StartsWithContractAndAddsErrorHandlingBlock()
    {
        // Explicit-argument overload: the ErrorPolicyOptions static state is not touched.
        string policyPath = Path.Combine(
            Path.GetTempPath(), "EvalContractOptionsTests_" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            File.WriteAllText(policyPath, "### FileNotFound.Edit\nIf the file is missing, list the directory before retrying.\n");

            string prompt = EvalContractOptions.ComposeSystemPrompt(ContractVariant.Standing, ErrorPolicyMode.Pointer, policyPath);

            Assert.That(prompt, Does.StartWith(AgentSystemPrompts.CodingAgentContract.TrimEnd()));
            Assert.That(prompt, Does.Contain("\n\n## Error handling\n\n"));
            Assert.That(prompt, Does.Contain("list the directory before retrying"));
        }
        finally
        {
            if (File.Exists(policyPath))
            {
                File.Delete(policyPath);
            }
        }
    }

    [Test]
    public void Render_ExplicitPlacement_RepeatsEndingsOnlyForSystemAndUser()
    {
        ScenarioTask task = SampleTask();
        string endings = Lf(AgentSystemPrompts.CodingAgentContractEndings).Trim();

        string system = EvalContractOptions.Render(task, EndingsPlacement.System);
        string systemAndUser = EvalContractOptions.Render(task, EndingsPlacement.SystemAndUser);

        Assert.That(system, Is.EqualTo(task.Render(repeatEndings: false)));
        Assert.That(system, Does.Not.Contain(endings));
        Assert.That(systemAndUser, Is.EqualTo(task.Render(repeatEndings: true)));
        Assert.That(systemAndUser, Does.EndWith("\n\n" + endings));
    }

    [Test]
    public void BuildContractStamp_Legacy_HashesCodingAgentAndNamesSystem()
    {
        var stamp = EvalContractOptions.BuildContractStamp(ContractVariant.Legacy, EndingsPlacement.System);

        Assert.That(stamp.Hash, Is.EqualTo(WorkflowContract.Hash(AgentSystemPrompts.CodingAgent)));
        Assert.That(stamp.Placement, Is.EqualTo("system"));
    }

    [Test]
    public void BuildContractStamp_StandingUser_HashesContractAndNamesSystemPlusUser()
    {
        var stamp = EvalContractOptions.BuildContractStamp(ContractVariant.Standing, EndingsPlacement.SystemAndUser);

        Assert.That(stamp.Hash, Is.EqualTo(WorkflowContract.Hash(AgentSystemPrompts.CodingAgentContract)));
        Assert.That(stamp.Placement, Is.EqualTo("system+user"));
    }

    [Test]
    public void Current_ReadsTheEnvironmentOnEveryCall()
    {
        string? originalContract = Environment.GetEnvironmentVariable(EvalContractOptions.ContractEnvVar);
        string? originalEndings = Environment.GetEnvironmentVariable(EvalContractOptions.EndingsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EvalContractOptions.ContractEnvVar, "standing");
            Environment.SetEnvironmentVariable(EvalContractOptions.EndingsEnvVar, "user");
            var first = EvalContractOptions.Current();

            Assert.That(first.Variant, Is.EqualTo(ContractVariant.Standing));
            Assert.That(first.Placement, Is.EqualTo(EndingsPlacement.SystemAndUser));

            Environment.SetEnvironmentVariable(EvalContractOptions.ContractEnvVar, null);
            Environment.SetEnvironmentVariable(EvalContractOptions.EndingsEnvVar, null);
            var second = EvalContractOptions.Current();

            Assert.That(second.Variant, Is.EqualTo(ContractVariant.Legacy));
            Assert.That(second.Placement, Is.EqualTo(EndingsPlacement.System));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalContractOptions.ContractEnvVar, originalContract);
            Environment.SetEnvironmentVariable(EvalContractOptions.EndingsEnvVar, originalEndings);
        }
    }
}
