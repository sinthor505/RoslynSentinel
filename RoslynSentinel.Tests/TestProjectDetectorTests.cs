using Microsoft.CodeAnalysis;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
public class TestProjectDetectorTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string PlainCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n";

    private string _dir = string.Empty;
    private AdhocWorkspace _workspace = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestProjectDetectorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspace = new AdhocWorkspace();
    }

    [TearDown]
    public void TearDown()
    {
        _workspace.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public void IsTestProject_WithTestSdkReference_ReturnsTrue()
    {
        var csproj = WriteCsproj("Tests.csproj", TestSdkCsproj);

        Assert.That(TestProjectDetector.IsTestProject(MakeProject(csproj)), Is.True);
    }

    [Test]
    public void IsTestProject_WithoutTestSdkReference_ReturnsFalse()
    {
        var csproj = WriteCsproj("Lib.csproj", PlainCsproj);

        Assert.That(TestProjectDetector.IsTestProject(MakeProject(csproj)), Is.False);
    }

    [Test]
    public void IsTestProject_MissingProjectFile_ReturnsFalse()
    {
        var missing = Path.Combine(_dir, "Missing.csproj");

        Assert.That(TestProjectDetector.IsTestProject(MakeProject(missing)), Is.False);
    }

    [Test]
    public void IsTestProject_AfterCsprojRewriteWithLaterWriteTime_RefreshesCachedResult()
    {
        var csproj = WriteCsproj("Refresh.csproj", TestSdkCsproj);
        var project = MakeProject(csproj);
        Assert.That(TestProjectDetector.IsTestProject(project), Is.True);

        File.WriteAllText(csproj, PlainCsproj);
        File.SetLastWriteTimeUtc(csproj, File.GetLastWriteTimeUtc(csproj).AddMinutes(1));

        Assert.That(TestProjectDetector.IsTestProject(project), Is.False);
    }

    private string WriteCsproj(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private Project MakeProject(string filePath)
    {
        var info = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "Probe",
            assemblyName: "Probe",
            language: LanguageNames.CSharp,
            filePath: filePath);
        return _workspace.AddProject(info);
    }
}
