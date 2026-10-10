// Coverage for the includeTests parameter of SymbolNavigationEngine.LocateSymbolAsync (engine layer)
// and SymbolNavigationImpl.LocateSymbol (impl layer). With includeTests: false and no projectName or
// filePath, symbols declared in test projects (TestProjectDetector.IsTestProject) are not searched; an
// explicit projectName naming a test project still finds its symbols.

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
public class LocateSymbolIncludeTestsTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    private const string TestsSource = "public class TestMarkerType { }";
    private const string AppSource = "public class AppMarkerType { }";
    private const string ExclusionPhrase = "test project(s) were excluded";
    private const string IncludeTestsHint = "includeTests: true";
    private const string TestOnlySymbol = "TestMarkerType";
    private const string AppOnlySymbol = "AppMarkerType";

    private string _dir = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolNavigationEngine _engine = null!;
    private SymbolNavigationImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LocateSymbolIncludeTestsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "TestsProj"));
        Directory.CreateDirectory(Path.Combine(_dir, "AppProj"));
        File.WriteAllText(Path.Combine(_dir, "TestsProj", "TestsProj.csproj"), TestSdkCsproj);
        File.WriteAllText(Path.Combine(_dir, "AppProj", "AppProj.csproj"), PlainCsproj);

        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _impl = new SymbolNavigationImpl(
            _engine,
            new ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance),
            _workspaceManager,
            NullLogger<SymbolNavigationImpl>.Instance);

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

    // StatusMessage is null when no exclusion applies; treat null as empty for negative checks.
    private static string StatusOf(SentinelCallToolResult<object> result) => result.StatusMessage ?? string.Empty;

    // ---- Engine layer ----

    [Test]
    public async Task Engine_IncludeTestsFalse_AppSymbolFound()
    {
        var locations = await _engine.LocateSymbolAsync(AppOnlySymbol, includeTests: false);

        Assert.That(locations.Select(l => l.ProjectName), Does.Contain("AppProj"));
    }

    [Test]
    public async Task Engine_IncludeTestsFalse_TestOnlySymbolNotReturned()
    {
        var locations = await _engine.LocateSymbolAsync(TestOnlySymbol, includeTests: false);

        Assert.That(locations, Is.Empty, "a symbol declared only in a test project must not be returned when includeTests is false");
    }

    [Test]
    public async Task Engine_IncludeTestsTrue_TestOnlySymbolReturned()
    {
        var locations = await _engine.LocateSymbolAsync(TestOnlySymbol, includeTests: true);

        Assert.That(locations.Select(l => l.ProjectName), Does.Contain("TestsProj"));
    }

    [Test]
    public async Task Engine_IncludeTestsOmitted_TestOnlySymbolReturned()
    {
        var locations = await _engine.LocateSymbolAsync(TestOnlySymbol);

        Assert.That(locations.Select(l => l.ProjectName), Does.Contain("TestsProj"), "the default for includeTests is true");
    }

    [Test]
    public async Task Engine_IncludeTestsFalse_ExplicitTestProjectName_ReturnsSymbol()
    {
        var locations = await _engine.LocateSymbolAsync(TestOnlySymbol, projectName: "TestsProj", includeTests: false);

        Assert.That(locations.Select(l => l.ProjectName), Does.Contain("TestsProj"),
            "naming the test project explicitly must search it even with includeTests: false");
    }

    // ---- Impl layer ----

    [Test]
    public async Task Impl_IncludeTestsFalse_AppHit_StatusMessageNamesTheExclusion()
    {
        var result = await _impl.LocateSymbol(reason: "test message", symbolName: AppOnlySymbol, includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(StatusOf(result), Does.Contain(ExclusionPhrase));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task Impl_IncludeTestsFalse_TestOnlySymbol_ReturnsNotFoundNamingTheExclusion()
    {
        var result = await _impl.LocateSymbol(reason: "test message", symbolName: TestOnlySymbol, includeTests: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain(ExclusionPhrase));
    }

    [Test]
    public async Task Impl_IncludeTestsTrue_TestOnlySymbol_FoundWithoutExclusionNote()
    {
        var result = await _impl.LocateSymbol(reason: "test message", symbolName: TestOnlySymbol, includeTests: true);

        Assert.That(result.IsError, Is.False);
        Assert.That(StatusOf(result), Does.Not.Contain(ExclusionPhrase));
    }

    [Test]
    public async Task Impl_IncludeTestsOmitted_TestOnlySymbol_Found()
    {
        var result = await _impl.LocateSymbol(reason: "test message", symbolName: TestOnlySymbol);

        Assert.That(result.IsError, Is.False, "the default for includeTests is true");
        Assert.That(StatusOf(result), Does.Not.Contain(ExclusionPhrase));
    }
}
