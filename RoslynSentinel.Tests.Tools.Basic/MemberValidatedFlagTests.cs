using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member replace and remove run the compile gate (ValidateAndApplyAsync) before they write, so a real apply
/// must report AppliedChangeSummary.Validated true. The flag used to be hardcoded false on these paths even
/// though the gate ran. addMember already reported true and is pinned here so it stays that way.
/// Real applies only: dry runs also run the gate and are not asserted here.
/// About the tool's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild
/// load or disk write). See MemberSingleDeclarationTests for the pattern.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class MemberValidatedFlagTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberValidatedFlagFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class ValidatedFlagTarget
    {
        public int Alpha() => 1;

        public int Beta() => 2;
    }

    public enum ValidatedFlagColor
    {
        Red,
        Green
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    [Test]
    public async Task Replace_Method_ReportsValidatedTrueAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test validated flag on method replace",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Alpha",
            newMemberSource: "public int Alpha() => 10;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((AppliedChangeSummary)result.SuccessData!).Validated, Is.True, "A real replace ran the compile gate, so it is validated.");
    }

    [Test]
    public async Task Remove_MethodWithNoUsages_ReportsValidatedTrueAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test validated flag on method remove",
            operation: MemberAction.remove,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Beta",
            containerName: "ValidatedFlagTarget");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((AppliedChangeSummary)result.SuccessData!).Validated, Is.True, "A real remove ran the compile gate, so it is validated.");
    }

    [Test]
    public async Task Replace_EnumMember_ReportsValidatedTrueAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test validated flag on enum member replace",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Green",
            containerName: "ValidatedFlagColor",
            newMemberSource: "Green = 5");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((AppliedChangeSummary)result.SuccessData!).Validated, Is.True, "A real enum member replace ran the compile gate, so it is validated.");
    }

    [Test]
    public async Task Remove_EnumMember_ReportsValidatedTrueAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test validated flag on enum member remove",
            operation: MemberAction.remove,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Red",
            containerName: "ValidatedFlagColor");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((AppliedChangeSummary)result.SuccessData!).Validated, Is.True, "A real enum member remove ran the compile gate, so it is validated.");
    }

    [Test]
    public async Task AddMember_ReportsValidatedTrueAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test validated flag stays true on addMember",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "ValidatedFlagTarget",
            newMemberSource: "public int Gamma() => 3;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((AppliedChangeSummary)result.SuccessData!).Validated, Is.True, "addMember with staging ran the compile gate, so it is validated.");
    }
}
