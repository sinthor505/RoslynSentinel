using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Reads project file paths from .sln, .slnx and direct project paths against real files in a temp folder.</summary>
[TestFixture]
public class SolutionProjectPathsTests
{
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "SolutionProjectPathsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    [Test]
    public void Read_Sln_ReturnsOnlyProjectFilesAndSkipsSolutionFolder()
    {
        var a = Touch("src/A/A.csproj");
        var b = Touch("src/B/B.vbproj");
        var solution = WriteText("Test.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            $"Project(\"{{{SolutionFolderTypeGuid}}}\") = \"Docs\", \"Docs\", \"{{11111111-1111-1111-1111-111111111111}}\"\r\n" +
            "EndProject\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\", \"src\\A\\A.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\r\n" +
            "EndProject\r\n" +
            "Project(\"{F184B08F-C81C-45F6-A57F-5ABD9991F28F}\") = \"B\", \"src\\B\\B.vbproj\", \"{33333333-3333-3333-3333-333333333333}\"\r\n" +
            "EndProject\r\n");

        Assert.That(SolutionProjectPaths.Read(solution), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public void Read_Slnx_ResolvesNestedAndRootForwardSlashPaths()
    {
        var a = Touch("src/A/A.csproj");
        var b = Touch("B/B.csproj");
        var solution = WriteText("Test.slnx",
            "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A/A.csproj\" /></Folder><Project Path=\"B/B.csproj\" /></Solution>");

        Assert.That(SolutionProjectPaths.Read(solution), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public void Read_Slnx_SkipsProjectEntryWhoseFileDoesNotExist()
    {
        var a = Touch("src/A/A.csproj");
        var solution = WriteText("Missing.slnx",
            "<Solution><Project Path=\"src/A/A.csproj\" /><Project Path=\"Gone/Gone.csproj\" /></Solution>");

        Assert.That(SolutionProjectPaths.Read(solution), Is.EqualTo(new[] { a }));
    }

    [Test]
    public void Read_ProjectPath_ReturnsThatSingleFullPath()
    {
        var a = Touch("src/A/A.csproj");

        Assert.That(SolutionProjectPaths.Read(a), Is.EqualTo(new[] { a }));
    }

    [Test]
    public void Read_NonexistentProjectPath_ReturnsEmpty()
    {
        var missing = Path.Combine(_root, "nope", "Nope.csproj");

        Assert.That(SolutionProjectPaths.Read(missing), Is.Empty);
    }

    [Test]
    public void Read_GarbageSlnx_ReturnsEmptyWithoutThrowing()
    {
        var garbage = WriteText("Garbage.slnx", "this is not xml <<<");
        IReadOnlyList<string> result = Array.Empty<string>();

        Assert.DoesNotThrow(() => result = SolutionProjectPaths.Read(garbage));
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Read_SlnWithoutProjectLines_ReturnsEmptyWithoutThrowing()
    {
        var noProjects = WriteText("Empty.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\r\n");
        IReadOnlyList<string> result = Array.Empty<string>();

        Assert.DoesNotThrow(() => result = SolutionProjectPaths.Read(noProjects));
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Read_DuplicateEntriesDifferingInCase_ReturnsOnce()
    {
        var a = Touch("src/A/A.csproj");
        var solution = WriteText("Dup.slnx",
            "<Solution><Project Path=\"src/A/A.csproj\" /><Project Path=\"src/A/A.csproj\" /><Project Path=\"SRC/a/a.csproj\" /></Solution>");

        Assert.That(SolutionProjectPaths.Read(solution), Is.EqualTo(new[] { a }));
    }

    /// <summary>Creates an empty file at a forward-slash relative path under the temp root and returns its full path.</summary>
    private string Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return Path.GetFullPath(path);
    }

    /// <summary>Writes a text file under the temp root and returns its full path.</summary>
    private string WriteText(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
