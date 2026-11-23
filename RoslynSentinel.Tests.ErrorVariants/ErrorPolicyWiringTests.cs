using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.Basic;

public class ErrorPolicyWiringTests
{
    private const string OneEntryPolicy = "### FileNotFound.Edit\nUse the edit guidance.\n";

    [Test]
    public void ComposePrompt_OffNoFile_ReturnsPromptUnchanged()
    {
        Assert.That(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, ErrorPolicyMode.Off, null), Is.EqualTo(AgentSystemPrompts.CodingAgent));
        Assert.That(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, ErrorPolicyMode.Off, "   "), Is.EqualTo(AgentSystemPrompts.CodingAgent));
    }

    [Test]
    public void ComposePrompt_PointerNoFile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, ErrorPolicyMode.Pointer, null));

        Assert.That(ex!.Message, Does.Contain("--error-policy-file"));
    }

    [TestCase(ErrorPolicyMode.Off)]
    [TestCase(ErrorPolicyMode.Pointer)]
    [TestCase(ErrorPolicyMode.Inline)]
    public void ComposePrompt_FileSetAnyMode_AppendsErrorHandlingBlock(ErrorPolicyMode mode)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, OneEntryPolicy);

            string result = ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent, mode, path);

            Assert.That(result, Does.StartWith(AgentSystemPrompts.CodingAgent.TrimEnd()));
            Assert.That(result, Does.Contain("## Error handling"));
            Assert.That(result, Does.Contain("### Error handling: FileNotFound.Edit"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ComposePrompt_ContractOffNoFile_ReturnsContractUnchanged()
    {
        Assert.That(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgentContract, ErrorPolicyMode.Off, null), Is.EqualTo(AgentSystemPrompts.CodingAgentContract));
        Assert.That(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgentContract, ErrorPolicyMode.Off, "   "), Is.EqualTo(AgentSystemPrompts.CodingAgentContract));
    }

    [TestCase(ErrorPolicyMode.Off)]
    [TestCase(ErrorPolicyMode.Pointer)]
    [TestCase(ErrorPolicyMode.Inline)]
    public void ComposePrompt_ContractFileSetAnyMode_AppendsErrorHandlingBlock(ErrorPolicyMode mode)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, OneEntryPolicy);

            string result = ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgentContract, mode, path);

            Assert.That(result, Does.StartWith(AgentSystemPrompts.CodingAgentContract.TrimEnd()));
            Assert.That(result, Does.Contain("## Error handling"));
            Assert.That(result, Does.Contain("### Error handling: FileNotFound.Edit"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ChildServerArgs_OffNoFile_IsEmpty()
    {
        Assert.That(ErrorPolicyWiring.ChildServerArgs(ErrorPolicyMode.Off, null), Is.Empty);
    }

    [Test]
    public void ChildServerArgs_PointerWithFile_ListsModeThenFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "policy.md");

        Assert.That(ErrorPolicyWiring.ChildServerArgs(ErrorPolicyMode.Pointer, path),
            Is.EqualTo(new[] { "--error-policy-mode=pointer", "--error-policy-file=" + path }));
    }

    [Test]
    public void ChildServerArgs_InlineOnly_ModeOnly()
    {
        Assert.That(ErrorPolicyWiring.ChildServerArgs(ErrorPolicyMode.Inline, null),
            Is.EqualTo(new[] { "--error-policy-mode=inline" }));
    }

    [Test]
    public void ChildServerArgs_OffWithFile_FileOnly()
    {
        string path = Path.Combine(Path.GetTempPath(), "policy.md");

        Assert.That(ErrorPolicyWiring.ChildServerArgs(ErrorPolicyMode.Off, path),
            Is.EqualTo(new[] { "--error-policy-file=" + path }));
    }

    [Test]
    public void WithErrorHandling_AppendsBlockWithHeadingsInAllOrderAndKeepsPromptPrefix()
    {
        const string prompt = "You are a coding agent.\n";
        // Sections deliberately written in reverse so the output order must come from ErrorPolicyKeys.All.
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### SymbolNotFound.Edit\nSymbol edit body.\n\n" +
            "### SymbolNotFound.Lookup\nSymbol lookup body.\n\n" +
            "### FileNotFound.Edit\nEdit body.\n\n" +
            "### FileNotFound.Lookup\nLookup body.\n");

        string result = AgentSystemPrompts.WithErrorHandling(prompt, doc);

        Assert.That(result, Does.StartWith("You are a coding agent."));
        Assert.That(result, Does.Contain("\n\n## Error handling\n\n"));
        var headings = result.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)).ToList();
        Assert.That(headings, Is.EqualTo(ErrorPolicyKeys.All.Select(key => "### " + ErrorPolicyDocument.RenderedHeading(key)).ToList()));
        Assert.That(result, Does.EndWith("Symbol edit body.\n"));
    }

    [Test]
    public void WithErrorHandling_EmptyDocument_ReturnsPromptUnchanged()
    {
        const string prompt = "You are a coding agent.\n";
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse("# Only a preamble, no sections.\n");

        Assert.That(AgentSystemPrompts.WithErrorHandling(prompt, doc), Is.EqualTo(prompt));
    }
}
