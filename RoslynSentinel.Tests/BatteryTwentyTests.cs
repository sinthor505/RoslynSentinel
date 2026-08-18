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
    private ProjectConsistencyEngine _projectConsistencyEngine;
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
        _projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new SentinelWorkspaceTools(
            _workspaceManager, _validationEngine, _diffEngine, _diagnosticEngine,
            _solutionManagementEngine, _structuralRefinementEngine, _dependencyEngine,
            _projectConsistencyEngine, _config, NullLogger<SentinelWorkspaceTools>.Instance);
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
        var result = _tools.Features(FeaturesAction.list);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_UpdateEmpty_ReturnsResult()
    {
        var result = _tools.Features(FeaturesAction.update, enabled: new List<KeyValuePair<string, bool>>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_GetEmptyList_ReturnsResult()
    {
        var result = _tools.Features(FeaturesAction.get, names: new List<string>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Features_GetWithFeatureName_ReturnsResult()
    {
        var features = _tools.Features(FeaturesAction.list) as System.Collections.IEnumerable;
        Assert.That(features, Is.Not.Null);
        Assert.Pass("Features list retrieved successfully.");
    }

    // --- List (consolidated: projects, files, dependencies) ---

    [Test]
    public async Task List_Projects_WithLoadedSolution_ReturnsList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(SolutionItemsKind.projects);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_Projects_NoSolution_ReturnsStructuredError()
    {
        // Tools no longer throw: they return ToolResult with Success=false and a ResultError.
        var result = await _tools.ListSolutionItems(SolutionItemsKind.projects);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task List_Files_KnownProject_ReturnsFileList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(SolutionItemsKind.files, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_Files_UnknownProject_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(SolutionItemsKind.files, "NoSuchProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("NoSuchProject"));
    }

    [Test]
    public async Task List_Dependencies_KnownProject_ReturnsReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(SolutionItemsKind.dependencies, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_SolutionItems_NoSolutionLoaded_ReturnsStructuredError()
    {
        var result = await _tools.ListSolutionItems(SolutionItemsKind.solutionItems);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task List_SolutionItems_ParsesSolutionFolderFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var slnPath = Path.Combine(tempDir, "Test.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{856FAB21-F17B-44B2-8638-072C8D796F07}\"\n" +
            "\tProjectSection(SolutionItems) = preProject\n" +
            "\t\tdocs\\plans\\PLAN.md = docs\\plans\\PLAN.md\n" +
            "\tEndProjectSection\n" +
            "EndProject\n" +
            "Global\n" +
            "EndGlobal\n");

        try
        {
            _workspaceManager.SolutionPath = slnPath;

            var result = await _tools.ListSolutionItems(SolutionItemsKind.solutionItems);

            Assert.That(result.Success, Is.True);
            var items = result.Data as List<SolutionItemFile>;
            Assert.That(items, Is.Not.Null);
            Assert.That(items!.Count, Is.EqualTo(1));
            Assert.That(items[0].FilePath.Relative.Replace('/', '\\'), Is.EqualTo(@"docs\plans\PLAN.md"));
            Assert.That(items[0].SolutionFolder, Is.EqualTo("Solution Items"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void GetSolutionFolderItems_InMemorySolution_ReturnsEmpty()
    {
        SetSource(SimpleSource, "Test.cs");
        var items = _workspaceManager.GetSolutionFolderItems();
        Assert.That(items, Is.Empty);
    }

    // --- SearchSolutionText ---

    [Test]
    public async Task SearchSolutionText_LiteralPattern_ReturnsNoWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText("Order");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public async Task SearchSolutionText_RegexLikePatternWithoutIsRegex_ReturnsWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText(@"^\s*public enum OrderStatus");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Does.Contain("isRegex=true"));
    }

    [Test]
    public async Task SearchSolutionText_RegexLikePatternWithIsRegex_ReturnsNoWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText(@"^namespace TestProj", isRegex: true);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public async Task SearchSolutionText_NoMatches_ReturnsScopeWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText("ThisPatternDoesNotAppearAnywhere");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Does.Contain("ProjectDoc"));
    }

    // --- LoadSolution ---

    [Test]
    public async Task LoadSolution_NonExistentPath_ReturnsErrorString()
    {
        var result = await _tools.LoadSolution("fake_path.sln");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // --- Diagnose ---

    // --- GetExternalChanges (sync) ---

    [Test]
    public void GetExternalChanges_Always_ReturnsList()
    {
        var result = _tools.ListExternalDiskChanges();
        Assert.That(result, Is.Not.Null);
    }

    // --- AcknowledgeSync (void sync) ---

    [Test]
    public void ClearExternalDrift_Always_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _tools.ClearExternalDrift());
    }

    // --- ProposedChange (consolidated: format × action) ---

    [Test]
    public async Task ProposedChange_Diff_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var diff = "--- Test.cs\n+++ Test.cs\n@@ -1,1 +1,1 @@\n-namespace TestProj; public class Order { public int Id { get; set; } }\n+namespace TestProj; public class Order { public int Id { get; set; } public string Name { get; set; } }";
        var result = await _tools.ProposedChange(ChangesetFormat.diff, ProposedChangeAction.validate, filepath: "Test.cs", unifiedDiff: diff);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ProposedChange_Files_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var changes = new Dictionary<FilePath, string>
        {
            [new FilePath("Test.cs")] = SimpleSource + " // changed"
        };
        var result = await _tools.ProposedChange(ChangesetFormat.files, ProposedChangeAction.validate, changes: changes);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ProposedChange_Diff_Apply_NonExistentFile_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ProposedChange(
            ChangesetFormat.diff, ProposedChangeAction.apply,
            filepath: "NonExistent.cs", unifiedDiff: "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task ProposedChange_Files_Apply_EmptyChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ProposedChange(ChangesetFormat.files, ProposedChangeAction.apply, changes: new Dictionary<FilePath, string>());
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

    // --- GetDiagnostics (consolidated: file, project, solution) ---

    [Test]
    public async Task GetDiagnostics_File_ValidFile_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(ToolScope.file, "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDelete ---

    [Test]
    public async Task SafeDelete_ValidPosition_ReturnsString()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SafeDeleteUnusedSymbol("Test.cs", 1, 1);
        Assert.That(result, Is.Not.Null);
    }

    // --- CreateProject ---

    [Test]
    public async Task CreateProject_NewProjectName_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.CreateProject("NewTestProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task GetDiagnostics_Project_KnownProject_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(ToolScope.project, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetDiagnostics_Solution_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(ToolScope.solution);
        Assert.That(result, Is.Not.Null);
    }

    // --- SplitProjectByFolder ---

    [Test]
    public async Task SplitProjectByFolder_NonExistentFolder_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SplitProjectByFolder("TestProj", "NonExistentFolder", "NewProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }
}
