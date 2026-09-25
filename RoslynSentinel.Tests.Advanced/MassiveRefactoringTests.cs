using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class MassiveRefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;
    private RefactoringStructuralTools _refactoringStructuralTools;
    private RefactoringSignatureTools _refactoringSignatureTools;
    private AdvancedRefactoringTools _advancedRefactoringTools;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _refactoringEngine = new RefactoringEngine(_workspaceManager, NullLogger<RefactoringEngine>.Instance, config);

        var sr = new StructuralRefinementEngine(_workspaceManager, config);
        var standard = new StandardRefactoringEngine(_workspaceManager);
        var advStruct = new AdvancedStructuralEngine(_workspaceManager);
        var mapping = new MappingEngine(_workspaceManager);
        var semLib = new SemanticRefactoringLibrary(_workspaceManager);
        var granular = new GranularRefactoringEngine(_workspaceManager);
        var advLogic = new AdvancedLogicEngine(_workspaceManager);
        var refinement = new RefinementEngine(_workspaceManager);
        var advType = new AdvancedTypeEngine(_workspaceManager);
        var style = new CodeStyleEngine(_workspaceManager, config);
        var codeFlow = new CodeFlowEngine(_workspaceManager);
        var advRefactoring = new AdvancedRefactoringEngine(_workspaceManager);
        var logicOpt = new LogicOptimizationEngine(_workspaceManager);
        var modernization = new ModernizationEngine(_workspaceManager, config);

        _refactoringStructuralTools = new RefactoringStructuralTools(new RefactoringStructuralImpl(
            _refactoringEngine,
            sr,
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
        _refactoringSignatureTools = new RefactoringSignatureTools(new RefactoringSignatureImpl(
            _refactoringEngine,
            _workspaceManager,
            new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            NullLogger<RefactoringSignatureImpl>.Instance));

        // ExtractMembers / MoveType moved to AdvancedRefactoringTools in the server split.
        _advancedRefactoringTools = new AdvancedRefactoringTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task ExtractInterface_ShouldCreateInterface(int id)
    {
        SetSource($"public class C{id} {{ public void M{id}() {{}} }}", $"C{id}.cs");
        var result = await _advancedRefactoringTools.ExtractMembers(reason: "test message", $"C{id}.cs", $"C{id}", ExtractAsType.@interface, $"IC{id}", autoStage: false);

        // With autoStage:false the tool returns SuccessDetails = AppliedChangeSummary { ChangedContent = Dictionary<FilePathWrapper, string> }.
        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
        var changes = ((AppliedChangeSummary)result.SuccessData!).ChangedContent;
        Assert.That(changes, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task RenameSymbol_ShouldUpdateReferences(int id)
    {
        var source = $"public class C{id} {{ public void OldM{id}() {{}} public void U() {{ OldM{id}(); }} }}";
        SetSource(source, $"C{id}.cs");

        // RenameSymbol takes a SymbolHandle (projectName, docCommentId) -> resolve it
        // via SymbolNavigationEngine first, as an agent would via LocateSymbol.
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var handle = (await symbolNavEngine.LocateSymbolAsync($"OldM{id}")).Single();

        var result = await _refactoringSignatureTools.RenameSymbol(
            reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!,
            newName: $"NewM{id}");
        Assert.That(result.IsSuccess, Is.True);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.That(json, Contains.Substring($"NewM{id}"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task MoveTypeToFile_ShouldSeparateTypes(int id)
    {
        SetSource($"public class C{id} {{}} public class D{id} {{}}", $"C{id}.cs");
        var result = await _advancedRefactoringTools.MoveType(reason: "test message", $"C{id}.cs", $"D{id}", "ownFile", autoStage: false);

        // Dictionary keys are FilePathWrapper, not string, since the server split.
        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
        var data = result.SuccessData?.ChangedContent;
        Assert.That(data?.Count, Is.GreaterThan(1));
    }
}
