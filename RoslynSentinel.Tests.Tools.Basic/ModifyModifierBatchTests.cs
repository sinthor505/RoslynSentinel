using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// About ModifyModifier's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk
// write). See ModifyAttributeBatchTests for the pattern.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ModifyModifierBatchTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/ModifierBatchFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class ModifierBatchTargetA
    {
        void MethodOne() { }
    }

    public class ModifierBatchTargetB
    {
        void MethodTwo() { }
    }
    """;

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

    [Test]
    public async Task ModifyModifier_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifier(
            reason: "batch test same file two edits",
            edits:
            [
                new ModifierEdit { FilePath = path, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = path, TargetName = "MethodTwo", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var newContent = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Contain("static void MethodOne"));
            Assert.That(newContent, Does.Contain("static void MethodTwo"));
        });
    }

    [Test]
    public async Task ModifyModifier_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        const string secondRelativePath = "ContosoOrders.Core/ModifierBatchFixtureSecond.cs";
        const string secondSource = """
        namespace ContosoOrders.Core;

        public class ModifierBatchTargetC
        {
            void MethodThree() { }
        }
        """;
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource), (secondRelativePath, secondSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyModifier(
            reason: "batch test across two files",
            edits:
            [
                new ModifierEdit { FilePath = workspace.PathOf(FixtureRelativePath), TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = workspace.PathOf(secondRelativePath), TargetName = "MethodThree", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("static void MethodOne"));
            Assert.That(workspace.ReadText(secondRelativePath), Does.Contain("static void MethodThree"));
        });
    }

    [Test]
    public async Task ModifyModifier_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);
        var originalContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyModifier(
            reason: "batch test same node collision",
            edits:
            [
                new ModifierEdit { FilePath = path, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = path, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(originalContent), "a same-node collision must not write anything");
    }

    [Test]
    public async Task ModifyModifier_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);
        var originalContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyModifier(
            reason: "batch test one edit not found",
            edits:
            [
                new ModifierEdit { FilePath = path, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
                new ModifierEdit { FilePath = path, TargetName = "MethodDoesNotExist", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(originalContent),
            "one unresolvable edit in a batch must roll back the whole batch, not partially apply it");
    }

    [Test]
    public async Task ModifyModifier_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyModifier(
            reason: "batch test both supplied",
            filePath: path,
            targetName: "MethodOne",
            modifier: NonAccessibilityModifier.@static,
            action: AddRemoveAction.add,
            edits: [new ModifierEdit { FilePath = path, TargetName = "MethodTwo", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ModifyModifier_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyModifier(reason: "batch test neither supplied", dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("required"));
    }

    [Test]
    public async Task ModifyModifier_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyModifier(reason: "batch test empty edits", edits: [], dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }

    [Test]
    public async Task ModifyModifier_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new ModifierEdit { FilePath = path, TargetName = $"NonexistentMethod{i}", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add })
            .ToList();

        var result = await tools.ModifyModifier(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }
}
