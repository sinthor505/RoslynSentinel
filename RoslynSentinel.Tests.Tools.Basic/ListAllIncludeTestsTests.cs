// Coverage for the includeTests parameter of WorkspaceReadNavigationImpl.ListAllTool.
// With includeTests: false and no projectName, declarations of projects whose .csproj references
// Microsoft.NET.Test.Sdk (TestProjectDetector.IsTestProject) are hidden and the StatusMessage says how
// many were hidden. An explicit projectName naming a test project always lists it.

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
public class ListAllIncludeTestsTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    private const string TestsSource = "public class UnitTest1 { public void TestMethod_InTestProject() { } } public class SharedName { }";
    private const string AppSource = "public class App { public void Run_InAppProject() { } } public class SharedName { }";
    private const string ExclusionPhrase = "hidden because includeTests is false";
    private const string IncludeTestsHint = "includeTests: true";

    private string _dir = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceReadNavigationImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ListAllIncludeTestsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "TestsProj"));
        Directory.CreateDirectory(Path.Combine(_dir, "AppProj"));
        File.WriteAllText(Path.Combine(_dir, "TestsProj", "TestsProj.csproj"), TestSdkCsproj);
        File.WriteAllText(Path.Combine(_dir, "AppProj", "AppProj.csproj"), PlainCsproj);

        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _impl = TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestsProj",
            Path.Combine(_dir, "TestsProj", "TestsProj.csproj"),
            [("UnitTest1.cs", TestsSource, Path.Combine(_dir, "TestsProj", "UnitTest1.cs"))]);

        var appProjectId = ProjectId.CreateNewId();
        var appProject = ProjectInfo.Create(appProjectId, VersionStamp.Default, "AppProj", "AppProj", LanguageNames.CSharp)
            .WithFilePath(Path.Combine(_dir, "AppProj", "AppProj.csproj"));
        solution = solution.AddProject(appProject)
            .AddDocument(DocumentId.CreateNewId(appProjectId, "App.cs"), "App.cs",
                SourceText.From(AppSource), filePath: Path.Combine(_dir, "AppProj", "App.cs"));

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static List<SolutionSymbolEntry> EntriesOf(SentinelCallToolResult<object> result) =>
        (List<SolutionSymbolEntry>)result.SuccessData!;

    // StatusMessage is null when no exclusion note and no other status applies; treat null as empty.
    private static string StatusOf(SentinelCallToolResult<object> result) => result.StatusMessage ?? string.Empty;

    [Test]
    public async Task IncludeTestsFalse_SkipsTestProjectDeclarations()
    {
        var result = await _impl.ListAll(reason: "test message", includeTests: false);

        Assert.That(result.IsError, Is.False);
        var names = EntriesOf(result).Select(e => e.Name).ToList();
        Assert.That(names, Does.Contain("Run_InAppProject"), "the non-test project must still be listed");
        Assert.That(names, Does.Not.Contain("TestMethod_InTestProject"), "the test project must be skipped when includeTests is false");
        Assert.That(StatusOf(result), Does.Contain(ExclusionPhrase));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task IncludeTestsTrue_ListsTestProjectDeclarations()
    {
        var result = await _impl.ListAll(reason: "test message", includeTests: true);

        Assert.That(result.IsError, Is.False);
        var names = EntriesOf(result).Select(e => e.Name).ToList();
        Assert.That(names, Does.Contain("TestMethod_InTestProject"));
        Assert.That(names, Does.Contain("Run_InAppProject"));
        Assert.That(StatusOf(result), Does.Not.Contain(ExclusionPhrase));
    }

    [Test]
    public async Task IncludeTestsOmitted_ListsTestProjectDeclarations()
    {
        var result = await _impl.ListAll(reason: "test message");

        Assert.That(result.IsError, Is.False);
        var names = EntriesOf(result).Select(e => e.Name).ToList();
        Assert.That(names, Does.Contain("TestMethod_InTestProject"), "the default for includeTests is true");
        Assert.That(StatusOf(result), Does.Not.Contain(ExclusionPhrase));
    }

    [Test]
    public async Task IncludeTestsFalse_ExplicitTestProjectName_ListsItsSymbolsWithoutExclusionNote()
    {
        var result = await _impl.ListAll(reason: "test message", projectName: "TestsProj", includeTests: false);

        Assert.That(result.IsError, Is.False);
        var names = EntriesOf(result).Select(e => e.Name).ToList();
        Assert.That(names, Does.Contain("TestMethod_InTestProject"), "naming the test project explicitly must list it even with includeTests: false");
        Assert.That(StatusOf(result), Does.Not.Contain(ExclusionPhrase));
    }

    [Test]
    public async Task IncludeTestsFalse_TruncatedPath_StatusMessageNamesTheExclusion()
    {
        var result = await _impl.ListAll(reason: "test message", includeTests: false, maxItems: 1);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.HasMoreData, Is.True, "two declarations exist in the included project and the cap is 1");
        Assert.That(StatusOf(result), Does.Contain(ExclusionPhrase));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task IncludeTestsFalse_ZeroMatchesOnlyBecauseOfExclusion_StatusMessageNamesTheExclusion()
    {
        var result = await _impl.ListAll(reason: "test message", includeTests: false,
            nameFilter: "TestMethod_InTestProject", exactMatch: true);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(StatusOf(result), Does.Contain(ExclusionPhrase));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task IncludeTestsFalse_NameInBothProjects_ReturnsOnlyAppEntryAndNamesTheTestProject()
    {
        var hidden = await _impl.ListAll(reason: "test message", kind: ListAllKind.@class, nameFilter: "SharedName", includeTests: false);

        Assert.That(hidden.IsError, Is.False);
        Assert.That(EntriesOf(hidden).Count, Is.EqualTo(1), "only the non-test project's SharedName may be listed");
        Assert.That(StatusOf(hidden), Does.Contain("TestsProj"), "the status message must name the hidden test project");
        Assert.That(StatusOf(hidden), Does.Contain(ExclusionPhrase));
        Assert.That(StatusOf(hidden), Does.Contain(IncludeTestsHint));

        var shown = await _impl.ListAll(reason: "test message", kind: ListAllKind.@class, nameFilter: "SharedName", includeTests: true);

        Assert.That(shown.IsError, Is.False);
        Assert.That(EntriesOf(shown).Count, Is.EqualTo(2), "includeTests: true must also list the test project's SharedName");
        Assert.That(StatusOf(shown), Does.Not.Contain(ExclusionPhrase));
    }
}
