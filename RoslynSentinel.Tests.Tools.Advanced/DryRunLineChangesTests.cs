using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Dry-run LineChanges coverage. A dry run builds its AppliedChangeSummary through AppliedChangeSummary.From, so LineChanges
// must be filled with real counts and the workspace must stay untouched. autoStage is hardcoded true on these tools now,
// so dryRun is the only preview-only path. In-memory (InMemoryWorkspace), like its siblings.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class DryRunLineChangesTests
{
    private static readonly string SimpleSource = Sources.Order;

    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");

    private static void AssertPlausibleDryRunLineChanges(AppliedChangeSummary summary)
    {
        Assert.That(summary.DryRun, Is.True, "The summary must be flagged as a dry run.");
        Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty, "A dry run must fill LineChanges.");
        Assert.That(summary.LineChanges!.Sum(change => change.LinesAdded), Is.GreaterThan(0), "LinesAdded must count the preview's added lines.");
        Assert.That(summary.LineChanges!.Sum(change => change.LinesRemoved), Is.GreaterThan(0), "LinesRemoved must count the preview's removed lines.");
    }

    // Family: basic refactoring (RefactoringSignatureTools.ChangeAccessibilityTool).
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ChangeAccessibility_DryRun_FillsLineChangesAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var before = workspace.ReadText("Order.cs");
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ChangeAccessibilityTool(reason: "test message", workspace.PathOf("Order.cs"), "OrderId", AccessibilityLevel.@internal, dryRun: true);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        AssertPlausibleDryRunLineChanges(result.SuccessData!);
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(before), "A dry run must not write the file.");
    }

    // Family: structural (RefactoringStructuralTools.ModifyModifierTool).
    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyModifier_DryRun_FillsLineChangesAndWritesNothing()
    {
        var fieldSource = "namespace TestProj;\n\npublic class Widget\n{\n    public int Count;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Widget.cs", fieldSource));
        var before = workspace.ReadText("Widget.cs");
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyModifierTool(reason: "test message", workspace.PathOf("Widget.cs"), "Count", NonAccessibilityModifier.@readonly, AddRemoveAction.add, dryRun: true);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        AssertPlausibleDryRunLineChanges(result.SuccessData!);
        Assert.That(workspace.ReadText("Widget.cs"), Is.EqualTo(before), "A dry run must not write the file.");
    }

    // Family: advanced refactoring (AdvancedRefactoringTools.WrapRangeTool, same range as WrapInTryCatch_ValidStatementRange).
    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task WrapRange_DryRun_FillsLineChangesAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var before = workspace.ReadText("Order.cs");
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 11, 12, WrapRangeWrapper.tryCatch, dryRun: true);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        AssertPlausibleDryRunLineChanges(result.SuccessData!);
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(before), "A dry run must not write the file.");
    }
}
