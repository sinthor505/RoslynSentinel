using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class FailureMessageNextStepTests
{
    private static WorkspaceTools BuildTools(IWorkspaceManager workspaceManager) =>
        TestTools.WorkspaceTools(workspaceManager);

    [Test]
    public async Task ListSolutionItems_UnknownKind_ListsValidValuesAndNextCallAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("ContosoOrders.Core/Helper.cs", "namespace TestProj;\n\npublic static class Helper\n{\n}\n"));
        var workspaceTools = BuildTools(workspace.Manager);

        var result = await workspaceTools.ListSolutionItemsTool(reason: "test message", (SolutionItemsKind)999);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("Valid values:").And.Contain("projects"));
        Assert.That(result.ErrorData.Message, Does.Contain("Next: ListSolutionItems("));
    }

    [Test]
    public async Task LoadSolution_TimeoutOutOfRange_NextStepNamesLoadSolutionAsync()
    {
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var workspaceTools = BuildTools(workspaceManager);

        var result = await workspaceTools.LoadSolutionTool(reason: "test message", "ok.sln", timeoutSeconds: 0);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("Next: LoadSolution("));
    }

    [Test]
    public async Task LoadSolution_MissingFile_NextStepMatchesErrorCodeAsync()
    {
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var workspaceTools = BuildTools(workspaceManager);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.sln");

        var result = await workspaceTools.LoadSolutionTool(reason: "test message", missing);

        Assert.That(result.IsError, Is.True);
        var expectedHint = result.ErrorData!.ErrorCode == ToolErrorCode.NotFound
            ? "ListWorkspaceSolutions"
            : "retry LoadSolution";
        Assert.That(result.ErrorData.Message, Does.Contain(expectedHint));
    }
}
