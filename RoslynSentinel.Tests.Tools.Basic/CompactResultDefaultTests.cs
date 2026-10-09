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
/// (docs/current/plans/plan_mutating_tools_compact_result_default.md, Step 7): a real apply through a real tool
/// (ModifyModifier -> ValidateAndApplyHelper -> AppliedChangeSummary) must not echo the whole file in
/// changedContent, while dry runs and autoStage=false results still do.
/// Runs on an InMemoryWorkspace (no MSBuild load). That workspace has no solution root by default, so the offload to a
/// large-result file cannot happen there (fail-closed: ChangedContentResultId is null and the Note falls back to
/// ReadFile). The offload end-to-end test sets FakeWorkspaceManager.SolutionPath to a temp directory, which is the
/// fake's documented way to supply a root.
/// NonParallelizable: ChangedContentOptions.InlineOnApply is process-wide static state that one test flips.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class CompactResultDefaultTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/CompactResultFixture.cs";

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
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), config),
            new StructuralRefinementEngine(workspaceManager, config),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
    }

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> AddVirtualAsync(
        RefactoringStructuralTools tools, InMemoryWorkspace workspace, bool dryRun = false, bool autoStage = true) =>
        tools.ModifyModifier(
            reason: "test compact result default",
            filePath: workspace.PathOf(FixtureRelativePath),
            targetName: "Target",
            modifier: NonAccessibilityModifier.@virtual,
            action: AddRemoveAction.add,
            autoStage: autoStage,
            dryRun: dryRun);

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
            Assert.That(result.LargeResult, Is.Null, "the compact result itself must not be offloaded");
            Assert.That(summary.Note, Does.Contain("ReadFile"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("public virtual int Target()"), "the edit must have landed");
    }

    [Test]
    public async Task ModifyModifier_Applied_WithoutSolutionRoot_ResultIdIsNullAndNoteFallsBackToReadFileAsync()
    {
        // The in-memory workspace has no solution root, so there is nowhere to write the offloaded content file:
        // the offload fails closed (null id) and the Note must not advertise GetLargeResult.
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        Assert.That(workspace.Manager.GetSolutionRoot(), Is.Null, "precondition: no solution root");

        var result = await AddVirtualAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.ChangedContentResultId, Is.Null);
            Assert.That(summary.Note, Does.Contain("ReadFile"));
            Assert.That(summary.Note, Does.Not.Contain("GetLargeResult"));
        });
    }

    [Test]
    public async Task ModifyModifier_DryRun_StillReturnsChangedContentAsync()
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
            Assert.That(summary.ChangedContent, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.ChangedContent!.Values.Single(), Does.Contain("public virtual int Target()"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a dry run must not write");
    }

    [Test]
    public async Task ModifyModifier_NoStage_StillReturnsChangedContentAndStatusNotWrittenAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddVirtualAsync(tools, workspace, autoStage: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.Status, Is.EqualTo("not_written"));
            Assert.That(summary.ChangedContent, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.ChangedContent!.Values.Single(), Does.Contain("public virtual int Target()"));
            Assert.That(summary.Note, Does.Contain("Nothing was written"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "autoStage=false must not write");
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
    public async Task ModifyModifier_Applied_ResultIdFetchesUpdatedTextViaGetLargeResultAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CompactResultDefaultTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
            // FakeWorkspaceManager.GetSolutionRoot() falls back to the directory of SolutionPath when set.
            workspace.Manager.SolutionPath = Path.Combine(root, "Test.sln");
            var tools = BuildTools(workspace.Manager);

            var result = await AddVirtualAsync(tools, workspace);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            var summary = result.SuccessData!;
            Assert.That(summary.ChangedContent, Is.Null);
            Assert.That(summary.ChangedContentResultId, Is.Not.Null.And.Not.Empty, "a solution root exists, so the content must be offloaded");
            Assert.That(summary.Note, Does.Contain("GetLargeResult").And.Contain(summary.ChangedContentResultId!));

            var readNav = new WorkspaceReadNavigationImpl(workspace.Manager, NullLogger<WorkspaceReadNavigationImpl>.Instance);
            var fetched = await readNav.GetLargeResult(reason: "test fetch offloaded changed content", resultId: summary.ChangedContentResultId);

            Assert.That(fetched.IsError, Is.False, fetched.ErrorData?.Message);
            var json = JsonSerializer.Serialize(fetched.SuccessData);
            Assert.That(json, Does.Contain("public virtual int Target()"), "the stored text must be the updated file");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
