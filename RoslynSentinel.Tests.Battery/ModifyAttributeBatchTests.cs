using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class ModifyAttributeBatchTests
{
    // Added by AddMember (expected - used for diagnostics)
    private const string FixtureRelativePath = "ContosoOrders.Core/AttributeBatchFixture.cs";


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    [Obsolete]
    public class AttributeBatchTargetA
    {
    }

    [Obsolete]
    public class AttributeBatchTargetB
    {
    }
    """;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string SecondFixtureRelativePath = "ContosoOrders.Core/AttributeBatchFixtureSecond.cs";


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string SecondFixtureSource = """
    namespace ContosoOrders.Core;

    public class AttributeBatchTargetC
    {
    }
    """;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private static SentinelRefactoringTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new SentinelRefactoringTools(
            new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, workspaceManager, config),
            //new StandardRefactoringEngine(workspaceManager),
            new MappingEngine(workspaceManager),
            //new SemanticRefactoringLibrary(workspaceManager),
            //new GranularRefactoringEngine(workspaceManager),
            new StructuralRefinementEngine(workspaceManager, config),
            //new CodeStyleEngine(workspaceManager, config),
            //new CodeFlowEngine(workspaceManager),
            new MsToolAugmentEngine(workspaceManager),
            //new CodeGenerationEngine(workspaceManager),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine),
            config,
            NullLogger<SentinelRefactoringTools>.Instance);
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test same file two edits",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.remove },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetB", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.replace, NewAttribute = "Obsolete(\"v2\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorDetails?.Message);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Not.Contain("[Obsolete]\r\npublic class AttributeBatchTargetA"));
            Assert.That(newContent, Does.Contain("Obsolete(\"v2\")"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource, reloadSolution: false);
        await fixture.AddFileToSolution(workspaceManager, SecondFixtureRelativePath, SecondFixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test across two files",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            new AttributeEdit { FilePath = SecondFixtureRelativePath, TargetName = "AttributeBatchTargetC", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorDetails?.Message);

        var firstContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        var secondContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, SecondFixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(firstContent, Does.Contain("[Serializable]"));
            Assert.That(secondContent, Does.Contain("[Serializable]"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyAttribute(
            reason: "batch test same node collision",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyAttribute(
            reason: "batch test target not found",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetDoesNotExist", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test both supplied",
            filepath: FixtureRelativePath,
            targetName: "AttributeBatchTargetA",
            existingAttribute: "Obsolete",
            action: AttributeModifyAction.remove,
            edits: [new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetB", Action = AttributeModifyAction.add, NewAttribute = "Serializable" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails!.Message, Does.Contain("not both"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails!.Message, Does.Contain("edits"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test empty edits array",
            edits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails!.Message, Does.Contain("empty"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyAttribute_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new AttributeEdit { FilePath = FixtureRelativePath, TargetName = $"NonexistentType{i}", Action = AttributeModifyAction.add, NewAttribute = "Serializable" })
            .ToList();

        var result = await tools.ModifyAttribute(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorDetails!.Message, Does.Contain("20"));
    }
}
