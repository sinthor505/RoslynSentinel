// Battery #20 — SentinelWorkspaceTools
// Tests all 26 public methods of SentinelWorkspaceTools in-memory via TestSolutionBuilder.

using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryTwentyTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ValidationEngine _validationEngine;
    private DiffEngine _diffEngine;
    private DiagnosticEngine _diagnosticEngine;
    private SolutionManagementEngine _solutionManagementEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private DependencyEngine _dependencyEngine;
    private SentinelWorkspaceTools _tools;

    private const string SimpleSource = "namespace TestProj; public class Order { public int Id { get; set; } }";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _diffEngine = new DiffEngine(_workspaceManager);
        _validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _tools = new SentinelWorkspaceTools(
            _workspaceManager, _validationEngine, _diffEngine, _diagnosticEngine,
            _solutionManagementEngine, _structuralRefinementEngine, _dependencyEngine,
            _config, NullLogger<SentinelWorkspaceTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- ListFeatures (sync) ---

    [Test]
    public void ListFeatures_Always_ReturnsList()
    {
        var result = _tools.ListFeatures();
        Assert.That(result, Is.Not.Null);
    }

    // --- UpdateFeatures (sync) ---

    [Test]
    public void UpdateFeatures_EmptyUpdates_ReturnsString()
    {
        var result = _tools.UpdateFeatures(new List<KeyValuePair<string, bool>>());
        Assert.That(result, Is.Not.Null);
    }

    // --- GetFeatureStatus (sync) ---

    [Test]
    public void GetFeatureStatus_EmptyList_ReturnsList()
    {
        var result = _tools.GetFeatureStatus(new List<string>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GetFeatureStatus_WithFeatureName_ReturnsList()
    {
        var features = _tools.ListFeatures();
        if (features.Count > 0)
        {
            var result = _tools.GetFeatureStatus(new List<string> { features[0].Key });
            Assert.That(result, Is.Not.Null);
        }
        else
        {
            Assert.Pass("No features to query.");
        }
    }

    // --- ListProjects ---

    [Test]
    public async Task ListProjects_WithLoadedSolution_ReturnsList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListProjects();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void ListProjects_NoSolution_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ListProjects());
    }

    // --- ListFiles ---

    [Test]
    public async Task ListFiles_KnownProject_ReturnsFileList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListFiles("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void ListFiles_UnknownProject_ThrowsException()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ListFiles("NoSuchProject"));
    }

    // --- ListDependencies ---

    [Test]
    public async Task ListDependencies_KnownProject_ReturnsReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListDependencies("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- LoadSolution ---

    [Test]
    public async Task LoadSolution_NonExistentPath_ReturnsErrorString()
    {
        var result = await _tools.LoadSolution("fake_path.sln");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // --- Diagnose ---

    [Test]
    public async Task Diagnose_NoArguments_ReturnsHealthReport()
    {
        var result = await _tools.Diagnose();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Diagnose_WithLoadedSolution_ReturnsHealthReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.Diagnose();
        Assert.That(result, Is.Not.Null);
    }

    // --- GetExternalChanges (sync) ---

    [Test]
    public void GetExternalChanges_Always_ReturnsList()
    {
        var result = _tools.GetExternalChanges();
        Assert.That(result, Is.Not.Null);
    }

    // --- AcknowledgeSync (void sync) ---

    [Test]
    public void AcknowledgeSync_Always_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _tools.AcknowledgeSync());
    }

    // --- ValidateProposedDiff ---

    [Test]
    public async Task ValidateProposedDiff_ValidDiff_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var diff = "--- Test.cs\n+++ Test.cs\n@@ -1,1 +1,1 @@\n-namespace TestProj; public class Order { public int Id { get; set; } }\n+namespace TestProj; public class Order { public int Id { get; set; } public string Name { get; set; } }";
        var result = await _tools.ValidateProposedDiff("Test.cs", diff);
        Assert.That(result, Is.Not.Null);
    }

    // --- ValidateProposedChanges ---

    [Test]
    public async Task ValidateProposedChanges_ValidChanges_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var changes = new Dictionary<string, string>
        {
            ["Test.cs"] = SimpleSource + " // changed"
        };
        var result = await _tools.ValidateProposedChanges(changes);
        Assert.That(result, Is.Not.Null);
    }

    // --- ValidateStagedChanges ---

    [Test]
    public void ValidateStagedChanges_UnknownChangeId_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.CatchAsync<Exception>(() => _tools.ValidateStagedChanges("nonexistent-change-id"));
    }

    // --- ApplyProposedDiff ---

    [Test]
    public void ApplyProposedDiff_NonExistentFile_ThrowsException()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.ApplyProposedDiff("NonExistent.cs", "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new"));
    }

    // --- ApplyProposedChanges ---

    [Test]
    public async Task ApplyProposedChanges_EmptyChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ApplyProposedChanges(new Dictionary<string, string>());
        Assert.That(result, Is.Not.Null);
    }

    // --- RetryFailedChanges ---

    [Test]
    public async Task RetryFailedChanges_NoFailedChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.RetryFailedChanges();
        Assert.That(result, Is.Not.Null);
    }

    // --- ApplyStagedChanges ---

    [Test]
    public void ApplyStagedChanges_UnknownChangeId_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.CatchAsync<Exception>(() => _tools.ApplyStagedChanges("nonexistent-change-id"));
    }

    // --- GetStagedChanges (sync) ---

    [Test]
    public void GetStagedChanges_UnknownChangeId_Throws()
    {
        Assert.Catch<Exception>(() => _tools.GetStagedChanges("nonexistent-change-id"));
    }

    // --- DiscardStagedChanges (sync) ---

    [Test]
    public void DiscardStagedChanges_UnknownChangeId_ReturnsNotFoundMessage()
    {
        var result = _tools.DiscardStagedChanges("nonexistent-change-id");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
        Assert.That(result, Does.Contain("not found").IgnoreCase);
    }

    // --- GetFileDiagnostics ---

    [Test]
    public async Task GetFileDiagnostics_ValidFile_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetFileDiagnostics("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDelete ---

    [Test]
    public async Task SafeDelete_ValidPosition_ReturnsString()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SafeDelete("Test.cs", 1, 1);
        Assert.That(result, Is.Not.Null);
    }

    // --- SyncTypeAndFilename ---

    [Test]
    public async Task SyncTypeAndFilename_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SyncTypeAndFilename("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- CreateProject ---

    [Test]
    public void CreateProject_NewProjectName_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<Exception>(() => _tools.CreateProject("NewTestProject"));
    }

    // --- GetProjectDiagnostics ---

    [Test]
    public async Task GetProjectDiagnostics_KnownProject_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetProjectDiagnostics("TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetSolutionDiagnostics ---

    [Test]
    public async Task GetSolutionDiagnostics_WithLoadedSolution_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetSolutionDiagnostics();
        Assert.That(result, Is.Not.Null);
    }

    // --- SplitProjectByFolder ---

    [Test]
    public void SplitProjectByFolder_NonExistentFolder_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<Exception>(() => _tools.SplitProjectByFolder("TestProj", "NonExistentFolder", "NewProject"));
    }
}
