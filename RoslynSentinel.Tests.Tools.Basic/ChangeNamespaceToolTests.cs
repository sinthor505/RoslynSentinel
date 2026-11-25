using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// ChangeNamespaceTool wiring (plan Step 33, T33.3 and T33.4): the dryRun flag, UndoChange, and the newNamespace/matchFolder
// either-or check. The apply tests run on a real PersistentWorkspaceManager over a TestSolutionFixture copy, because undo
// restores files on disk. The either-or test is refused before the solution is read, so it runs in memory.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("ChangeNamespace")]
public sealed class ChangeNamespaceToolTests
{
    private const string NewNamespace = "ContosoOrders.Core.Modern";
    private const string WidgetRelative = "ContosoOrders.Core/NamespaceToolWidget.cs";
    private const string ConsumerRelative = "ContosoOrders.Core/Consumers/NamespaceToolConsumer.cs";
    private const string WidgetText = "namespace ContosoOrders.Core.Legacy;\r\n\r\npublic class NamespaceToolWidget\r\n{\r\n    public int Size;\r\n}\r\n";
    private const string ConsumerText = "using ContosoOrders.Core.Legacy;\r\n\r\nnamespace ContosoOrders.Core.Consumers;\r\n\r\npublic class NamespaceToolConsumer\r\n{\r\n    public NamespaceToolWidget Item = new NamespaceToolWidget();\r\n}\r\n";

    private static string PathIn(TestSolutionFixture fixture, string relative) => Path.Combine(fixture.SolutionDirectory, relative);

    // Loads the solution, writes the widget and its consumer to disk, then reloads once and acknowledges the external changes.
    private static async Task<PersistentWorkspaceManager> LoadWithFilesAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        await fixture.AddFileToSolution(manager, WidgetRelative, WidgetText, reloadSolution: false);
        await fixture.AddFileToSolution(manager, ConsumerRelative, ConsumerText, reloadSolution: false);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        manager.ClearExternalFileChanges();
        return manager;
    }

    [Test]
    public async Task DryRun_ReturnsFileCountsAndWritesNothing()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadWithFilesAsync(fixture);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(manager);
        var widgetPath = PathIn(fixture, WidgetRelative);
        var consumerPath = PathIn(fixture, ConsumerRelative);
        var widgetBefore = await File.ReadAllTextAsync(widgetPath);
        var consumerBefore = await File.ReadAllTextAsync(consumerPath);

        var result = await tools.ChangeNamespaceTool(reason: "test message", filePath: widgetPath, newNamespace: NewNamespace, dryRun: true);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData?.Status, Is.EqualTo("dry_run_ok"));
        Assert.That(result.SuccessData?.AffectedFiles, Has.Count.EqualTo(2), "The widget and its consumer both change, so the dry run counts two files.");
        Assert.That(await File.ReadAllTextAsync(widgetPath), Is.EqualTo(widgetBefore), "A dry run must not write the widget.");
        Assert.That(await File.ReadAllTextAsync(consumerPath), Is.EqualTo(consumerBefore), "A dry run must not write the consumer.");
    }

    [Test]
    public async Task UndoChange_RestoresAll()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = await LoadWithFilesAsync(fixture);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(manager);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var widgetPath = PathIn(fixture, WidgetRelative);
        var consumerPath = PathIn(fixture, ConsumerRelative);
        var widgetBefore = await File.ReadAllTextAsync(widgetPath);
        var consumerBefore = await File.ReadAllTextAsync(consumerPath);

        var applied = await tools.ChangeNamespaceTool(reason: "test message", filePath: widgetPath, newNamespace: NewNamespace);

        Assert.That(applied.IsError, Is.False);
        Assert.That(applied.SuccessData?.Status, Is.EqualTo("applied"));
        Assert.That(applied.SuccessData?.ChangeId, Is.Not.Null.And.Not.Empty);
        Assert.That(await File.ReadAllTextAsync(widgetPath), Does.Contain($"namespace {NewNamespace};"));

        var undo = await workspaceTools.UndoChangeTool(reason: "test message", changeId: applied.SuccessData!.ChangeId!);

        Assert.That(undo.IsError, Is.False);
        Assert.That(await File.ReadAllTextAsync(widgetPath), Is.EqualTo(widgetBefore), "Undo restores the widget to its pre-apply text.");
        Assert.That(await File.ReadAllTextAsync(consumerPath), Is.EqualTo(consumerBefore), "Undo restores the consumer to its pre-apply text.");
    }

    [Test]
    public async Task NeitherOrBothOfNewNamespaceAndMatchFolder_InvalidArgument()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", "public class Refactor\r\n{\r\n}\r\n"));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var path = workspace.PathOf("Refactor.cs");

        var neither = await tools.ChangeNamespaceTool(reason: "test message", filePath: path);
        var both = await tools.ChangeNamespaceTool(reason: "test message", filePath: path, newNamespace: NewNamespace, matchFolder: true);

        Assert.That(neither.IsError, Is.True);
        Assert.That(neither.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(both.IsError, Is.True);
        Assert.That(both.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }
}
