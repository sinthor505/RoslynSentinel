using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// The batch parameter of ModifyModifier, ModifyAttribute and ModifyBaseType is named 'batchEdits'. These tests pin that
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
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine, config),
            new StructuralRefinementEngine(workspaceManager, config),
            symbolNavigationEngine,
            workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance));
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

        var result = await tools.ModifyModifier(
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

        var result = await tools.ModifyAttribute(
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

        var result = await tools.ModifyBaseType(
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

        var result = await tools.ModifyModifier(
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

        var result = await tools.ModifyAttribute(
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

    [Test]
    public async Task ModifyBaseType_BatchSecondEditTargetNotFound_ErrorNamesBatchEditsIndexAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
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
