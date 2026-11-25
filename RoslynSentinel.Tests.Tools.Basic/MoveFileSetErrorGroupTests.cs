using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Tool-level tests for the grouped MoveFile refusal (plan Step 21, rows T21.1-T21.2). Same fixture style as MoveFileSetTests: a real
// PersistentWorkspaceManager over a TestSolutionFixture copy of ContosoOrders. The grouping runs inside the compile gate's describer
// (chokepoint behaviour), so a real manager is needed rather than a stub.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
public sealed class MoveFileSetErrorGroupTests
{
    private const string CoreFolder = "ContosoOrders.Core";
    private const string TestsFolder = "ContosoOrders.Tests";

    private static async Task<PersistentWorkspaceManager> LoadAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        return manager;
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager, WorkspaceTools workspaceTools)
    {
        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestTools.WholeFileWriteTools(manager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager) => BuildTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager));

    // Calls MoveFile with one list of pairs (a set of one is a single move).
    private static Task<SentinelCallToolResult<AppliedChangeSummary>> MoveSetAsync(WholeFileWriteTools tools, IEnumerable<(string Source, string Destination)> pairs, MoveFileNamespaceHandling handling, bool dryRun = false)
    {
        var moves = pairs.Select(pair => new FileMovePair { SourcePath = pair.Source, DestinationPath = pair.Destination }).ToList();
        return tools.MoveFileTool(reason: "test message", moves, handling, dryRun);
    }

    private static string PathIn(TestSolutionFixture fixture, params string[] segments) =>
        Path.Combine(new[] { fixture.SolutionDirectory }.Concat(segments).ToArray());

    // The effects are recorded through ToolCallContext.Current, so a call context must be set before the MoveFile call.
    private static void StartMoveCall()
    {
        var context = new ToolCallContext("agent-1", "coder", "MoveFile", "move the set", 0) { SessionId = "session-1" };
        context.AssignToolCallId("c3e5a7b9");
        ToolCallContext.Set(context);
    }

    private static void AssertValidationFailed(SentinelCallToolResult<AppliedChangeSummary> result)
    {
        Assert.That(result.IsError, Is.True, "The move should have been refused.");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
    }

    // The error groups ride on StructuredDetail as one extra item after any EditImpactDetail items, so the list is found by type, not by index.
    private static IReadOnlyList<MoveSetErrorGroup> GroupsOf(SentinelCallToolResult<AppliedChangeSummary> result)
    {
        Assert.That(result.ErrorData!.StructuredDetail, Is.Not.Null, "The refusal carries structured detail.");
        var lists = result.ErrorData.StructuredDetail!.OfType<IReadOnlyList<MoveSetErrorGroup>>().ToList();
        Assert.That(lists, Has.Count.EqualTo(1), "Exactly one error-group list is in the detail.");
        return lists[0];
    }

    // Real manager: the gate refusal that carries the grouped errors is chokepoint behaviour, not a unit of the grouper alone.
    [Test]
    public async Task GateRefusal_GroupsErrorsBySetFile_Async()
    {
        using var fixture = new TestSolutionFixture();
        var testsFolder = PathIn(fixture, TestsFolder);
        var aSource = Path.Combine(testsFolder, "GroupA.cs");
        File.WriteAllText(aSource, "namespace ContosoOrders.Tests;\r\n\r\npublic class GroupA\r\n{\r\n    public GroupB? Peer { get; set; }\r\n}\r\n");
        File.WriteAllText(Path.Combine(testsFolder, "GroupB.cs"), "namespace ContosoOrders.Tests;\r\n\r\npublic class GroupB\r\n{\r\n}\r\n");
        var aDestination = PathIn(fixture, CoreFolder, "GroupSet", "GroupA.cs");
        using var manager = await LoadAsync(fixture);
        StartMoveCall();

        // Only GroupA moves; Core cannot see GroupB in Tests, so the destination file breaks.
        var result = await MoveSetAsync(BuildTools(manager), [(aSource, aDestination)], MoveFileNamespaceHandling.keep);

        AssertValidationFailed(result);
        var groups = GroupsOf(result);
        var destinationGroup = groups.SingleOrDefault(group => !group.OutsideSet && PathComparison.Comparer.Equals(group.FilePath, aDestination));
        Assert.That(destinationGroup, Is.Not.Null, "The errors bind to the destination of GroupA.cs, inside the set.");
        Assert.That(destinationGroup!.Diagnostics, Is.Not.Empty, "The set file's group lists its compile errors.");
        Assert.That(PathComparison.Comparer.Equals(destinationGroup.SourcePath, aSource), Is.True, "The group names the original source path.");
        Assert.That(File.Exists(aSource), Is.True, "The file is still in Tests.");
        Assert.That(File.Exists(aDestination), Is.False, "Nothing reached Core.");
    }

    // Real manager: the break is only visible to the compile gate, which runs over the whole workspace after the move.
    [Test]
    public async Task GateRefusal_ErrorOutsideSet_GoesToOutsideGroupAsync()
    {
        using var fixture = new TestSolutionFixture();
        var testsFolder = PathIn(fixture, TestsFolder);
        var hiddenSource = Path.Combine(testsFolder, "GroupHidden.cs");
        File.WriteAllText(hiddenSource, "namespace ContosoOrders.Tests;\r\n\r\ninternal class GroupHidden\r\n{\r\n    public int Value { get; set; }\r\n}\r\n");
        File.WriteAllText(Path.Combine(testsFolder, "GroupUser.cs"), "namespace ContosoOrders.Tests;\r\n\r\npublic class GroupUser\r\n{\r\n    public GroupHidden? Item { get; set; }\r\n}\r\n");
        var hiddenDestination = PathIn(fixture, CoreFolder, "GroupSet", "GroupHidden.cs");
        using var manager = await LoadAsync(fixture);
        StartMoveCall();

        // GroupHidden is internal; once it lives in Core, GroupUser (not in the set) cannot see it.
        var result = await MoveSetAsync(BuildTools(manager), [(hiddenSource, hiddenDestination)], MoveFileNamespaceHandling.keep);

        AssertValidationFailed(result);
        var groups = GroupsOf(result);
        var outsideGroup = groups.SingleOrDefault(group => group.OutsideSet);
        Assert.That(outsideGroup, Is.Not.Null, "The break in a non-set file is reported in the outside group.");
        Assert.That(outsideGroup!.Diagnostics, Is.Not.Empty, "The outside group lists its compile errors.");
        Assert.That(outsideGroup.SourcePath, Is.Null, "The outside group has no source path.");
    }
}
