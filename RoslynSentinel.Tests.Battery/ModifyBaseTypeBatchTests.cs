using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]

public class ModifyBaseTypeBatchTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private DiffEngine _diffEngine;
    private ValidationEngine _validationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private RefactoringStructuralTools _refactoringStructuralTools;
    private MsToolAugmentEngine _msToolAugmentEngine;

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

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(_workspaceManager, NullLogger<RefactoringEngine>.Instance, _config);
        _diffEngine = new DiffEngine();
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _validationEngine = new ValidationEngine(_workspaceManager, _diffEngine, NullLogger<ValidationEngine>.Instance);
        _msToolAugmentEngine = new MsToolAugmentEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(
            _workspaceManager);
        _refactoringStructuralTools = new RefactoringStructuralTools(
            _refactoringEngine,
            _structuralRefinementEngine,
            _symbolNavigationEngine,
            _workspaceManager,
            _validationEngine,
            NullLogger<RefactoringStructuralTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var fixture = new TestSolutionFixture();
        // Load into the class-field _workspaceManager, not a disconnected local instance:
        // _refactoringStructuralTools (built in Setup()) holds a reference to _workspaceManager,
        // so loading the fixture anywhere else leaves it seeing an empty workspace ("no solution
        // is loaded") even though a solution was loaded - just not into the instance that matters.
        await fixture.AddFileToSolution(_workspaceManager, FixtureRelativePath, FixtureSource);

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test same file two edits",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

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
        // See the sibling same-file batch test above for why this loads into the class-field
        // _workspaceManager rather than a fresh local instance.
        await fixture.AddFileToSolution(_workspaceManager, FixtureRelativePath, FixtureSource, reloadSolution: false);
        await fixture.AddFileToSolution(_workspaceManager, SecondFixtureRelativePath, SecondFixtureSource);

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test across two files",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = SecondFixtureRelativePath, TypeName = "BaseTypeBatchTargetC", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

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

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test same node collision",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

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

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test target not found",
            edits:
            [
                new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetDoesNotExist", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

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

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test both supplied",
            filePath: FixtureRelativePath,
            typeName: "BaseTypeBatchTargetA",
            baseTypeName: "IBaseTypeBatchMarker",
            action: AddRemoveAction.add,
            edits: [new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("edits"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);

        var result = await _refactoringStructuralTools.ModifyBaseType(
            reason: "batch test empty edits array",
            edits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task ModifyBaseType_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new BaseTypeEdit { FilePath = FixtureRelativePath, TypeName = $"NonexistentType{i}", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add })
            .ToList();

        var result = await _refactoringStructuralTools.ModifyBaseType(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }
}
