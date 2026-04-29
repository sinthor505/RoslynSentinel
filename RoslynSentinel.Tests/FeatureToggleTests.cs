#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class FeatureToggleTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ProjectStructureEngine _structureEngine;
    private AnalysisEngine _analysisEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _structureEngine = new ProjectStructureEngine(_workspaceManager, _config);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("Test.cs", source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task FindStructuralSmells_ShouldRespectDisabledToggle()
    {
        // Arrange - File with MultiType smell
        SetSource("public class A {} public class B {}");
        
        // Act 1: Enabled (Default)
        var result1 = await _structureEngine.FindStructuralSmellsAsync();
        Assert.That(result1.Any(s => s.Contains("[MULTI_TYPE]")), Is.True);

        // Act 2: Disable
        _config.SetFeatureStatus("MultiTypeFile", false);
        var result2 = await _structureEngine.FindStructuralSmellsAsync();

        // Assert
        Assert.That(result2.Any(s => s.Contains("[MULTI_TYPE]")), Is.False, "MultiType should be skipped when disabled.");
    }

    [Test]
    public async Task FindBoxingAllocations_ShouldRespectDisabledToggle()
    {
        // Arrange - Boxing allocation
        SetSource("public class C { object o = 1; }");

        // Act 1: Enabled
        var result1 = await _analysisEngine.FindBoxingAllocationsAsync();
        Assert.That(result1.Count, Is.GreaterThan(0));

        // Act 2: Disable
        _config.SetFeatureStatus("BoxingAllocation", false);
        var result2 = await _analysisEngine.FindBoxingAllocationsAsync();

        // Assert
        Assert.That(result2.Count, Is.EqualTo(0), "Boxing should be skipped when disabled.");
    }

    [Test]
    public void BatchUpdate_ShouldWork()
    {
        // Arrange
        var updates = new List<KeyValuePair<string, bool>>
        {
            new("MultiTypeFile", false),
            new("BoxingAllocation", false)
        };

        // Act
        _config.BatchUpdateFeatureStatus(updates);

        // Assert
        Assert.That(_config.IsFeatureEnabled("MultiTypeFile"), Is.False);
        Assert.That(_config.IsFeatureEnabled("BoxingAllocation"), Is.False);
    }
}
