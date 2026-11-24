using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Consumer-level coverage (one consumer, the ReadFile not-found hint) for ActiveToolSurface.IsCallable reading the live
/// snapshot: a tool a runtime toolset added is offered by the hint, a tool with no snapshot row is not.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")]
public class CallableToolHintLiveSurfaceTests
{
    private const string MissingPath = "C:\\x\\Missing.cs";

    // No active class and no allow-list: the startup rule says no tool is callable, so only the snapshot can say yes.
    private static ActiveToolSurface StartupSurfaceWithoutListSolutionItems()
    {
        var none = new HashSet<string>();
        return new ActiveToolSurface("test", none, none, none, new HashSet<string>(), null, null);
    }

    private static ToolSurfaceSnapshot SnapshotWith(params ToolSurfaceTool[] rows) =>
        new(1, DateTime.UtcNow, "test", "fingerprint", rows, [], []);

    private static Solution SolutionWithoutTheRequestedFile()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        workspace.AddDocument(project.Id, "Other.cs", SourceText.From("class A { }"));
        return workspace.CurrentSolution;
    }

    [Test]
    public void ReadFileNotFound_SnapshotListsListSolutionItemsAsRuntimeToolset_OffersIt()
    {
        var snapshot = SnapshotWith(
            new ToolSurfaceTool("ListSolutionItems", "WorkspaceTools", ToolSurfaceOrigin.RuntimeToolset, "projectAdmin", [], []));
        var surface = StartupSurfaceWithoutListSolutionItems().WithLiveSnapshot(() => snapshot);

        var error = WorkspaceFileEditImpl.BuildFileNotFoundError(SolutionWithoutTheRequestedFile(), MissingPath, surface.IsCallable);

        Assert.That(error.Message, Does.Contain("ListSolutionItems("));
        Assert.That(error.Message, Does.Contain("lists every file"));
    }

    [Test]
    public void ReadFileNotFound_SnapshotWithoutTheRow_DoesNotOfferIt()
    {
        var snapshot = SnapshotWith(
            new ToolSurfaceTool("ReadFile", "WorkspaceTools", ToolSurfaceOrigin.Startup, null, [], []));
        var surface = StartupSurfaceWithoutListSolutionItems().WithLiveSnapshot(() => snapshot);

        var error = WorkspaceFileEditImpl.BuildFileNotFoundError(SolutionWithoutTheRequestedFile(), MissingPath, surface.IsCallable);

        Assert.That(error.Message, Does.Not.Contain("ListSolutionItems"));
        Assert.That(error.Message, Does.Contain("renamed or moved"));
    }
}
