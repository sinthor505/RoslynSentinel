// Coverage for the includeTests parameter of SymbolRelationshipImpl.FindReferencesTool. With includeTests:
// false, callers whose file lies in a test project (TestProjectDetector.IsTestProject) are dropped before
// the summary is built and the StatusMessage ends with a dropped-hits note. A filePath that itself lies
// in a test project disables the filter. The test project references AppProj so its caller binds to Svc.

using System.Collections;
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
public class FindReferencesIncludeTestsTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    // Test project: a caller of Svc.DoWork and Svc.OnlyFromTests, plus a helper whose Assist is only called inside the test project.
    private const string TestsSource =
        "public class TestCaller { public void Run() { new Svc().DoWork(); new Svc().OnlyFromTests(); } }\n" +
        "public class TestHelper { public void Assist() { } public void UseAssist() { new TestHelper().Assist(); } }";

    // App project: Svc declares DoWork (called from AppCaller and TestCaller) and OnlyFromTests (called only from TestCaller).
    private const string AppSource =
        "public class Svc { public void DoWork() { } public void OnlyFromTests() { } }\n" +
        "public class AppCaller { public void Run() { new Svc().DoWork(); } }";

    private const string DoWorkSnippet = "public void DoWork()";
    private const string OnlyFromTestsSnippet = "public void OnlyFromTests()";
    private const string AssistSnippet = "public void Assist()";
    private const string NoteFragment = "test project(s) excluded because includeTests is false";
    private const string IncludeTestsHint = "includeTests: true";

    private string _dir = string.Empty;
    private string _appDocPath = string.Empty;
    private string _testDocPath = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolRelationshipImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "FindReferencesIncludeTestsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "TestsProj"));
        Directory.CreateDirectory(Path.Combine(_dir, "AppProj"));
        File.WriteAllText(Path.Combine(_dir, "TestsProj", "TestsProj.csproj"), TestSdkCsproj);
        File.WriteAllText(Path.Combine(_dir, "AppProj", "AppProj.csproj"), PlainCsproj);
        _appDocPath = Path.Combine(_dir, "AppProj", "App.cs");
        _testDocPath = Path.Combine(_dir, "TestsProj", "UnitTest1.cs");

        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _impl = TestTools.SymbolRelationshipImpl(
            TestEngines.DiscoveryEngine(_workspaceManager),
            TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            NullLogger<SymbolRelationshipImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestsProj",
            Path.Combine(_dir, "TestsProj", "TestsProj.csproj"),
            [("UnitTest1.cs", TestsSource, _testDocPath)]);
        var testsId = solution.Projects.Single(p => p.Name == "TestsProj").Id;
        var testsProject = solution.GetProject(testsId)!;

        var appId = ProjectId.CreateNewId();
        var appProject = ProjectInfo.Create(appId, VersionStamp.Default, "AppProj", "AppProj", LanguageNames.CSharp)
            .WithFilePath(Path.Combine(_dir, "AppProj", "AppProj.csproj"))
            .WithMetadataReferences(testsProject.MetadataReferences);
        solution = solution.AddProject(appProject)
            .AddDocument(DocumentId.CreateNewId(appId, "App.cs"), "App.cs", SourceText.From(AppSource), filePath: _appDocPath)
            .AddProjectReference(testsId, new ProjectReference(appId));

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // StatusMessage is null when nothing is excluded; treat null as empty for negative checks.
    private static string StatusOf(SentinelCallToolResult<object> result) => result.StatusMessage ?? string.Empty;

    // SuccessData is an anonymous object; read a named list property and its FilePath values by reflection.
    private static List<string> PathsIn(SentinelCallToolResult<object> result, string listProperty)
    {
        var list = (IEnumerable)result.SuccessData!.GetType().GetProperty(listProperty)!.GetValue(result.SuccessData)!;
        return list.Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("FilePath")!.GetValue(item)!)
            .ToList();
    }

    private static bool IsInTestDoc(string path) => path.EndsWith("UnitTest1.cs", StringComparison.OrdinalIgnoreCase);
    private static bool IsInAppDoc(string path) => path.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task Callers_IncludeTestsFalse_DropsTestCallerKeepsAppCallerAndNote()
    {
        var result = await _impl.FindReferences(reason: "test message", symbolName: "DoWork", kind: FindReferencesKind.callers,
            filePath: _appDocPath, contextSnippet: DoWorkSnippet, includeTests: false);

        Assert.That(result.IsError, Is.False);
        var paths = PathsIn(result, "callers");
        Assert.That(paths.Any(IsInAppDoc), Is.True, "the app project caller must be kept");
        Assert.That(paths.Any(IsInTestDoc), Is.False, "the test project caller must be dropped when includeTests is false");
        Assert.That(StatusOf(result), Does.Contain(NoteFragment));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task Callers_IncludeTestsTrue_KeepsBothAndNoNote()
    {
        var result = await _impl.FindReferences(reason: "test message", symbolName: "DoWork", kind: FindReferencesKind.callers,
            filePath: _appDocPath, contextSnippet: DoWorkSnippet, includeTests: true);

        Assert.That(result.IsError, Is.False);
        var paths = PathsIn(result, "callers");
        Assert.That(paths.Any(IsInAppDoc), Is.True);
        Assert.That(paths.Any(IsInTestDoc), Is.True, "includeTests: true must keep the test project caller");
        Assert.That(StatusOf(result), Does.Not.Contain(NoteFragment));
    }

    [Test]
    public async Task Callers_IncludeTestsOmitted_KeepsBothAndNoNote()
    {
        var result = await _impl.FindReferences(reason: "test message", symbolName: "DoWork", kind: FindReferencesKind.callers,
            filePath: _appDocPath, contextSnippet: DoWorkSnippet);

        Assert.That(result.IsError, Is.False);
        var paths = PathsIn(result, "callers");
        Assert.That(paths.Any(IsInTestDoc), Is.True, "the default for includeTests is true");
        Assert.That(StatusOf(result), Does.Not.Contain(NoteFragment));
    }

    [Test]
    public async Task All_IncludeTestsFalse_DropsTestCallerKeepsAppCallerAndNote()
    {
        var result = await _impl.FindReferences(reason: "test message", symbolName: "DoWork", kind: FindReferencesKind.all,
            filePath: _appDocPath, contextSnippet: DoWorkSnippet, includeTests: false);

        Assert.That(result.IsError, Is.False);
        var callerPaths = PathsIn(result, "callers");
        Assert.That(callerPaths.Any(IsInAppDoc), Is.True);
        Assert.That(callerPaths.Any(IsInTestDoc), Is.False);
        Assert.That(StatusOf(result), Does.Contain(NoteFragment));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }

    [Test]
    public async Task All_IncludeTestsTrue_KeepsBothAndNoNote()
    {
        var result = await _impl.FindReferences(reason: "test message", symbolName: "DoWork", kind: FindReferencesKind.all,
            filePath: _appDocPath, contextSnippet: DoWorkSnippet, includeTests: true);

        Assert.That(result.IsError, Is.False);
        var callerPaths = PathsIn(result, "callers");
        Assert.That(callerPaths.Any(IsInAppDoc), Is.True);
        Assert.That(callerPaths.Any(IsInTestDoc), Is.True);
        Assert.That(StatusOf(result), Does.Not.Contain(NoteFragment));
    }

    [Test]
    public async Task Callers_FilePathInsideTestProject_IncludeTestsFalse_KeepsTestHitsWithoutNote()
    {
        // The symbol is declared in the test project, so the filter is disabled and its test-project caller stays.
        var result = await _impl.FindReferences(reason: "test message", symbolName: "Assist", kind: FindReferencesKind.callers,
            filePath: _testDocPath, contextSnippet: AssistSnippet, includeTests: false);

        Assert.That(result.IsError, Is.False);
        var paths = PathsIn(result, "callers");
        Assert.That(paths.Any(IsInTestDoc), Is.True, "a filePath inside the test project must disable the test-hit filter");
        Assert.That(StatusOf(result), Does.Not.Contain(NoteFragment));
    }

    [Test]
    public async Task Callers_EverythingDropped_IncludeTestsFalse_StillShowsNote()
    {
        // OnlyFromTests is called only from the test project, so every caller is dropped.
        var result = await _impl.FindReferences(reason: "test message", symbolName: "OnlyFromTests", kind: FindReferencesKind.callers,
            filePath: _appDocPath, contextSnippet: OnlyFromTestsSnippet, includeTests: false);

        Assert.That(result.IsError, Is.False);
        Assert.That(PathsIn(result, "callers"), Is.Empty);
        Assert.That(StatusOf(result), Does.Contain(NoteFragment));
        Assert.That(StatusOf(result), Does.Contain(IncludeTestsHint));
    }
}
