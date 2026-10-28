// Coverage for the includeTests parameter of WorkspaceReadNavigationImpl.SearchSolutionTextTool.
// Test projects are always scanned; with includeTests: false their hits are hidden and only counted per
// project in the status message. Test projects are those whose .csproj references Microsoft.NET.Test.Sdk
// (TestProjectDetector.IsTestProject).

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
public class SearchSolutionTextIncludeTestsTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    private const string TestsMarker = "IncludeTestsMarker_InTestProject";
    private const string AppMarker = "IncludeTestsMarker_InAppProject";
    private const string SharedPattern = "IncludeTestsMarker_";

    private string _dir = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceReadNavigationImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "IncludeTestsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "TestsProj"));
        Directory.CreateDirectory(Path.Combine(_dir, "AppProj"));
        File.WriteAllText(Path.Combine(_dir, "TestsProj", "TestsProj.csproj"), TestSdkCsproj);
        File.WriteAllText(Path.Combine(_dir, "AppProj", "AppProj.csproj"), PlainCsproj);

        // SolutionPath supplies the solution root; without it FilePathWrapper.Relative is empty and
        // a '/'-containing fileGlob (matched against the solution-relative path) matches nothing.
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance)
        {
            SolutionPath = Path.Combine(_dir, "Test.slnx")
        };
        _impl = TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var testsDocPath = Path.Combine(_dir, "TestsProj", "UnitTest1.cs");
        var appDocPath = Path.Combine(_dir, "AppProj", "App.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestsProj",
            Path.Combine(_dir, "TestsProj", "TestsProj.csproj"),
            [("UnitTest1.cs", "public class UnitTest1 { const string M = \"" + TestsMarker + "\"; }", testsDocPath)]);

        var appProjectId = ProjectId.CreateNewId();
        var appProject = ProjectInfo.Create(appProjectId, VersionStamp.Default, "AppProj", "AppProj", LanguageNames.CSharp)
            .WithFilePath(Path.Combine(_dir, "AppProj", "AppProj.csproj"));
        solution = solution.AddProject(appProject)
            .AddDocument(DocumentId.CreateNewId(appProjectId, "App.cs"), "App.cs",
                SourceText.From("public class App { const string M = \"" + AppMarker + "\"; }"), filePath: appDocPath);

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static IEnumerable<string> PathsOf(TextSearchResult payload) =>
        payload.LiteralResults.Select(m => m.filePath.Absolute);

    [Test]
    public async Task IncludeTestsFalse_SkipsDocumentsOfTestProjects()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern, includeTests: false);

        Assert.That(result.IsError, Is.False);
        var paths = PathsOf((TextSearchResult)result.SuccessData!).ToList();
        Assert.That(paths.Any(p => p.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase)), Is.True,
            "the non-test project's document must still be scanned");
        Assert.That(paths.Any(p => p.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase)), Is.False,
            "the test project's document must not be scanned when includeTests is false");
    }

    [Test]
    public async Task IncludeTestsTrue_ScansDocumentsOfTestProjects()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern, includeTests: true);

        Assert.That(result.IsError, Is.False);
        var paths = PathsOf((TextSearchResult)result.SuccessData!).ToList();
        Assert.That(paths.Any(p => p.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(paths.Any(p => p.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(result.StatusMessage, Does.Not.Contain("hidden because includeTests is false"),
            "nothing is excluded when includeTests is true");
    }

    [Test]
    public async Task IncludeTestsOmitted_DefaultsToScanningTestProjects()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern);

        Assert.That(result.IsError, Is.False);
        var paths = PathsOf((TextSearchResult)result.SuccessData!).ToList();
        Assert.That(paths.Any(p => p.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase)), Is.True,
            "the default for includeTests is true, so the test project must be scanned");
    }

    [Test]
    public async Task IncludeTestsFalse_WithMatchesElsewhere_StatusMessageNamesTheExclusion()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern, includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("+1 more in test projects (TestsProj 1)"));
        Assert.That(result.StatusMessage, Does.Contain("includeTests: true"));
    }

    [Test]
    public async Task IncludeTestsFalse_ZeroMatchesOnlyBecauseOfExclusion_WarningMentionsExclusion()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: TestsMarker, includeTests: false);

        Assert.That(result.IsError, Is.False, "zero matches is an empty answer, not a failure");
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("hidden because includeTests is false"));
        Assert.That(result.StatusMessage, Does.Contain("TestsProj 1"));
        Assert.That(result.StatusMessage, Does.Contain("includeTests: true"));
    }

    [Test]
    public async Task IncludeTestsFalse_GlobOnlyMatchingHiddenTestProject_IsNotAnErrorAndNamesIncludeTests()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern,
            fileGlob: "UnitTest1.cs", includeTests: false);

        Assert.That(result.IsError, Is.False, "the glob matched a (hidden) test document, so it is not a glob problem");
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("includeTests: true"));
        Assert.That(result.StatusMessage, Does.Contain("TestsProj 1"));
        Assert.That(result.StatusMessage, Does.Not.Contain("glob problem"));
    }

    [Test]
    public async Task IncludeTestsFalse_GlobNamingTestProject_ReturnsTestDocumentWithoutHiddenNote()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern,
            fileGlob: "TestsProj/**/*.cs", includeTests: false);

        Assert.That(result.IsError, Is.False);
        var paths = PathsOf((TextSearchResult)result.SuccessData!).ToList();
        Assert.That(paths.Any(p => p.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase)), Is.True,
            "a glob whose first segment names a test project implies includeTests");
        Assert.That(result.StatusMessage, Does.Not.Contain("hidden because includeTests is false"));
    }

    [Test]
    public async Task IncludeTestsFalse_GlobWithWildcardFirstSegment_StillHidesTestProject()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern,
            fileGlob: "**/*.cs", includeTests: false);

        Assert.That(result.IsError, Is.False);
        var paths = PathsOf((TextSearchResult)result.SuccessData!).ToList();
        Assert.That(paths.Any(p => p.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(paths.Any(p => p.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase)), Is.False,
            "'**' names no project, so it must not switch includeTests on");
        Assert.That(result.StatusMessage, Does.Contain("+1 more in test projects (TestsProj 1)"));
    }

    [Test]
    public async Task GlobMatchingNoDocument_IsNotAnErrorAndSamplesPathsFromAllProjects()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern,
            fileGlob: "NoSuchFile.cs", includeTests: false);

        Assert.That(result.IsError, Is.False, "a glob that matches nothing is an empty answer, not a failure");
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("fileGlob 'NoSuchFile.cs' matched 0 documents"));
        Assert.That(result.StatusMessage, Does.Contain("UnitTest1.cs"), "sample paths must include test-project documents");
        Assert.That(result.StatusMessage, Does.Contain("App.cs"));
        Assert.That(result.StatusMessage, Does.Not.Contain("glob problem, not a search-pattern problem"));
    }
}
