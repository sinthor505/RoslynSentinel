// Coverage for the zero-hit recovery ladder of WorkspaceReadNavigationImpl.SearchSolutionText: when the
// visible result is empty, ONE extra pass counts relaxed literal candidates (glob dropped, dotted-name
// prefixes, '|' alternatives) and reports them as advice in StatusMessage/WarningDetails. Relaxed
// matches are never returned as hits.

using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class SearchSolutionTextRecoveryHintTests
{
    private string _dir = string.Empty;

    private IWorkspaceManager _workspaceManager = null!;

    private WorkspaceReadNavigationImpl _impl = null!;

    private static Solution AddProject(Solution solution, string dir, string projectName, string csproj, string docName, string content)
    {
        Directory.CreateDirectory(Path.Combine(dir, projectName));
        var csprojPath = Path.Combine(dir, projectName, projectName + ".csproj");
        File.WriteAllText(csprojPath, csproj);
        var projectId = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithFilePath(csprojPath);
        return solution.AddProject(project)
            .AddDocument(DocumentId.CreateNewId(projectId, docName), docName, SourceText.From(content),
                filePath: Path.Combine(dir, projectName, docName));
    }

    // AppProj: Recov.Alpha x2, Recov.Beta x1; LibProj: Recov.Alpha x1; TestsProj (test project): TestOnly x1.
    [SetUp]
    public void SetUp()
    {
        const string testSdkCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";
        const string plainCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

        _dir = Path.Combine(Path.GetTempPath(), "RecoveryHintTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // SolutionPath supplies the solution root so '/'-containing globs match solution-relative paths.
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance)
        {
            SolutionPath = Path.Combine(_dir, "Test.slnx")
        };
        _impl = new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var solution = TestSolutionBuilder.CreateEmptySolution();
        solution = AddProject(solution, _dir, "AppProj", plainCsproj, "App.cs",
            "public class App\n{\n    const string A = \"Recov.Alpha.One\";\n    const string B = \"Recov.Alpha.Two\";\n    const string C = \"Recov.Beta.Three\";\n}\n");
        solution = AddProject(solution, _dir, "LibProj", plainCsproj, "Lib.cs", "// Recov.Alpha.Lib\npublic class Lib { }\n");
        solution = AddProject(solution, _dir, "TestsProj", testSdkCsproj, "UnitTest1.cs", "// TestOnly.Thing.Here\npublic class UnitTest1 { }\n");
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task DottedPattern_NoHits_ReportsPrefixCountsAndNeverReturnsRelaxedMatches()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "Recov.Alpha.Missing", includeTests: false);

        const string expected = "0 matches for 'Recov.Alpha.Missing'; 3 for 'Recov.Alpha' (AppProj 2, LibProj 1); " +
                                "4 for 'Recov' (AppProj 3, LibProj 1). Rerun with query: 'Recov.Alpha'.";
        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain(expected));
        Assert.That(result.WarningDetails, Is.EqualTo(expected));
        var payload = (TextSearchResult)result.SuccessData!;
        Assert.That(payload.LiteralResults, Is.Empty, "relaxed matches are advice only, never hits");
        Assert.That(payload.RegexResults, Is.Empty, "relaxed matches are advice only, never hits");
    }

    [Test]
    public async Task GlobExcludingTheHits_ReportsGlobDroppedCountAndSuggestsProjectGlob()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "Recov.Beta",
            fileGlob: "LibProj/**/*.cs", includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("1 for the same pattern outside fileGlob 'LibProj/**/*.cs' (AppProj 1)"));
        Assert.That(result.StatusMessage, Does.Contain("1 for 'Recov' (LibProj 1)"), "dotted prefixes keep the fileGlob");
        Assert.That(result.StatusMessage, Does.Contain("Rerun without fileGlob or with fileGlob: 'AppProj/**/*.cs'."));
    }

    [Test]
    public async Task AlternationPattern_NoHits_ReportsEachAlternative()
    {
        // "Nope(" is not a valid regex, so the whole pattern only runs as a literal and hits nothing.
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "Nope(|Recov.Beta.Three", includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("1 for 'Recov.Beta.Three' (AppProj 1)"));
        Assert.That(result.StatusMessage, Does.Contain("Rerun with query: 'Recov.Beta.Three'."));
        Assert.That(result.StatusMessage, Does.Not.Contain("'Nope('"), "alternatives with no hits are not listed");
    }

    [Test]
    public async Task CandidateHitsOnlyInTestProject_SaysRerunWithIncludeTests()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "TestOnly.Thing.Missing", includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("1 in test projects for 'TestOnly.Thing' (TestsProj 1; rerun with includeTests: true)"));
        Assert.That(result.StatusMessage, Does.Contain("Rerun with query: 'TestOnly.Thing', includeTests: true."));

        var withTests = await _impl.SearchSolutionText(reason: "test message", pattern: "TestOnly.Thing.Missing", includeTests: true);
        Assert.That(withTests.StatusMessage, Does.Contain("1 for 'TestOnly.Thing' (TestsProj 1)"),
            "with includeTests: true the same hits are visible, not test-only");
    }

    [TestCase("NoSuchToken", Description = "no dot, no '|', no fileGlob: the recovery pass is skipped")]
    [TestCase("Zzz.Yyy.Xxx", Description = "dotted but no candidate hits anything: no hint")]
    public async Task NothingToRelaxOrNothingFound_AddsNoRecoveryHint(string pattern)
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: pattern, includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Not.Contain("Rerun with"));
        Assert.That(result.StatusMessage, Does.Not.Contain("0 matches for"));
        Assert.That(result.WarningDetails, Is.Null);
    }

    [Test]
    public async Task VisibleHits_AddNoRecoveryHint()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "Recov.Alpha.One", includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.GreaterThan(0));
        Assert.That(result.StatusMessage, Does.Not.Contain("Rerun with"));
        Assert.That(result.StatusMessage, Does.Not.Contain("0 matches for"));
    }

    [Test]
    public async Task GlobMatchingNoDocument_StillOffersGlobDroppedCount()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: "Recov.Beta",
            fileGlob: "NoSuchProj/*.cs", includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("matched 0 documents in any project"));
        Assert.That(result.StatusMessage, Does.Contain("1 for the same pattern outside fileGlob 'NoSuchProj/*.cs' (AppProj 1)"));
        Assert.That(result.StatusMessage, Does.Contain("Rerun without fileGlob or with fileGlob: 'AppProj/**/*.cs'."));
    }
}
