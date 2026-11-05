using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// SubAgentModelRun.AwaitSolutionLoadAsync only waits when a load is running (SolutionLoadStatus.Loading).
// FakeWorkspaceManager never reports Loading, so only the "nothing to wait for" path is covered here.
// TODO cover the Loading-and-times-out path (needs a fake whose SolutionLoadStatus is Loading and whose WaitForLoadAsync returns Loading)
[TestFixture]
public class SubAgentModelRunTests
{
    [Test]
    public async Task AwaitSolutionLoadAsync_NoSolutionLoaded_ReturnsNullImmediately()
    {
        using var workspaceManager = FakeWorkspaceManager.ReadOnly();

        var error = await SubAgentModelRun.AwaitSolutionLoadAsync(workspaceManager, CancellationToken.None);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task AwaitSolutionLoadAsync_SolutionLoaded_ReturnsNullImmediately()
    {
        using var workspaceManager = FakeWorkspaceManager.ReadOnly();
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("A.cs", "public class A {}")]));

        var error = await SubAgentModelRun.AwaitSolutionLoadAsync(workspaceManager, CancellationToken.None);

        Assert.That(error, Is.Null);
    }
}
