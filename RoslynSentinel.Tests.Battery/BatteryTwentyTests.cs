// Battery #20 — SentinelWorkspaceTools
// Tests all 26 public methods of SentinelWorkspaceTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class BatteryTwentyTests
{
    private IWorkspaceManager _workspaceManager;
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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _diffEngine = new DiffEngine();
        _validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        _solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new SentinelWorkspaceTools(
            _workspaceManager, _validationEngine, _diffEngine, _diagnosticEngine,
            _solutionManagementEngine, _structuralRefinementEngine, _dependencyEngine,
            _projectConsistencyEngine, _config, NullLogger<SentinelWorkspaceTools>.Instance, new BuildEngine(_workspaceManager, _diagnosticEngine),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(_workspaceManager));
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
    public async Task Features_List_ReturnsList()
    {
        var result = await _tools.Features(reason: "test", FeaturesAction.list);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Features_UpdateEmpty_ReturnsResult()
    {
        var result = await _tools.Features(reason: "test", FeaturesAction.update, enabled: new List<KeyValuePair<string, bool>>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Features_GetEmptyList_ReturnsResult()
    {
        var result = await _tools.Features(reason: "test", FeaturesAction.get, names: new List<string>());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Features_GetWithFeatureName_ReturnsResult()
    {
        var result = await _tools.Features(reason: "test", FeaturesAction.list);
        var features = result.Data as System.Collections.IEnumerable;
        Assert.That(features, Is.Not.Null);
        Assert.Pass("Features list retrieved successfully.");
    }

    // --- List (consolidated: projects, files, dependencies) ---

    [Test]
    public async Task List_Projects_WithLoadedSolution_ReturnsList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.projects);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_Projects_NoSolution_ReturnsStructuredError()
    {
        // Tools no longer throw: they return ToolResult with Success=false and a ResultError.
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.projects);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task List_Files_KnownProject_ReturnsFileList()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.files, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_Files_UnknownProject_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.files, "NoSuchProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("NoSuchProject"));
    }

    [Test]
    public async Task List_Dependencies_KnownProject_ReturnsReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.dependencies, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task List_SolutionItems_NoSolutionLoaded_ReturnsStructuredError()
    {
        var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.solutionItems);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task List_SolutionItems_ParsesSolutionFolderFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var slnPath = Path.Combine(tempDir, "Test.sln");
        await File.WriteAllTextAsync(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
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

            var result = await _tools.ListSolutionItems(reason: "test", SolutionItemsKind.solutionItems);

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
        var result = await _tools.SearchSolutionText(reason: "test", "Order");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public async Task SearchSolutionText_RegexLikePatternWithoutIsRegex_ReturnsWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText(reason: "test", @"^\s*public enum OrderStatus", searchMode: TextSearchMode.literal);

        Assert.That(result.Success, Is.False, "explicit literal mode must actually search literally, not silently switch to regex, and zero matches is now a failure");
        Assert.That(result.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.NoMatches));
        Assert.That(result.Error?.Message, Does.Contain("contains regex metacharacters"));
        Assert.That(result.Error?.Message, Does.Contain("searched for the literal substring as requested"));
    }

    [Test]
    public async Task SearchSolutionText_RegexLikePatternWithIsRegex_ReturnsNoWarning()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText(reason: "test", @"^namespace TestProj", searchMode: TextSearchMode.regex);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Warning, Is.Null);
    }

    [Test]
    public async Task SearchSolutionText_NoMatches_ReturnsNoMatchesError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SearchSolutionText(reason: "test", "ThisPatternDoesNotAppearAnywhere");

        Assert.That(result.Success, Is.False, "Zero matches should surface as a failure so the protocol-level IsError filter picks it up.");
        Assert.That(result.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.NoMatches));
        Assert.That(result.Error?.Message, Does.Contain("ProjectDoc"));
    }

    [Test]
    public async Task SearchSolutionText_MatchInsideMethod_ReturnsEnclosingMemberName()
    {
        SetSource("""
        using System;

        namespace TestProj;

        public class Calculator
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """, "Test.cs");

        var result = await _tools.SearchSolutionText(reason: "test", "return a + b");

        Assert.That(result.Success, Is.True);
        var matches = (System.Collections.Generic.IEnumerable<TextSearchMatch>)result.Data!;
        var match = matches.Single();
        Assert.That(match.EnclosingMember, Is.EqualTo("Add"));
    }

    [Test]
    public async Task SearchSolutionText_MatchOutsideAnyMember_ReturnsNullEnclosingMember()
    {
        SetSource("""
        using System;

        namespace TestProj;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;
        }
        """, "Test.cs");

        var result = await _tools.SearchSolutionText(reason: "test", "using System");

        Assert.That(result.Success, Is.True);
        var matches = (System.Collections.Generic.IEnumerable<TextSearchMatch>)result.Data!;
        var match = matches.Single();
        Assert.That(match.EnclosingMember, Is.Null);
    }

    [Test]
    public async Task SearchSolutionText_WorkspaceVersion_IncreasesAcrossAMutation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"WsVersion_Search_{Guid.NewGuid()}.cs");
        try
        {
            const string initialContent = "namespace TestProj; public class Foo { public int Bar() => 1; }";
            await File.WriteAllTextAsync(tempFile, initialContent);

            var projectCsproj = Path.Combine(Path.GetTempPath(), "TestProj", "TestProj.csproj");
            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj", projectCsproj, [("Foo.cs", initialContent, tempFile)]);
            _workspaceManager.SetTestSolution(solution);

            var before = await _tools.SearchSolutionText(reason: "test", "Bar");
            Assert.That(before.Success, Is.True);
            Assert.That(before.WorkspaceVersion, Is.Not.Null);

            const string updatedContent = "namespace TestProj; public class Foo { public int Bar() => 2; public int Baz() => 3; }";
            await File.WriteAllTextAsync(tempFile, updatedContent);
            var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<FilePath, string> { [tempFile] = updatedContent });
            Assert.That(applyResult.Success, Is.True);

            var after = await _tools.SearchSolutionText(reason: "test", "Baz");

            Assert.That(after.Success, Is.True);
            Assert.That(after.WorkspaceVersion, Is.Not.Null);
            Assert.That(after.WorkspaceVersion, Is.GreaterThan(before.WorkspaceVersion!),
                "A read tool's workspaceVersion must increase after a mutation lands, so a caller can tell its earlier response is stale.");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- LoadSolution ---

    [Test]
    public async Task LoadSolution_NonExistentPath_ReturnsErrorString()
    {
        var result = await _tools.LoadSolution(reason: "test", "fake_path.sln");
        Assert.That(result.Success, Is.False, "nonexistent solution path should not succeed");
        Assert.That(result.Error?.Message, Is.Not.Null.And.Not.Empty, "should carry an error message");
    }

    // --- ListWorkspaceSolutions ---

    [Test]
    public void ListWorkspaceSolutions_PathWrappedInQuotes_StillResolvesDirectory()
    {
        // Regression: workspacePath used to go straight to Directory.Exists with no
        // sanitization, so a path wrapped in stray quotes (e.g. copied from a shell example)
        // failed even though the underlying directory existed.
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var wrappedPath = $"  \"{tempDir}\"  ";

            var result = _tools.ListWorkspaceSolutions(reason: "test", wrappedPath);

            Assert.That(result.Success, Is.True,
                "A workspacePath wrapped in quotes/whitespace must still resolve to the real directory.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void ListWorkspaceSolutions_UnknownPath_ReturnsInvalidArgument()
    {
        var result = _tools.ListWorkspaceSolutions(reason: "test", Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_DoesNotExist_" + Guid.NewGuid()));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error?.ErrorCode, Is.EqualTo("InvalidArgument"));
    }

    [Test]
    public void ListWorkspaceSolutions_DriveRoot_RejectedInsteadOfScanningWholeDrive()
    {
        // Regression: a model passed workspacePath:"/" (a plausible "search from root" guess),
        // which Directory.Exists resolves to the current drive's root on Windows, sending an
        // uncancellable Directory.EnumerateFiles(..., AllDirectories) across the entire C: drive —
        // observed hanging 30+ minutes while climbing to 4.6GB RAM in a real model-eval run.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        var result = _tools.ListWorkspaceSolutions(reason: "test", driveRoot);

        Assert.That(result.Success, Is.False, "scanning an entire drive root must be rejected, not attempted");
        Assert.That(result.Error?.ErrorCode, Is.EqualTo("InvalidArgument"));
    }

    [Test]
    public void ListWorkspaceSolutions_BareSlash_RejectedInsteadOfScanningWholeDrive()
    {
        // "/" is the exact value observed triggering the hang above — NormalizeWirePath leaves it
        // untouched, and Directory.Exists("/") is true on Windows (resolves to the current drive).
        var result = _tools.ListWorkspaceSolutions(reason: "test", "/");

        Assert.That(result.Success, Is.False, "'/' resolves to a drive root and must be rejected");
        Assert.That(result.Error?.ErrorCode, Is.EqualTo("InvalidArgument"));
    }

    // --- Diagnose ---

    // GetExternalChanges/AcknowledgeSync tests moved to SentinelAdminToolsTests.cs —
    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to SentinelAdminTools
    // (RoslynSentinel.Server.Basic/SentinelAdminTools.cs), gated behind the "Admin" mode. See
    // docs/current/ideas/external-drift-hard-blocker.md.

    // --- ApplyDiff (consolidated: format × action; formerly named ProposedChange) ---

    [Test]
    public async Task ApplyDiff_Diff_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var diff = "--- Test.cs\n+++ Test.cs\n@@ -1,1 +1,1 @@\n-namespace TestProj; public class Order { public int Id { get; set; } }\n+namespace TestProj; public class Order { public int Id { get; set; } public string Name { get; set; } }";
        var result = await _tools.ApplyDiff(reason: "test", ChangesetFormat.diff, ProposedChangeAction.validate, filepath: "Test.cs", unifiedDiff: diff);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ApplyDiff_Files_Validate_ReturnsDiagnosticReport()
    {
        SetSource(SimpleSource, "Test.cs");
        var changes = new Dictionary<FilePath, string>
        {
            [new FilePath("Test.cs")] = SimpleSource + " // changed"
        };
        var result = await _tools.ApplyDiff(reason: "test", ChangesetFormat.files, ProposedChangeAction.validate, changes: changes);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ApplyDiff_Diff_Apply_NonExistentFile_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ApplyDiff(reason: "test", ChangesetFormat.diff, ProposedChangeAction.apply, filepath: "NonExistent.cs", unifiedDiff: "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task ApplyDiff_Files_Apply_EmptyChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.ApplyDiff(reason: "test", ChangesetFormat.files, ProposedChangeAction.apply, changes: new Dictionary<FilePath, string>());
        Assert.That(result, Is.Not.Null);
    }

    // --- RetryFailedChanges ---

    [Test]
    public async Task RetryFailedChanges_NoFailedChanges_ReturnsResult()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.RetryFailedChanges(reason: "test");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetDiagnostics (consolidated: file, project, solution) ---

    [Test]
    public async Task GetDiagnostics_File_ValidFile_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(reason: "test", ToolScope.file, "Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDelete ---

    [Test]
    public async Task SafeDelete_ValidPosition_ReturnsString()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SafeDeleteUnusedSymbol(reason: "test", "Test.cs", projectName: "", docCommentId: "", line: 1, column: 1);
        Assert.That(result, Is.Not.Null);
    }

    private const string DeadMethodSource = @"
namespace TestProj;
public class Order
{
    public int Id { get; set; }

    private string BuildInternalDebugLabel()
    {
        return ""debug"";
    }
}";

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 5): the contextSnippet/lineBefore/"
                 + "lineAfter fallback path was documented on SafeDeleteUnusedSymbol's own [Description] "
                 + "but never wired to any resolution logic, leaving an agent with only a raw line/column "
                 + "pair to identify a target — and no other tool exposes a column, only a line, making "
                 + "that pair effectively unobtainable too. A live agent hit exactly this: it had a line "
                 + "number from SearchSolutionText but no column, called the tool with line-only, and got "
                 + "a generic 'requires either (sessionId, projectName, docCommentId) or (line, column)' "
                 + "error before falling back to RemoveMember(skipPrecheck: true) instead.")]
    public async Task SafeDeleteUnusedSymbol_SymbolNameOnly_DeletesZeroUsageMethod()
    {
        SetSource(DeadMethodSource, "Test.cs");

        var result = await _tools.SafeDeleteUnusedSymbol(reason: "test", "Test.cs", symbolName: "BuildInternalDebugLabel");

        Assert.That(result.Success, Is.True, result.Error?.Message);
    }

    [Test]
    public async Task SafeDeleteUnusedSymbol_SymbolNameWithContextSnippet_DisambiguatesAndDeletes()
    {
        SetSource(DeadMethodSource, "Test.cs");

        var result = await _tools.SafeDeleteUnusedSymbol(reason: "test", "Test.cs", symbolName: "BuildInternalDebugLabel",
            contextSnippet: "private string BuildInternalDebugLabel()");

        Assert.That(result.Success, Is.True, result.Error?.Message);
    }

    [Test]
    public async Task SafeDeleteUnusedSymbol_SymbolNameNotFound_ReturnsActionableError()
    {
        SetSource(DeadMethodSource, "Test.cs");

        var result = await _tools.SafeDeleteUnusedSymbol(reason: "test", "Test.cs", symbolName: "NoSuchMethod");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Message, Does.Contain("NoSuchMethod"));
    }

    [Test]
    [Description("sessionId is no longer an exposed parameter: docCommentId + projectName alone "
                 + "(as returned by LocateSymbol/FindReferences) must be sufficient, since no tool "
                 + "ever surfaces a sessionId for a caller to pass back in.")]
    public async Task SafeDeleteUnusedSymbol_DocCommentIdAndProjectNameOnly_NoLongerRequiresSessionId()
    {
        SetSource(DeadMethodSource, "Test.cs");

        var docCommentId = "M:TestProj.Order.BuildInternalDebugLabel";
        var result = await _tools.SafeDeleteUnusedSymbol(reason: "test", "Test.cs", projectName: "TestProj", docCommentId: docCommentId);

        Assert.That(result.Success, Is.True, result.Error?.Message);
    }

    // --- CreateProject ---

    [Test]
    public async Task CreateProject_NewProjectName_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.CreateProject(reason: "test", "NewTestProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task GetDiagnostics_Project_KnownProject_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(reason: "test", ToolScope.project, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetDiagnostics_Solution_ReturnsSummary()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(reason: "test", ToolScope.solution);
        Assert.That(result, Is.Not.Null);
    }

    // --- Build ---

    [Test]
    public async Task Build_QuickBuild_CleanSource_ReturnsSuccess()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.Build(reason: "test", BuildVerifyLevel.quickBuild);

        Assert.That(result.Success, Is.True);
        var data = (BuildResult)result.Data!;
        Assert.That(data.BuildSucceeded, Is.True);
        Assert.That(data.ErrorCount, Is.EqualTo(0));
        Assert.That(data.ExitCode, Is.EqualTo(-1), "quickBuild does not run a subprocess.");
    }

    [Test]
    public async Task Build_QuickBuild_SourceWithCompileError_ReturnsBuildFailure()
    {
        SetSource("namespace TestProj; public class Order { this is not valid C# }", "Test.cs");
        var result = await _tools.Build(reason: "test", BuildVerifyLevel.quickBuild);

        Assert.That(result.Success, Is.True, "The tool call itself succeeds; the build outcome is carried in Data.BuildSucceeded.");
        var data = (BuildResult)result.Data!;
        Assert.That(data.BuildSucceeded, Is.False);
        Assert.That(data.ErrorCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task Build_QuickBuild_RepeatedDiagnosticAcrossFiles_ErrorSummaryGroupsByIdAsync()
    {
        static string MissingUsingSource(string className) => $"namespace TestProj; public class {className} {{ public UndeclaredType Field; }}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
        [
            ("A.cs", MissingUsingSource("A")),
            ("B.cs", MissingUsingSource("B")),
            ("C.cs", MissingUsingSource("C")),
        ]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.Build(reason: "test", BuildVerifyLevel.quickBuild);

        Assert.That(result.Success, Is.True);
        var data = (BuildResult)result.Data!;
        Assert.That(data.BuildSucceeded, Is.False);
        Assert.That(data.ErrorCount, Is.EqualTo(3), "one CS0246 (undeclared type) per file.");
        Assert.That(data.ErrorSummary, Is.Not.Empty);
        var group = data.ErrorSummary.Single(g => g.DiagnosticId == "CS0246");
        Assert.That(group.Count, Is.EqualTo(3));
        Assert.That(group.Locations, Has.Count.EqualTo(3));
        Assert.That(data.WarningSummary, Is.Empty);
    }

    [Test]
    public async Task GetDiagnostics_VerifyQuickBuild_AttachesBuildVerification()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetDiagnostics(reason: "test", ToolScope.solution, verify: BuildVerifyLevel.quickBuild);

        Assert.That(result.Success, Is.True);
        var data = (DiagnosticSummary)result.Data!;
        Assert.That(data.BuildVerification, Is.Not.Null);
        Assert.That(data.BuildVerification!.BuildSucceeded, Is.True);
    }

    [Test]
    public async Task GetWorkspaceHealth_VerifyQuickBuild_AttachesBuildVerification()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.GetWorkspaceHealth(reason: "test", verify: BuildVerifyLevel.quickBuild);

        Assert.That(result.Success, Is.True);
        var data = (WorkspaceHealthReport)result.Data!;
        Assert.That(data.BuildVerification, Is.Not.Null);
    }

    // --- SplitProjectByFolder ---

    [Test]
    public async Task SplitProjectByFolder_NonExistentFolder_ReturnsStructuredError()
    {
        SetSource(SimpleSource, "Test.cs");
        var result = await _tools.SplitProjectByFolder(reason: "test", "TestProj", "NonExistentFolder", "NewProject");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }
}
