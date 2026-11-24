// Coverage for plan_tool_surface_controller_stage_c.md Step 4: ToolSurfaceController.ApplyToolset is the single writer
// of runtime toolset tools. Covers enable and disable against a bare McpServerPrimitiveCollection (no host), the
// Changed ordering, snapshot attribution to the applying set, the first-refresh path and concurrent use.
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // ConcurrentApplyAndRefresh_FinishesAndStaysConsistent runs applies and refreshes on worker threads.
[Category("ToolClassRegistry")]
public class ToolSurfaceControllerApplyTests
{
    private static (ToolSurfaceController Controller, McpServerPrimitiveCollection<McpServerTool> Collection) CreateController()
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var options = Options.Create(new McpServerOptions { ToolCollection = collection });
        return (new ToolSurfaceController(options), collection);
    }

    private static McpServerTool MakeTool(string name) =>
        McpServerTool.Create((string x) => x, new McpServerToolCreateOptions { Name = name });

    private static void ToggleSet(ToolSurfaceController controller, ToolSetName toolSet, IReadOnlyList<string> names)
    {
        for (var i = 0; i < 50; i++)
        {
            controller.ApplyToolset(toolSet, enabled: i % 3 != 0, names, MakeTool);
        }
    }

    [Test]
    public void Enable_AddsEveryCreatedTool_AndReportsTheToolsetChangeFields()
    {
        var (controller, collection) = CreateController();

        var change = controller.ApplyToolset(ToolSetName.git, true, ["GitStatusTool", "GitLogTool"], MakeTool);

        Assert.That(change.ToolSet, Is.EqualTo(ToolSetName.git));
        Assert.That(change.Enabled, Is.True);
        Assert.That(change.Changed, Is.True);
        Assert.That(change.AddedTools, Is.EqualTo(new[] { "GitStatusTool", "GitLogTool" }));
        Assert.That(change.RemovedTools, Is.Empty);
        Assert.That(change.UnavailableTools, Is.Empty);
        Assert.That(change.EnabledToolSets, Is.EqualTo(new[] { ToolSetName.git }));
        Assert.That(change.ActiveTools, Is.EqualTo(new[] { "GitLogTool", "GitStatusTool" }));
        Assert.That(collection.TryGetPrimitive("GitStatusTool", out _), Is.True);
        Assert.That(collection.TryGetPrimitive("GitLogTool", out _), Is.True);
        Assert.That(controller.RuntimeToolNames, Is.EqualTo(new[] { "GitLogTool", "GitStatusTool" }));
    }

    [Test]
    public void Enable_NameAlreadyInCollection_IsUnavailable_AndFactoryNotCalled()
    {
        var (controller, collection) = CreateController();
        var startupTool = MakeTool("ModifyEnum");
        collection.Add(startupTool);
        var requested = new List<string>();

        McpServerTool? Factory(string name)
        {
            requested.Add(name);
            return MakeTool(name);
        }

        var change = controller.ApplyToolset(ToolSetName.declarations, true, ["ModifyEnum", "FreshTool"], Factory);

        Assert.That(requested, Is.EqualTo(new[] { "FreshTool" }));
        Assert.That(change.UnavailableTools, Is.EqualTo(new[] { "ModifyEnum" }));
        Assert.That(change.AddedTools, Is.EqualTo(new[] { "FreshTool" }));
        Assert.That(collection.TryGetPrimitive("ModifyEnum", out var remaining), Is.True);
        Assert.That(remaining, Is.SameAs(startupTool));
    }

    [Test]
    public void Enable_FactoryReturnsNull_ReportsUnavailable()
    {
        var (controller, collection) = CreateController();

        var change = controller.ApplyToolset(ToolSetName.git, true, ["NullTool", "OkTool"],
            name => name == "NullTool" ? null : MakeTool(name));

        Assert.That(change.UnavailableTools, Is.EqualTo(new[] { "NullTool" }));
        Assert.That(change.AddedTools, Is.EqualTo(new[] { "OkTool" }));
        Assert.That(collection.TryGetPrimitive("NullTool", out _), Is.False);
        Assert.That(collection.TryGetPrimitive("OkTool", out _), Is.True);
    }

    [Test]
    public void Enable_FactoryThrows_LeavesCollectionAndLayerUntouched()
    {
        var (controller, collection) = CreateController();

        McpServerTool? Factory(string name) =>
            name == "Broken" ? throw new InvalidOperationException("factory failed") : MakeTool(name);

        Assert.Throws<InvalidOperationException>(() =>
            controller.ApplyToolset(ToolSetName.git, true, ["Fine", "Broken"], Factory));

        Assert.That(collection.TryGetPrimitive("Fine", out _), Is.False);
        Assert.That(collection.TryGetPrimitive("Broken", out _), Is.False);
        Assert.That(controller.RuntimeToolSets, Is.Empty);
        Assert.That(controller.RuntimeToolNames, Is.Empty);
    }

    [Test]
    public void Disable_RemovesExactlyTheInstancesItAdded()
    {
        var (controller, collection) = CreateController();
        var startupTool = MakeTool("StartupTool");
        collection.Add(startupTool);
        controller.ApplyToolset(ToolSetName.git, true, ["GitStatusTool", "GitLogTool"], MakeTool);

        var change = controller.ApplyToolset(ToolSetName.git, false, ["GitStatusTool", "GitLogTool"], MakeTool);

        Assert.That(change.Enabled, Is.False);
        Assert.That(change.Changed, Is.True);
        Assert.That(change.RemovedTools, Is.EqualTo(new[] { "GitStatusTool", "GitLogTool" }));
        Assert.That(change.AddedTools, Is.Empty);
        Assert.That(change.EnabledToolSets, Is.Empty);
        Assert.That(change.ActiveTools, Is.EqualTo(new[] { "StartupTool" }));
        Assert.That(collection.TryGetPrimitive("StartupTool", out var remaining), Is.True);
        Assert.That(remaining, Is.SameAs(startupTool));
        Assert.That(controller.RuntimeToolSets, Is.Empty);
    }

    [Test]
    public void RepeatedApply_IsNoOp_AndRaisesNoChanged()
    {
        var (controller, collection) = CreateController();
        var changedCount = 0;
        collection.Changed += (_, _) => changedCount++;

        controller.ApplyToolset(ToolSetName.git, true, ["GitStatusTool"], MakeTool);
        var countAfterFirst = changedCount;

        var again = controller.ApplyToolset(ToolSetName.git, true, ["GitStatusTool"], MakeTool);
        var offAgain = controller.ApplyToolset(ToolSetName.moveExtract, false, ["MoveTool"], MakeTool);

        Assert.That(countAfterFirst, Is.EqualTo(1));
        Assert.That(changedCount, Is.EqualTo(countAfterFirst));
        Assert.That(again.Enabled, Is.True);
        Assert.That(again.Changed, Is.False);
        Assert.That(again.AddedTools, Is.Empty);
        Assert.That(again.UnavailableTools, Is.Empty);
        Assert.That(again.EnabledToolSets, Is.EqualTo(new[] { ToolSetName.git }));
        Assert.That(offAgain.Changed, Is.False);
    }

    [Test]
    public void Enable_RaisesChangedOnce_WithLayerAlreadyUpdatedInTheHandler()
    {
        var (controller, collection) = CreateController();
        var notifications = 0;
        IReadOnlyList<ToolSetName>? setsSeen = null;
        IReadOnlyCollection<string>? namesSeen = null;
        collection.Changed += (_, _) =>
        {
            notifications++;
            setsSeen = controller.RuntimeToolSets;
            namesSeen = controller.RuntimeToolNames;
        };

        controller.ApplyToolset(ToolSetName.git, true, ["GitStatusTool", "GitLogTool"], MakeTool);

        Assert.That(notifications, Is.EqualTo(1));
        Assert.That(setsSeen, Is.EqualTo(new[] { ToolSetName.git }));
        Assert.That(namesSeen, Is.EqualTo(new[] { "GitLogTool", "GitStatusTool" }));
    }

    [Test]
    public void Snapshot_AttributesToolToTheSetThatAddedIt_NotTheCatalogSet()
    {
        var (controller, _) = CreateController();
        controller.Refresh("startup");
        controller.ApplyToolset(ToolSetName.git, true, ["TypedMember"], MakeTool);

        var snapshot = controller.Refresh("test");
        var row = snapshot.Find("TypedMember");

        Assert.That(row, Is.Not.Null);
        Assert.That(row!.ToolSet, Is.EqualTo("git"));
        Assert.That(row.Origin, Is.EqualTo(ToolSurfaceOrigin.RuntimeToolset));
        Assert.That(ToolsetCatalog.FindSet("TypedMember"), Is.EqualTo(ToolSetName.declarations));
    }

    [Test]
    public void ApplyBeforeFirstRefresh_FirstSnapshotIsVersionOne_WithRuntimeOrigin()
    {
        var (controller, _) = CreateController();
        controller.ApplyToolset(ToolSetName.git, true, ["TypedMember"], MakeTool);

        var snapshot = controller.Refresh("startup");

        Assert.That(snapshot.Version, Is.EqualTo(1));
        Assert.That(snapshot.Trigger, Is.EqualTo("startup"));
        Assert.That(snapshot.EnabledToolSets, Is.EqualTo(new[] { "git" }));
        Assert.That(snapshot.Find("TypedMember")?.Origin, Is.EqualTo(ToolSurfaceOrigin.RuntimeToolset));
    }

    [Test]
    public void UndefinedToolSet_Throws_AndChangesNothing()
    {
        var (controller, collection) = CreateController();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.ApplyToolset((ToolSetName)999, true, ["TypedMember"], MakeTool));

        Assert.That(collection.TryGetPrimitive("TypedMember", out _), Is.False);
        Assert.That(controller.RuntimeToolSets, Is.Empty);
        Assert.That(controller.RuntimeToolNames, Is.Empty);
    }

    [Test]
    public void NoCollection_ThrowsWithToolCollectionMessage()
    {
        var controller = new ToolSurfaceController(Options.Create(new McpServerOptions()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            controller.ApplyToolset(ToolSetName.git, true, ["TypedMember"], MakeTool));

        Assert.That(ex.Message, Does.Contain("tool collection"));
        Assert.That(controller.RuntimeToolSets, Is.Empty);
    }

    [Test]
    public async Task ConcurrentApplyAndRefresh_FinishesAndStaysConsistent()
    {
        var (controller, collection) = CreateController();
        controller.Refresh("startup");

        var workers = new List<Task>
        {
            Task.Run(() => ToggleSet(controller, ToolSetName.git, ["GitStatusTool", "GitLogTool"])),
            Task.Run(() => ToggleSet(controller, ToolSetName.declarations, ["DeclarationsTool"])),
            Task.Run(() => ToggleSet(controller, ToolSetName.moveExtract, ["MoveExtractTool"])),
            Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    controller.Refresh("concurrent");
                }
            }),
        };

        var all = Task.WhenAll(workers);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.That(finished, Is.SameAs(all), "Concurrent applies and refreshes did not finish within 30 s.");
        await all;

        Assert.That(collection.PrimitiveNames.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            Is.EqualTo(controller.RuntimeToolNames.ToList()));
    }
}
