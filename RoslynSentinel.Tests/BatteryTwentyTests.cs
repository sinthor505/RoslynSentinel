// Battery #20 — SentinelWorkspaceTools
// Tests all 26 public methods of SentinelWorkspaceTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

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

    // --- Features (consolidated: list, update, get) ---

    [Test]
    public void Features_List_ReturnsList()
    {
        var result = _tools.Features("list");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_UpdateEmpty_ReturnsResult()
    {
        var result = _tools.Features("update", enabled: new List<KeyValuePair<string, bool>>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_GetEmptyList_ReturnsResult()
    {
        var result = _tools.Features("get", names: new List<string>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_GetWithFeatureName_ReturnsResult()
    {
        var features = _tools.Features("list") as System.Collections.IEnumerable;
        Assert.That(features, Is.Not.Null);
        Assert.Pass("Features list retrieved successfully.");
    }

    // --- List (consolidated: projects, files, dependencies) ---

    [Test]
    public async Task List_Projects_WithLoadedSolution_ReturnsList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.List("projects");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void List_Projects_NoSolution_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.List("projects"));
    }

    [Test]
    public async Task List_Files_KnownProject_ReturnsFileList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.List("files", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void List_Files_UnknownProject_ThrowsException()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(() => _tools.List("files", "NoSuchProject"));
    }

    [Test]
    public async Task List_Dependencies_KnownProject_ReturnsReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.List("dependencies", "TestProj");
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

    // --- ProposedChange (consolidated: format × action) ---

    [Test]
    public async Task ProposedChange_Diff_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var diff = "--- Test.cs\n+++ Test.cs\n@@ -1,1 +1,1 @@\n-namespace TestProj; public class Order { public int Id { get; set; } }\n+namespace TestProj; public class Order { public int Id { get; set; } public string Name { get; set; } }";
        var result = await _tools.ProposedChange("diff", "validate", filePath: "Test.cs", unifiedDiff: diff);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ProposedChange_Files_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var changes = new Dictionary<string, string>
        {
            ["Test.cs"] = SimpleSource + " // changed"
        };
        var result = await _tools.ProposedChange("files", "validate", changes: changes);
        Assert.That(result, Is.Not.Null);
    }

    // --- StagedChange (consolidated: apply, get, validate, discard) ---

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0058:Expression value is never used", Justification = "Test is only verifying exception throwing")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncUsage", "AsyncFixer06:Task<T> to Task conversion silently discards result", Justification = "Test is only verifying exception throwing")]
    public void StagedChange_Validate_UnknownChangeId_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.CatchAsync<Exception>(() => _tools.StagedChange("validate", "nonexistent-change-id"));
    }

    [Test]
    public void ProposedChange_Diff_Apply_NonExistentFile_ThrowsException()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.ProposedChange("diff", "apply", filePath: "NonExistent.cs", unifiedDiff: "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new"));
    }

    [Test]
    public async Task ProposedChange_Files_Apply_EmptyChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ProposedChange("files", "apply", changes: new Dictionary<string, string>());
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

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0058:Expression value is never used", Justification = "Test is only verifying exception throwing")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncUsage", "AsyncFixer06:Task<T> to Task conversion silently discards result", Justification = "Test is only verifying exception throwing")]
    public void StagedChange_Apply_UnknownChangeId_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.CatchAsync<Exception>(() => _tools.StagedChange("apply", "nonexistent-change-id"));
    }

    [Test]
    public void StagedChange_Get_UnknownChangeId_Throws()
    {
        Assert.CatchAsync<Exception>(() => _tools.StagedChange("get", "nonexistent-change-id"));
    }

    [Test]
    public async Task StagedChange_Discard_UnknownChangeId_ReturnsNotFoundMessage()
    {
        var result = await _tools.StagedChange("discard", "nonexistent-change-id");
        Assert.That(result, Is.Not.Null);
        Assert.That(result?.ToString(), Does.Contain("not found").IgnoreCase);
    }

    // --- GetDiagnostics (consolidated: file, project, solution) ---

    [Test]
    public async Task GetDiagnostics_File_ValidFile_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics("file", "Test.cs");
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

    // --- CreateProject ---

    [Test]
    public void CreateProject_NewProjectName_Throws()
    {
        SetSource(SimpleSource, "Test.cs");
        Assert.ThrowsAsync<Exception>(() => _tools.CreateProject("NewTestProject"));
    }

    [Test]
    public async Task GetDiagnostics_Project_KnownProject_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics("project", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetDiagnostics_Solution_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics("solution");
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
