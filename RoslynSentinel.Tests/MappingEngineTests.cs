using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

[TestFixture]
public class MappingEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MappingEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MappingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // GenerateMappingAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateMapping_MatchingProperties_GeneratesMappingMethod()
    {
        SetSource(@"
public class ProductDto
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}

public class ProductEntity
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}", "Models.cs");

        var result = await _engine.GenerateMappingAsync("Models.cs", "ProductDto", "ProductEntity");

        Assert.That(result, Does.Contain("MapProductDtoToProductEntity"),
            "Mapping method should be named Map{From}To{To}.");
        Assert.That(result, Does.Contain("dest.Name = source.Name"),
            "Should generate Name property assignment.");
        Assert.That(result, Does.Contain("dest.Price = source.Price"),
            "Should generate Price property assignment.");
    }

    [Test]
    public async Task GenerateMapping_MethodIsPublicStatic()
    {
        SetSource(@"
public class SourceDto { public string Title { get; set; } }
public class DestEntity { public string Title { get; set; } }
", "Mapping.cs");

        var result = await _engine.GenerateMappingAsync("Mapping.cs", "SourceDto", "DestEntity");

        Assert.That(result, Does.Contain("public"),
            "Mapping method should be public.");
        Assert.That(result, Does.Contain("static"),
            "Mapping method should be static.");
    }

    [Test]
    public async Task GenerateMapping_NonMatchingProperties_OnlyMapsMatchingOnes()
    {
        SetSource(@"
public class SourceModel
{
    public string Name { get; set; }
    public string SourceOnlyField { get; set; }
}

public class TargetModel
{
    public string Name { get; set; }
    public string TargetOnlyField { get; set; }
}", "Models.cs");

        var result = await _engine.GenerateMappingAsync("Models.cs", "SourceModel", "TargetModel");

        Assert.That(result, Does.Contain("dest.Name = source.Name"),
            "Matching property Name should be mapped.");
        Assert.That(result, Does.Not.Contain("SourceOnlyField"),
            "Source-only field should NOT appear in mapping output.");
        Assert.That(result, Does.Not.Contain("TargetOnlyField"),
            "Target-only field without a source match should not be assigned.");
    }

    // ══════════════════════════════════════════════════════════════
    // InvertAssignmentsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task InvertAssignments_SimpleAssignments_SwapsLeftAndRight()
    {
        // InvertAssignmentsAsync takes (filePath, startLine, endLine)
        // Source content: the assignments are at lines 5-6 (1-indexed)
        SetSource(@"public class Mapper
{
    public void Map(Dto src, Entity dest)
    {
        dest.Name = src.Name;
        dest.Price = src.Price;
    }
}", "Mapper.cs");

        var result = await _engine.InvertAssignmentsAsync("Mapper.cs", 5, 6);

        Assert.That(result, Does.Contain("src.Name = dest.Name"),
            "Inverted: left and right sides should be swapped for Name.");
        Assert.That(result, Does.Contain("src.Price = dest.Price"),
            "Inverted: left and right sides should be swapped for Price.");
    }

    [Test]
    public async Task InvertAssignments_SingleLine_InvertsThatLine()
    {
        SetSource(@"public class Syncer
{
    public void Sync(Local local, Remote remote)
    {
        remote.Value = local.Value;
    }
}", "Syncer.cs");

        var result = await _engine.InvertAssignmentsAsync("Syncer.cs", 5, 5);

        Assert.That(result, Does.Contain("local.Value = remote.Value"),
            "Single assignment should be inverted.");
    }

    [Test]
    public async Task InvertAssignments_NoAssignmentsInRange_ReturnsUnchanged()
    {
        SetSource(@"public class NoOp
{
    public int Compute(int a, int b)
    {
        return a + b;
    }
}", "NoOp.cs");

        // Lines 5-5 contain "return a + b;" — no assignment expressions
        var result = await _engine.InvertAssignmentsAsync("NoOp.cs", 5, 5);

        Assert.That(result, Does.Contain("return a + b"),
            "Source without assignments in range should be returned unchanged.");
    }
}
