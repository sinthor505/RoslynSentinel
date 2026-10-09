using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// An applied Member add must return line changes, not the whole file back as changedContent
/// (docs/current/plans/plan_mutating_and_test_tool_result_noise.md, Step 2). Dry runs and autoStage=false results
/// still carry the content: nothing was written, so the content is the result.
/// Runs on an InMemoryWorkspace, which has no solution root: the offloaded content file cannot be written there, so
/// ChangedContentResultId is null by design (fail-closed) and is not asserted.
/// NonParallelizable: another fixture flips the process-wide ChangedContentOptions.InlineOnApply, which these
/// assertions depend on.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class MemberAddResultSizeTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberAddResultSizeFixture.cs";

    // About 200 lines so that a whole-file echo would be obviously large.
    private static readonly string FixtureSource = BuildFixtureSource();

    private static string BuildFixtureSource()
    {
        var sb = new StringBuilder();
        sb.Append("namespace ContosoOrders.Core;\n\npublic class MemberAddTarget\n{\n    public int Existing() => 1;\n");
        for (var i = 0; i < 45; i++)
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

    private static Task<SentinelCallToolResult<object>> AddMemberAsync(
        RefactoringStructuralTools tools, InMemoryWorkspace workspace, bool dryRun = false, bool autoStage = true) =>
        tools.Member(
            reason: "test add member result size",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "MemberAddTarget",
            position: "end",
            newMemberSource: "public int Added() => 42;",
            autoStage: autoStage,
            dryRun: dryRun);

    private static Task<SentinelCallToolResult<object>> AddTopLevelTypeAsync(
        RefactoringStructuralTools tools, InMemoryWorkspace workspace, bool dryRun = false) =>
        tools.Member(
            reason: "test add top level type result size",
            operation: MemberAction.addTopLevelType,
            filePath: workspace.PathOf(FixtureRelativePath),
            newMemberSource: "public class AddedTopLevelType\n{\n    public int Value => 7;\n}",
            dryRun: dryRun);

    [Test]
    public async Task Member_AddMember_AppliedResultDoesNotEchoWholeFileAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddMemberAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.False);
            Assert.That(summary.ChangedContent, Is.Null, "an applied add must not echo the whole file");
            Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.Note, Does.Contain("ReadFile"));
            Assert.That(result.LargeResult, Is.Null);
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("public int Added() => 42;"), "the add must have landed");
    }

    [Test]
    public async Task Member_AddMember_DryRunStillReturnsChangedContentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddMemberAsync(tools, workspace, dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.True);
            Assert.That(summary.ChangedContent, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.ChangedContent!.Values.Single(), Does.Contain("public int Added() => 42;"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a dry run must not write");
    }

    [Test]
    public async Task Member_AddTopLevelType_AppliedResultDoesNotEchoWholeFileAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddTopLevelTypeAsync(tools, workspace);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.DryRun, Is.False);
            Assert.That(summary.ChangedContent, Is.Null, "an applied addTopLevelType must not echo the whole file");
            Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.Note, Does.Contain("ReadFile"));
            Assert.That(result.LargeResult, Is.Null);
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("class AddedTopLevelType"), "the type must have landed");
    }

    [Test]
    public async Task Member_AddMember_NoAutoStageStillReturnsChangedContentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddMemberAsync(tools, workspace, autoStage: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(summary.Status, Is.EqualTo("not_written"));
            Assert.That(summary.ChangedContent, Is.Not.Null.And.Not.Empty, "nothing was written, so the content is the result");
            Assert.That(summary.ChangedContent!.Values.Single(), Does.Contain("public int Added() => 42;"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "autoStage=false must not write");
    }
}
