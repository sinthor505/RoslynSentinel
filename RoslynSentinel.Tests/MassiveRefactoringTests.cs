using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class MassiveRefactoringTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;
    private SentinelRefactoringTools _refactoringTools;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager);
        
        var sr = new StructuralRefinementEngine(_workspaceManager);
        var standard = new StandardRefactoringEngine(_workspaceManager);
        var advStruct = new AdvancedStructuralEngine(_workspaceManager);
        var mapping = new MappingEngine(_workspaceManager);
        var semLib = new SemanticRefactoringLibrary(_workspaceManager);
        var granular = new GranularRefactoringEngine(_workspaceManager);
        var advLogic = new AdvancedLogicEngine(_workspaceManager);
        var refinement = new RefinementEngine(_workspaceManager);
        var advType = new AdvancedTypeEngine(_workspaceManager);
        var style = new CodeStyleEngine(_workspaceManager, config);
        
        _refactoringTools = new SentinelRefactoringTools(_refactoringEngine, standard, advStruct, mapping, semLib, granular, advLogic, refinement, advType, sr, style, _workspaceManager, config, NullLogger<SentinelRefactoringTools>.Instance);
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
        var result = (Dictionary<string, string>)await _refactoringTools.ExtractInterface($"C{id}.cs", $"C{id}", $"IC{id}", autoStage: false);
        Assert.That(result.Count, Is.GreaterThan(0));
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
        
        int index = source.IndexOf($"OldM{id}") + 1;
        var result = (Dictionary<string, string>)await _refactoringTools.RenameSymbol($"C{id}.cs", 1, index, $"NewM{id}", autoStage: false);
        Assert.That(result.Values.Any(v => v.Contains($"NewM{id}")), Is.True);
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
        var result = (Dictionary<string, string>)await _refactoringTools.MoveTypeToFile($"C{id}.cs", $"D{id}", autoStage: false);
        Assert.That(result.Count, Is.GreaterThan(1));
    }
}
