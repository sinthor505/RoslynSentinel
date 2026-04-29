using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class MassiveStructuralTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ProjectStructureEngine _structureEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _structureEngine = new ProjectStructureEngine(_workspaceManager, new SentinelConfiguration());
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
    public async Task StructuralSmells_MultiTypeFile(int id)
    {
        SetSource($"public class C{id} {{}} public class D{id} {{}}", $"C{id}.cs");
        var smells = await _structureEngine.FindStructuralSmellsAsync();
        Assert.That(smells, Has.Some.Contains("[MULTI_TYPE]"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    public async Task StructuralSmells_NameMismatch(int id)
    {
        SetSource($"public class WrongName{id} {{}}", $"RightName{id}.cs");
        var smells = await _structureEngine.FindStructuralSmellsAsync();
        Assert.That(smells, Has.Some.Contains("[NAME_MISMATCH]"));
    }

    [Test]
    public async Task StructuralSmells_NestedTypes()
    {
        SetSource("public class Outer { public class Inner {} }", "Outer.cs");
        var smells = await _structureEngine.FindStructuralSmellsAsync();
        // Since Nested types aren't explicitly flagged in the code I read (only MultiType and NameMismatch), 
        // I'll check for MultiType if they are counted as separate types.
        Assert.That(smells, Has.Some.Contains("[MULTI_TYPE]"));
    }
}
