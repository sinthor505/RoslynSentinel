using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Full-pipeline regression tests for the mutating-tool compact-result default
/// (docs/current/plans/closed/plan_mutating_tools_compact_result_default.md, Step 7): a real apply through a real tool
/// (ModifyModifierTool -> ValidateAndApplyHelper -> AppliedChangeSummary) must not echo the whole file in
/// changedContent and neither does a dry run (it carries lineChanges, plus the diff with returnDiff: true), while
/// autoStage=false results still do.
/// Runs on an InMemoryWorkspace (no MSBuild load). A real apply detaches its written files as the "changedFiles" payload
/// of the call's ToolCallContext (proposal_side_stores_never_inline.md, slice 4); the marshaller stores it, which is asserted
/// in Tests.Server ToolResultMarshallerDetachedTests. Here a ToolCallContext is set only by the test that checks the detach;
/// without one nothing is detached and the Note falls back to ReadFileTool.
/// NonParallelizable: ChangedContentOptions.InlineOnApply is process-wide static state that one test flips.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class CompactResultDefaultTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/CompactResultFixture.cs";

    [SetUp]
    [TearDown]
    public void ResetContext() => ToolCallContext.Set(null);

    // About 150 lines: enough that echoing the whole file on a real apply would be the noise the default removes.
    private static readonly string FixtureSource = BuildFixtureSource();

    private static string BuildFixtureSource()
    {
        var sb = new StringBuilder();
        sb.Append("namespace ContosoOrders.Core;\n\npublic class CompactTarget\n{\n    public int Target() => 1;\n");
        for (var i = 0; i < 35; i++)
        {
            sb.Append($"\n    public int Filler{i}(int x)\n    {{\n        var y = x + {i};\n        return y * 2;\n    }}\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> AddVirtualAsync(
        RefactoringStructuralTools tools, InMemoryWorkspace workspace, bool dryRun = false, bool returnDiff = false) =>
        tools.ModifyModifierTool(
            reason: "test compact result default",
            filePath: workspace.PathOf(FixtureRelativePath),
            targetName: "Target",
            modifier: NonAccessibilityModifier.@virtual,
            action: AddRemoveAction.add,
            dryRun: dryRun,
            returnDiff: returnDiff);

    [Test]
    public async Task ModifyModifier_Applied_ResultHasNoChangedContentHasLineChangesAndNoteAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddVirtualAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.False);
            Assert.That(summary.Status, Is.EqualTo("applied"));
            Assert.That(summary.ChangedContent, Is.Null, "a real apply must not echo the file");
            Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.Note, Does.Contain("ReadFile"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("public virtual int Target()"), "the edit must have landed");
    }

    [Test]
    public async Task ModifyModifier_Applied_WithoutToolCallContext_NothingIsDetachedAndNoteFallsBackToReadFileAsync()
    {
        // No ToolCallContext (an engine or test calling the tool directly): DetachCurrent is a no-op, so the Note must not
        // advertise GetLargeResultTool for a payload that was never declared.
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        Assert.That(ToolCallContext.Current, Is.Null, "precondition: no tool call context");

        var result = await AddVirtualAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.Note, Does.Contain("ReadFile"));
            Assert.That(summary.Note, Does.Not.Contain("GetLargeResult"));
        });
    }

    [Test]
    public async Task ModifyModifier_DryRun_IsCompactByDefaultAndNoteNamesReturnDiffAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddVirtualAsync(tools, workspace, dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.True);
            Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
            Assert.That(summary.ChangedContent, Is.Null, "a dry run must not echo the whole file by default");
            Assert.That(summary.Diff, Is.Null, "no diff unless returnDiff is asked for");
            Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.Note, Does.Contain("returnDiff: true"));
            Assert.That(summary.Note, Does.Contain("Re-call with dryRun=false to apply."));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a dry run must not write");
    }

    [Test]
    public async Task ModifyModifier_DryRun_ReturnDiffShowsTheChangeWithoutTheWholeFileAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddVirtualAsync(tools, workspace, dryRun: true, returnDiff: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.True);
            Assert.That(summary.Diff, Does.Contain("public virtual int Target()"));
            Assert.That(summary.ChangedContent, Is.Null, "the diff is the preview, never the diff and the whole file");
            Assert.That(summary.Note, Does.Contain("The diff is the preview."));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a dry run must not write");
    }

    [Test]
    public async Task ModifyModifier_InlineOptionOn_ReturnsChangedContentAsync()
    {
        var original = ChangedContentOptions.InlineOnApply;
        try
        {
            ChangedContentOptions.InlineOnApply = true;
            using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
            var tools = BuildTools(workspace.Manager);

            var result = await AddVirtualAsync(tools, workspace);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            var summary = result.SuccessData!;
            Assert.Multiple(() =>
            {
                Assert.That(summary.DryRun, Is.False);
                Assert.That(summary.Status, Is.EqualTo("applied"));
                Assert.That(summary.ChangedContent, Is.Not.Null.And.Not.Empty);
                Assert.That(summary.ChangedContent!.Values.Single(), Does.Contain("public virtual int Target()"));
            });
        }
        finally
        {
            ChangedContentOptions.InlineOnApply = original;
        }
    }

    [Test]
    public async Task ModifyModifier_Applied_DetachesTheUpdatedTextAsChangedFilesAndNotePointsAtLargeResultAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var context = new ToolCallContext("agent-1", "implementer", "ModifyModifier", "test detach", 0);
        ToolCallContext.Set(context);

        var result = await AddVirtualAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.That(summary.ChangedContent, Is.Null);
        var detached = context.Detached.Single(d => d.Name == "changedFiles");
        var rows = detached.Payload as List<ChangedFile>;
        Assert.That(rows, Is.Not.Null.And.Count.EqualTo(1));
        Assert.That(rows![0].Path, Does.EndWith("CompactResultFixture.cs"));
        Assert.That(rows[0].Text, Does.Contain("public virtual int Target()"), "the detached text must be the updated file");
        Assert.That(JsonSerializer.Serialize(summary), Does.Not.Contain("public virtual int Target()"), "the file text is not in the serialized summary");
        Assert.That(summary.Note, Does.Contain("GetLargeResult").And.Contain("largeResult.resultId").And.Contain("successData.changedFiles"));
    }
}
