using RoslynSentinel;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class UnrecoverableBreakerPolicyTests
{
    [SetUp]
    public void SetUp()
    {
        // Force-load all Tools.* assemblies by touching a type from each so the policy's
        // reflection discovery finds all annotated methods regardless of test execution order.
        _ = typeof(AdminTools).Assembly;
        _ = typeof(GitTools).Assembly;
        _ = typeof(WorkspaceFileEditTools).Assembly;
        _ = typeof(WorkspaceReadNavigationTools).Assembly;
        _ = typeof(WorkspaceProjectManagementTools).Assembly;
        _ = typeof(WorkspaceHealthMiscTools).Assembly;
        _ = typeof(WorkspaceTools).Assembly;
        _ = typeof(ScanTools).Assembly;   // Tools.Advanced
        _ = typeof(GenerationTools).Assembly; // Tools.Experimental (if present)
    }

    [Test]
    public void AllowedToolNames_EqualsExpectedSet()
    {
        // Golden test: the allow-list must be exactly these 8 tools, ordinal comparison, sorted.
        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            "ReadFile",
            "ListAll",
            "ListSolutionItems",
            "GetFileOutline",
            "GetOperationDetail",
            "GetWorkspaceHealth",
            "IsSessionHalted",
            "Git"
        };

        var actual = new SortedSet<string>(UnrecoverableBreakerPolicy.AllowedToolNames, StringComparer.Ordinal);

        Assert.That(actual, Is.EqualTo(expected),
            $"The allow-list is defined by [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)] " +
            $"attributes on tool methods. Expected 8 distinct tool names; got {actual.Count}. " +
            $"Missing: {string.Join(", ", expected.Except(actual))}. " +
            $"Extra: {string.Join(", ", actual.Except(expected))}.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("WriteFile")]
    [TestCase("ApplyDiff")]
    [TestCase("McpServerStatus")]
    public void IsAllowed_ReturnsFalseForBlockedTools(string? toolName)
    {
        Assert.That(UnrecoverableBreakerPolicy.IsAllowed(toolName), Is.False,
            $"Tool '{toolName}' must be blocked (not in the allow-list).");
    }

    [Test]
    public void IsAllowed_ReturnsTrueForGit()
    {
        Assert.That(UnrecoverableBreakerPolicy.IsAllowed("Git"), Is.True,
            "Git must be in the allow-list.");
    }

    [TestCase("ReadFile")]
    [TestCase("ListAll")]
    [TestCase("ListSolutionItems")]
    [TestCase("GetFileOutline")]
    [TestCase("GetOperationDetail")]
    [TestCase("GetWorkspaceHealth")]
    [TestCase("IsSessionHalted")]
    public void IsAllowed_ReturnsTrueForAllowedTools(string toolName)
    {
        Assert.That(UnrecoverableBreakerPolicy.IsAllowed(toolName), Is.True,
            $"Tool '{toolName}' must be in the allow-list.");
    }
}
