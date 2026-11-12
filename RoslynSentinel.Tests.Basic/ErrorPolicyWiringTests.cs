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
}
