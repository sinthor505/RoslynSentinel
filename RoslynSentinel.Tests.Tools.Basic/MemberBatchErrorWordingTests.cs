using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// The batch parameter of ModifyModifierTool, ModifyAttributeTool and ModifyBaseTypeTool is named 'batchEdits'. These tests pin that
// the user-visible error text names it correctly ('batchEdits[n]'), not a stale 'edits[n]', and carries no stray
// internal identifier (an earlier find/replace accident injected '_symbolNavigationEngine.' into the collision message).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class MemberBatchErrorWordingTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/BatchWordingFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public interface IWordingMarker
    {
    }

    [Obsolete]
    public class WordingTarget
    {
        void WordingMethod() { }
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    private static void AssertBatchWording(string? message, params string[] expectedIndexNames)
    {
        Assert.That(message, Is.Not.Null.And.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var indexName in expectedIndexNames)
            {
                Assert.That(message, Does.Contain(indexName), "message should name the parameter 'batchEdits': " + message);
            }

            Assert.That(message, Does.Not.Contain("_symbolNavigationEngine"), "an internal identifier leaked into the message: " + message);
            Assert.That(Regex.IsMatch(message!, @"(?<!batch)edits\["), Is.False, "message still uses the stale 'edits[' name: " + message);
        });
    }

    [Test]
    public async Task ModifyModifier_BatchTwoEditsSameTarget_ErrorUsesBatchEditsNameAndNoInternalIdentifierAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifierTool(
            reason: "wording test same target collision",
            batchEdits:
            [
                new ModifierEdit { FilePath = path, TargetName = "WordingMethod", Modifier = NonAccessibilityModifier.@sealed, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = path, TargetName = "WordingMethod", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[0]", "batchEdits[1]");
    }

    [Test]
    public async Task ModifyAttribute_BatchTwoEditsSameTarget_ErrorUsesBatchEditsNameAndNoInternalIdentifierAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "wording test same target collision",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "WordingTarget", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
                new AttributeEdit { FilePath = path, TargetName = "WordingTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[0]", "batchEdits[1]");
    }

    [Test]
    public async Task ModifyBaseType_BatchTwoEditsSameTarget_ErrorUsesBatchEditsNameAndNoInternalIdentifierAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseTypeTool(
            reason: "wording test same target collision",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "WordingTarget", BaseTypeName = "IWordingMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "WordingTarget", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[0]", "batchEdits[1]");
    }

    [Test]
    public async Task ModifyModifier_BatchSecondEditTargetNotFound_ErrorNamesBatchEditsIndexAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifierTool(
            reason: "wording test target not found",
            batchEdits:
            [
                new ModifierEdit { FilePath = path, TargetName = "WordingMethod", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = path, TargetName = "WordingMethodDoesNotExist", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[1]");
    }

    [Test]
    public async Task ModifyAttribute_BatchSecondEditTargetNotFound_ErrorNamesBatchEditsIndexAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "wording test target not found",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "WordingTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "WordingTargetDoesNotExist", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[1]");
    }

    private const string OverloadedFixtureSource = """
    namespace ContosoOrders.Core;

    public class OverloadHolder
    {
        public void Run(int x) { }
        public void Run(string s) { }
    }
    """;

    [Test]
    public async Task ModifyModifier_BatchTargetNotFound_ErrorCodeIsNotFoundAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifierTool(
            reason: "error code test target not found",
            batchEdits:
            [
                new ModifierEdit { FilePath = path, TargetName = "WordingMethodDoesNotExist", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyModifier_BatchAmbiguousSnippet_ErrorCodeIsAmbiguousAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, OverloadedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifierTool(
            reason: "error code test ambiguous snippet",
            batchEdits:
            [
                new ModifierEdit { FilePath = path, TargetName = "Run", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add, ContextSnippet = "public void Run(" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous), result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyModifier_BatchFailuresDisagreeOnKind_ErrorCodeFallsBackToInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, OverloadedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifierTool(
            reason: "error code test mixed failures",
            batchEdits:
            [
                new ModifierEdit { FilePath = path, TargetName = "Run", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add, ContextSnippet = "public void Run(double d)" },
                new ModifierEdit { FilePath = path, TargetName = "Run", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add, ContextSnippet = "public void Run(" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument), result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyAttribute_BatchTargetNotFound_ErrorCodeIsNotFoundAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "error code test target not found",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "WordingTargetDoesNotExist", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyBaseType_BatchTypeNotFound_ErrorCodeIsNotFoundAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseTypeTool(
            reason: "error code test type not found",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "WordingTargetDoesNotExist", BaseTypeName = "IWordingMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyBaseType_BatchSecondEditTargetNotFound_ErrorNamesBatchEditsIndexAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseTypeTool(
            reason: "wording test target not found",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "WordingTarget", BaseTypeName = "IWordingMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "WordingTargetDoesNotExist", BaseTypeName = "IWordingMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        AssertBatchWording(result.ErrorData?.Message, "batchEdits[1]");
    }
}
