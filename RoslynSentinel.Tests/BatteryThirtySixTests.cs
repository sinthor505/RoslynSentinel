using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the four improvements shipped in this sprint:
///  1. FindUnawaitedFireAndForget — filePath now optional; projectName scope added.
///  2. FindHardcodedPaths — project/solution scope added.
///  3. CheckForSqlInjection — project/solution scope added.
///  4. FindSequentialIndependentAwaits — consecutive block grouped into one finding.
///  5. ProjectStructureEngine NAME_MISMATCH — AppHost projects suppressed.
/// </summary>
[TestFixture]
public class BatteryThirtySixTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncSafetyEngine _asyncSafetyEngine;
    private SecurityEngine _securityEngine;
    private ProjectStructureEngine _structureEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _structureEngine = new ProjectStructureEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetSingleFile(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultiProject(params (string projectName, string fileName, string source)[] items)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(
            Microsoft.CodeAnalysis.SolutionId.CreateNewId(), Microsoft.CodeAnalysis.VersionStamp.Default);
        var solution = workspace.AddSolution(solutionInfo);

        foreach (var grp in items.GroupBy(x => x.projectName))
        {
            var docs = grp.Select(x => (x.fileName, x.source)).ToArray();
            var proj = TestSolutionBuilder.CreateSolutionWithProject(grp.Key, docs);
            // Merge by adding each project's documents into the shared workspace solution
            var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            solution = solution.AddProject(
                Microsoft.CodeAnalysis.ProjectInfo.Create(projectId,
                    Microsoft.CodeAnalysis.VersionStamp.Default,
                    grp.Key, grp.Key,
                    Microsoft.CodeAnalysis.LanguageNames.CSharp)
                .WithMetadataReferences(
                    proj.Projects.First().MetadataReferences)
                .WithCompilationOptions(
                    proj.Projects.First().CompilationOptions!));

            foreach (var (fn, src) in grp.Select(x => (x.fileName, x.source)))
            {
                solution = solution.AddDocument(
                    Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId),
                    fn,
                    Microsoft.CodeAnalysis.Text.SourceText.From(src));
            }
        }

        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1 — FindUnawaitedFireAndForget: solution-wide scan (filePath = null)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindUnawaitedFireAndForget_NullFilePath_ScansWholeSolution()
    {
        SetSingleFile(@"
using System.Threading.Tasks;
class C {
    void M() { DoWorkAsync(); }
    Task DoWorkAsync() => Task.CompletedTask;
}", "Fire.cs");

        var results = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(filePath: null);
        Assert.That(results, Is.Not.Empty, "Solution-wide scan should find fire-and-forget");
        Assert.That(results.Any(r => r.MethodName == "M"), Is.True);
    }

    [Test]
    public async Task FindUnawaitedFireAndForget_ProjectNameScope_FiltersCorrectly()
    {
        // Two-project setup via simple single-project workspace (projectName must match)
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TargetProj", [("Fire.cs", @"
using System.Threading.Tasks;
class C {
    void M() { DoWorkAsync(); }
    Task DoWorkAsync() => Task.CompletedTask;
}")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(projectName: "TargetProj");
        Assert.That(results, Is.Not.Empty, "projectName scope should return findings from that project");

        var empty = await _asyncSafetyEngine.FindUnawaitedFireAndForgetAsync(projectName: "NonExistent");
        Assert.That(empty, Is.Empty, "Unknown project should return no findings");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2 — FindHardcodedPaths: solution-wide and project scope
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindHardcodedPaths_NullScope_FindsPathsAcrossSolution()
    {
        SetSingleFile(@"
class C {
    string p = ""C:\\Data\\file.txt"";
}", "Paths.cs");

        var results = await _securityEngine.FindHardcodedPathsAsync();
        Assert.That(results, Is.Not.Empty, "Solution-wide scan should find hardcoded path");
        Assert.That(results.All(r => r.FilePath != null), Is.True, "FilePath must be set on every finding");
    }

    [Test]
    public async Task FindHardcodedPaths_FileScope_ReturnsSameAsOldBehavior()
    {
        SetSingleFile(@"
class C {
    string p = ""C:\\Data\\file.txt"";
}", "Paths.cs");

        // Locate the actual document path from the workspace
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var docPath = solution.Projects.First().Documents.First().FilePath ?? "Paths.cs";

        var results = await _securityEngine.FindHardcodedPathsAsync(filePath: docPath);
        Assert.That(results, Is.Not.Empty, "File-scoped scan should still work");
    }

    [Test]
    public async Task FindHardcodedPaths_ProjectScope_FiltersToProject()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("MyService", [("Paths.cs", @"
class C { string p = ""C:\\Logs\\app.log""; }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _securityEngine.FindHardcodedPathsAsync(projectName: "MyService");
        Assert.That(results, Is.Not.Empty);

        var empty = await _securityEngine.FindHardcodedPathsAsync(projectName: "Ghost");
        Assert.That(empty, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3 — CheckForSqlInjection: solution-wide and project scope
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CheckForSqlInjection_NullScope_FindsVulnerabilitiesAcrossSolution()
    {
        SetSingleFile(@"
using System.Data;
class Repo {
    void Run(IDbCommand cmd, string id) {
        cmd.ExecuteNonQuery($""SELECT * FROM T WHERE Id = {id}"");
    }
}", "Repo.cs");

        var results = await _securityEngine.CheckForSqlInjectionAsync();
        Assert.That(results, Is.Not.Empty, "Solution-wide scan should detect SQL injection");
    }

    [Test]
    public async Task CheckForSqlInjection_ProjectScope_FiltersCorrectly()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DataProj", [("Repo.cs", @"
using System.Data;
class Repo {
    void Run(IDbCommand cmd, string id) {
        cmd.ExecuteNonQuery($""SELECT * FROM T WHERE Id = {id}"");
    }
}")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _securityEngine.CheckForSqlInjectionAsync(projectName: "DataProj");
        Assert.That(results, Is.Not.Empty);

        var empty = await _securityEngine.CheckForSqlInjectionAsync(projectName: "NoSuchProj");
        Assert.That(empty, Is.Empty);
    }

    [Test]
    public async Task CheckForSqlInjection_FileScope_StillWorks()
    {
        SetSingleFile(@"
using System.Data;
class Repo {
    void Run(IDbCommand cmd, string id) {
        cmd.ExecuteNonQuery($""SELECT * FROM T WHERE Id = {id}"");
    }
}", "Repo.cs");

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var docPath = solution.Projects.First().Documents.First().FilePath ?? "Repo.cs";

        var results = await _securityEngine.CheckForSqlInjectionAsync(filePath: docPath);
        Assert.That(results, Is.Not.Empty, "File-scoped SQL injection scan must still work");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4 — FindSequentialIndependentAwaits: block grouping
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindSequentialIndependentAwaits_ThreeIndependent_OneGroupedFinding()
    {
        // 3 sequential independent awaits → must produce exactly ONE finding (not 2)
        SetSingleFile(@"
using System.Threading.Tasks;
class C {
    async Task M() {
        var a = await Task.FromResult(1);
        var b = await Task.FromResult(2);
        var c = await Task.FromResult(3);
    }
}");
        var results = await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync();

        Assert.That(results.Count, Is.EqualTo(1),
            "Three consecutive independent awaits should produce exactly ONE grouped finding");
        Assert.That(results[0].Reason, Does.Contain("'a'"));
        Assert.That(results[0].Reason, Does.Contain("'b'"));
        Assert.That(results[0].Reason, Does.Contain("'c'"));
    }

    [Test]
    public async Task FindSequentialIndependentAwaits_FiveIndependent_OneGroupedFinding()
    {
        // 5 independent sequential awaits → 1 finding (old code would give 4)
        SetSingleFile(@"
using System.Threading.Tasks;
class C {
    async Task M() {
        var a = await Task.FromResult(1);
        var b = await Task.FromResult(2);
        var c = await Task.FromResult(3);
        var d = await Task.FromResult(4);
        var e = await Task.FromResult(5);
    }
}");
        var results = await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync();

        Assert.That(results.Count, Is.EqualTo(1),
            "Five consecutive independent awaits should produce exactly ONE grouped finding");
        Assert.That(results[0].Reason, Does.Contain("5"), "Should mention all 5 in the message");
    }

    [Test]
    public async Task FindSequentialIndependentAwaits_DependentPair_NotReported()
    {
        // Second await uses result of first → NOT parallelisable
        SetSingleFile(@"
using System.Threading.Tasks;
class C {
    async Task M() {
        var a = await Task.FromResult(1);
        var b = await Task.FromResult(a + 1);
    }
}");
        var results = await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync();
        Assert.That(results, Is.Empty, "Dependent awaits must not be flagged");
    }

    [Test]
    public async Task FindSequentialIndependentAwaits_TwoBlocksSeparatedByDependent_TwoFindings()
    {
        // Block1: a,b  |  dependent c=f(b)  |  Block2: d,e
        // Expect 2 separate findings (one per independent block)
        SetSingleFile(@"
using System.Threading.Tasks;
class C {
    async Task M() {
        var a = await Task.FromResult(1);
        var b = await Task.FromResult(2);
        var c = await Task.FromResult(b + 1);  // depends on b — breaks the block
        var d = await Task.FromResult(10);
        var e = await Task.FromResult(11);
    }
}");
        var results = await _asyncSafetyEngine.FindSequentialIndependentAwaitsAsync();

        Assert.That(results.Count, Is.EqualTo(2),
            "Two independent blocks separated by a dependent await should produce two findings");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5 — NAME_MISMATCH: AppHost projects suppressed
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NameMismatch_AppHostProject_Suppressed()
    {
        // File "Resources.cs" with a type named "ServiceNames" — classic Aspire AppHost pattern
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "MyApp.AppHost",
            [("Resources.cs", "public static class ServiceNames { public const string Api = \"api\"; }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);

        Assert.That(results, Is.Empty,
            "NAME_MISMATCH should be suppressed in AppHost projects");
    }

    [Test]
    public async Task NameMismatch_NonAppHostProject_StillReported()
    {
        // File "Foo.cs" with a type named "Bar" — genuine mismatch in a normal project
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "MyApp.Service",
            [("Foo.cs", "public class Bar { }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);

        Assert.That(results.Any(r => r.Contains("NAME_MISMATCH")), Is.True,
            "NAME_MISMATCH should still be reported in non-AppHost projects");
    }

    [Test]
    public async Task NameMismatch_AppHostDotNew_AlsoSuppressed()
    {
        // ".AppHost.New" suffix — the ExpressRecipe pattern
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "ExpressRecipe.AppHost.New",
            [("Constants.cs", "public static class ResourceNames { }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);

        Assert.That(results, Is.Empty,
            "AppHost.New variant must also be suppressed");
    }
}
