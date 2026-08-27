using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class MassiveRefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;
    private SentinelRefactoringTools _refactoringTools;
    private SentinelAdvancedRefactoringTools _advancedRefactoringTools;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);

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

        _refactoringTools = new SentinelRefactoringTools(_refactoringEngine,
            standard,
            mapping,
            semLib,
            granular,
            sr,
            style,
            codeFlow,
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()),
            config,
            NullLogger<SentinelRefactoringTools>.Instance);

        // ExtractMembers / MoveType moved to SentinelAdvancedRefactoringTools in the server split.
        _advancedRefactoringTools = new SentinelAdvancedRefactoringTools(
            _refactoringEngine, standard, advStruct, mapping, semLib, granular,
            advLogic, refinement, advType, style, codeFlow,
            advRefactoring, logicOpt, modernization,
            new OutParamRefactoringEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, new DiffEngine()),
            config,
            NullLogger<SentinelAdvancedRefactoringTools>.Instance);
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
        var result = await _advancedRefactoringTools.ExtractMembers($"C{id}.cs", $"C{id}", "interface", $"IC{id}", autoStage: false);

        // With autoStage:false the tool returns Data = new { Changes = Dictionary<FilePath, string> }.
        Assert.That(result.Success, Is.True, result.Error?.Message);
        var changes = result.Data!.GetType().GetProperty("Changes")!.GetValue(result.Data)
            as Dictionary<FilePath, string>;
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

        // RenameSymbol takes a SymbolHandle (sessionId, projectName, docCommentId) — resolve it
        // via SymbolNavigationEngine first, as an agent would via LocateSymbol.
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var handle = (await symbolNavEngine.LocateSymbolAsync($"OldM{id}")).Single();

        var result = await _refactoringTools.RenameSymbol(
            handle.ProjectName, handle.DocCommentId!, $"NewM{id}", _workspaceManager.SessionId.ToString());
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
        var result = await _advancedRefactoringTools.MoveType($"C{id}.cs", $"D{id}", "ownFile", autoStage: false);

        // Dictionary keys are FilePath, not string, since the server split.
        Assert.That(result.Success, Is.True, result.Error?.Message);
        var data = (Dictionary<FilePath, string>?)result.Data;
        Assert.That(data?.Count, Is.GreaterThan(1));
    }
}
