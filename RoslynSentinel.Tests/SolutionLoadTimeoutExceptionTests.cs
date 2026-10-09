using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class SolutionLoadTimeoutExceptionTests
{
    [Test]
    public void ToolErrorMapper_MapsToSolutionLoadTimeoutCodeAndKeepsMessage()
    {
        var ex = new SolutionLoadTimeoutException("x");
        var fake = new FakeWorkspaceManager();

        var result = ToolErrorMapper.ToResultError(ex, fake, "ctx");

        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.SolutionLoadTimeout));
        Assert.That(result.Message, Does.Contain("x"));
    }
}
