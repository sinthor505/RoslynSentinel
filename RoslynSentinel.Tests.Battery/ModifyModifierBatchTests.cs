using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class ModifyModifierBatchTests
{
    // Added by AddMember (expected - used for diagnostics)
    private const string FixtureRelativePath = "ContosoOrders.Core/ModifierBatchFixture.cs";


    // Added by InsertMemberAfter (expected - used for diagnostics)
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


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringStructuralTools(
            new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, workspaceManager, config),
            new StructuralRefinementEngine(workspaceManager, config),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine),
            NullLogger<RefactoringStructuralTools>.Instance);
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyModifier(
            reason: "batch test same file two edits",
            edits:
            [
                new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodTwo", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Contain("static void MethodOne"));
            Assert.That(newContent, Does.Contain("static void MethodTwo"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);

        const string secondRelativePath = "ContosoOrders.Core/ModifierBatchFixtureSecond.cs";
        const string secondSource = """
        namespace ContosoOrders.Core;

        public class ModifierBatchTargetC
        {
            void MethodThree() { }
        }
        """;
        await fixture.AddFileToSolution(workspaceManager, secondRelativePath, secondSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyModifier(
            reason: "batch test across two files",
            edits:
            [
                new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = secondRelativePath, TargetName = "MethodThree", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var contentA = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        var contentB = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, secondRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(contentA, Does.Contain("static void MethodOne"));
            Assert.That(contentB, Does.Contain("static void MethodThree"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);
        var originalContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyModifier(
            reason: "batch test same node collision",
            edits:
            [
                new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(newContent, Is.EqualTo(originalContent), "a same-node collision must not write anything");
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);
        var originalContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyModifier(
            reason: "batch test one edit not found",
            edits:
            [
                new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodDoesNotExist", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(newContent, Is.EqualTo(originalContent),
            "one unresolvable edit in a batch must roll back the whole batch, not partially apply it");
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyModifier(
            reason: "batch test both supplied",
            filepath: FixtureRelativePath,
            targetName: "MethodOne",
            modifier: NonAccessibilityModifier.@static,
            action: AddRemoveAction.add,
            edits: [new ModifierEdit { FilePath = FixtureRelativePath, TargetName = "MethodTwo", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyModifier(reason: "batch test neither supplied", dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("required"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyModifier(reason: "batch test empty edits", edits: [], dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyModifier_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new ModifierEdit { FilePath = FixtureRelativePath, TargetName = $"NonexistentMethod{i}", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add })
            .ToList();

        var result = await tools.ModifyModifier(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }
}
