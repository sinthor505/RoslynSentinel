using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Step 5e slice 6 (inventory C5a): a relative solution path that resolves under none of the candidate directories
// reports the paths it tried as the fact, and names LoadSolution's own parameters as options, not as an order.
[TestFixture]
[Category("WorkspaceTools")]
public class SolutionPathMissTests
{
    [Test]
    public async Task LoadSolution_RelativePathUnderNoCandidateDirectory_ListsTriedPathsAndNamesOptionsAsync()
    {
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var workspaceTools = TestTools.WorkspaceTools(workspaceManager);

        var result = await workspaceTools.LoadSolutionTool(reason: "relative path miss", "NoSuchFolder/Missing.sln");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("Could not resolve relative solution path 'NoSuchFolder/Missing.sln'."));
        Assert.That(message, Does.Contain("Tried: ").And.Contain("Missing.sln"));
        Assert.That(message, Does.Contain("LoadSolution accepts an absolute path, or a baseRepoDir"));
        Assert.That(message, Does.Not.Contain("Pass an absolute path"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("tell the user"));
    }
}
