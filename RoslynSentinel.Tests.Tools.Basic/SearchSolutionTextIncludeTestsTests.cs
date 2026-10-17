// Coverage for the includeTests parameter of WorkspaceReadNavigationImpl.SearchSolutionTextTool.
// With includeTests: false, documents of projects whose .csproj references Microsoft.NET.Test.Sdk
// (TestProjectDetector.IsTestProject) are not scanned; the result says how many were excluded.

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

        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _impl = new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance);

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
        Assert.That(result.StatusMessage, Does.Not.Contain("test project(s) were excluded"),
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
        Assert.That(result.StatusMessage, Does.Contain("test project(s) were excluded"));
        Assert.That(result.StatusMessage, Does.Contain("includeTests: true"));
    }

    [Test]
    public async Task IncludeTestsFalse_ZeroMatchesOnlyBecauseOfExclusion_WarningMentionsExclusion()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: TestsMarker, includeTests: false);

        Assert.That(result.IsError, Is.False, "zero matches is an empty answer, not a failure");
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.StatusMessage, Does.Contain("test project(s) were excluded"));
        Assert.That(result.StatusMessage, Does.Contain("includeTests: true"));
    }

    [Test]
    public async Task IncludeTestsFalse_GlobOnlyMatchingExcludedTestProject_ErrorNamesIncludeTests()
    {
        var result = await _impl.SearchSolutionText(reason: "test message", pattern: SharedPattern,
            fileGlob: "UnitTest1.cs", includeTests: false);

        Assert.That(result.IsError, Is.True, "a glob matching only excluded documents is a glob problem and must surface as an error");
        Assert.That(result.ErrorData?.Message, Does.Contain("includeTests: true"));
    }
}
