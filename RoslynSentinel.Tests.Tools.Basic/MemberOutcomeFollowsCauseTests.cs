using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Tool-level regression tests for plan_error_codes_follow_cause slice A: the tool result follows the engine's
/// real outcome. A failure is IsError with the outcome-derived code and writes nothing; AlreadyInTargetState is
/// a success. Runs on an InMemoryWorkspace, so the file on disk is the workspace text.
/// autoStage is exposed only on the Impl classes, so the autoStage:false cases call them directly.
/// </summary>
[TestFixture]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class MemberOutcomeFollowsCauseTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberOutcomeFollowsCauseFixture.cs";

    private const string FixtureSource =
        "namespace ContosoOrders.Core;\n\npublic enum OrderState\n{\n    Pending,\n    Shipped\n}\n\npublic class OrderTarget\n{\n    public int Existing() => 1;\n}\n";

    private static RefactoringStructuralImpl BuildStructuralImpl(IWorkspaceManager workspaceManager)
    {
        var config = TestEngines.SentinelConfiguration();
        var diffEngine = TestEngines.DiffEngine();
        return new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), config),
            TestEngines.StructuralRefinementEngine(workspaceManager, config),
            TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance);
    }

    private static RefactoringExtractionDocsImpl BuildExtractionDocsImpl(IWorkspaceManager workspaceManager)
    {
        var config = TestEngines.SentinelConfiguration();
        var diffEngine = TestEngines.DiffEngine();
        return new RefactoringExtractionDocsImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MsToolAugmentEngine(workspaceManager),
            TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringExtractionDocsImpl>.Instance);
    }

    [Test]
    public async Task Member_AddTopLevelType_NonTypeSourceWithoutAutoStage_ReturnsInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var impl = BuildStructuralImpl(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await impl.Member(
            reason: "test addTopLevelType non-type source outcome",
            filePath: workspace.PathOf(FixtureRelativePath),
            operation: MemberAction.addTopLevelType,
            newMemberSource: "public int Value => 7;",
            autoStage: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a SourceInvalid engine outcome must be an error even with autoStage false");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a failed add must not write");
    }

    [Test]
    public async Task Member_Replace_EnumMemberIdenticalSource_IsSuccessAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = new RefactoringStructuralTools(BuildStructuralImpl(workspace.Manager));
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test enum replace identical source outcome",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Pending",
            newMemberSource: "Pending");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "an already-in-state replace must not write");
    }

    [Test]
    public async Task Member_Replace_EnumMemberInvalidValue_ReturnsInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = new RefactoringStructuralTools(BuildStructuralImpl(workspace.Manager));
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test enum replace invalid value outcome",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Pending",
            newMemberSource: "Pending = notanint");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a CannotEdit engine outcome must be an error, not a success");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a failed replace must not write");
    }

    [Test]
    public async Task UsingDirective_AddUnknownFileWithoutAutoStage_ReturnsNotFoundAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var impl = BuildExtractionDocsImpl(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await impl.UsingDirective(
            reason: "test using add unknown file outcome",
            filePath: workspace.PathOf("ContosoOrders.Core/MissingFile.cs"),
            operation: AddRemoveViewAction.add,
            namespaceName: "System.Text",
            autoStage: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a DocumentNotFound engine outcome must be an error even with autoStage false");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "the existing fixture must be untouched");
    }

    [Test]
    public async Task Member_Replace_EnumMemberInvalidValue_ErrorPointsAtModifyEnumAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = new RefactoringStructuralTools(BuildStructuralImpl(workspace.Manager));

        var result = await tools.MemberTool(
            reason: "test enum replace invalid value recovery hint",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Pending",
            newMemberSource: "Pending = notanint");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an invalid enum value must be an error");
            Assert.That(result.ErrorData?.Message, Does.Contain("ModifyEnum(reason:"), "the recovery hint must name ModifyEnum");
            Assert.That(result.ErrorData?.Message, Does.Contain("enumName: "), "the recovery hint must name the enumName argument");
            Assert.That(result.ErrorData?.Message, Does.Contain("OrderState"), "the recovery hint must name the enum");
        });
    }

    [Test]
    public async Task Member_Replace_MethodIdenticalSource_IsSuccessAndWritesNothingAsync()
    {
        const string methodFixture = "namespace ContosoOrders.Core;\n\npublic class OrderTarget\n{\n    public int Existing() => 1;\n}\n";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, methodFixture));
        var tools = new RefactoringStructuralTools(BuildStructuralImpl(workspace.Manager));
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test method replace identical source outcome",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Existing",
            newMemberSource: "public int Existing() => 1;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "an already-in-state replace must not write");
    }

    [Test]
    public async Task Member_AddMemberAfterUnparsableSource_ReturnsInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = new RefactoringStructuralTools(BuildStructuralImpl(workspace.Manager));
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test addMember unparsable source outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "OrderTarget",
            newMemberSource: "public int Broken( =>",
            position: "after:Existing");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an unparsable member source must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a failed add must not write");
    }
}
