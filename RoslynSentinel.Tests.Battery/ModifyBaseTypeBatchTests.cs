using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class ModifyBaseTypeBatchTests
{
    // Added by AddMember (expected - used for diagnostics)
    private const string FixtureRelativePath = "ContosoOrders.Core/BaseTypeBatchFixture.cs";


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public interface IBaseTypeBatchMarker
    {
    }

    public class BaseTypeBatchTargetA
    {
    }

    public class BaseTypeBatchTargetB : IBaseTypeBatchMarker
    {
    }
    """;


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string SecondFixtureRelativePath = "ContosoOrders.Core/BaseTypeBatchFixtureSecond.cs";


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private const string SecondFixtureSource = """
    namespace ContosoOrders.Core;

    public class BaseTypeBatchTargetC
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
            new StandardRefactoringEngine(workspaceManager),
            new MappingEngine(workspaceManager),
            new SemanticRefactoringLibrary(workspaceManager),
            new GranularRefactoringEngine(workspaceManager),
            new StructuralRefinementEngine(workspaceManager, config),
            new CodeStyleEngine(workspaceManager, config),
            new CodeFlowEngine(workspaceManager),
            new MsToolAugmentEngine(workspaceManager),
            new CodeGenerationEngine(workspaceManager),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, workspaceManager, diffEngine),
            config,
            NullLogger<SentinelRefactoringTools>.Instance);
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyBaseType(
            reason: "batch test same file two edits",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.True, result.Error?.Message);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Match(@"BaseTypeBatchTargetA\s*:\s*IBaseTypeBatchMarker"));
            Assert.That(newContent, Does.Not.Match(@"BaseTypeBatchTargetB\s*:\s*IBaseTypeBatchMarker"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource, reloadSolution: false);
        await fixture.AddFileToSolution(workspaceManager, SecondFixtureRelativePath, SecondFixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyBaseType(
            reason: "batch test across two files",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = SecondFixtureRelativePath, TypeName = "BaseTypeBatchTargetC", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.True, result.Error?.Message);

        var firstContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        var secondContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, SecondFixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(firstContent, Does.Match(@"BaseTypeBatchTargetA\s*:\s*IBaseTypeBatchMarker"));
            Assert.That(secondContent, Does.Match(@"BaseTypeBatchTargetC\s*:\s*IBaseTypeBatchMarker"));
        });
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyBaseType(
            reason: "batch test same node collision",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyBaseType(
            reason: "batch test target not found",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetDoesNotExist", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyBaseType(
            reason: "batch test both supplied",
            filepath: FixtureRelativePath,
            typeName: "BaseTypeBatchTargetA",
            baseTypeName: "IBaseTypeBatchMarker",
            action: AddRemoveAction.add,
            edits: [new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("not both"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyBaseType(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("edits"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyBaseType(
            reason: "batch test empty edits array",
            edits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("empty"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = $"NonexistentType{i}", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add })
            .ToList();

        var result = await tools.ModifyBaseType(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("20"));
    }
}
