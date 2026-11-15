using System.Reflection;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// A bare file name (no directory part) that matches exactly one loaded document continues with that document in
/// ReadFile and GetFileOutline and carries a FileResolved finding. Two loaded files with that name keep the
/// FileNotFound error, which lists both paths. Every test calls the real tool entry point.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileBareNameAutoResolveTests
{
    private const string UniqueSource = "class Unique { public int Value() => 1; }";
    private const string DupSource = "class Dup { }";

    private string _root = "";
    private FakeWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "fbn_" + Guid.NewGuid().ToString("N"));
        _workspaceManager = FakeWorkspaceManager.ReadOnly();
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            Path.Combine(_root, "TestProj.csproj"),
            [
                ("Unique.cs", UniqueSource, Path.Combine(_root, "Helpers", "Unique.cs")),
                ("Dup.cs", DupSource, Path.Combine(_root, "Helpers", "Dup.cs")),
                ("Dup.cs", DupSource, Path.Combine(_root, "Other", "Dup.cs")),
            ]));
        _tools = TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task ReadFile_UniqueBareName_ResolvesToTheLoadedFileAndCarriesFileResolvedFinding()
    {
        var result = await _tools.ReadFileTool(reason: "test message", "Unique.cs");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That((string)GetProp(result.SuccessData!, "filePath")!, Does.EndWith(Path.Combine("Helpers", "Unique.cs")));
        Assert.That((string)GetProp(result.SuccessData!, "source")!, Does.Contain("class Unique"));
        Assert.That(result.Findings, Has.Some.Matches<Finding>(f => f.Source == "FileResolved" && f.Message.Contains("Unique.cs")));
    }

    [Test]
    public async Task GetFileOutline_UniqueBareName_ResolvesToTheLoadedFileAndCarriesFileResolvedFinding()
    {
        var result = await _tools.GetFileOutlineTool(reason: "test message", "Unique.cs");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.Symbols, Is.Not.Empty);
        Assert.That(result.Findings, Has.Some.Matches<Finding>(f => f.Source == "FileResolved" && f.Message.Contains("Unique.cs")));
    }

    [Test]
    public async Task ReadFile_AmbiguousBareName_ReturnsFileNotFoundListingBothPaths()
    {
        var result = await _tools.ReadFileTool(reason: "test message", "Dup.cs");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("FileNotFound"));
        Assert.That(result.ErrorData!.Message, Does.Contain(Path.Combine(_root, "Helpers", "Dup.cs")));
        Assert.That(result.ErrorData!.Message, Does.Contain(Path.Combine(_root, "Other", "Dup.cs")));
    }

    [Test]
    public async Task GetFileOutline_AmbiguousBareName_ReturnsFileNotFoundListingBothPaths()
    {
        var result = await _tools.GetFileOutlineTool(reason: "test message", "Dup.cs");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("FileNotFound"));
        Assert.That(result.ErrorData!.Message, Does.Contain(Path.Combine(_root, "Helpers", "Dup.cs")));
        Assert.That(result.ErrorData!.Message, Does.Contain(Path.Combine(_root, "Other", "Dup.cs")));
    }
}
