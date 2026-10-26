using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class EditorConfigEolReaderTests
{
    private string _root = string.Empty;

    [SetUp]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "EditorConfigEolReaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static string WriteEditorConfig(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".editorconfig");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void Read_RootOnlyNoEndOfLine_ReturnsNull()
    {
        WriteEditorConfig(_root, "root = true\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(_root, "Foo.cs")), Is.Null);
    }

    [Test]
    public void Read_CsSectionLf_ReturnsLf()
    {
        WriteEditorConfig(_root, "root = true\n[*.cs]\nend_of_line = lf\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(_root, "Foo.cs")), Is.EqualTo("lf"));
    }

    [Test]
    public void Read_AllFilesCrlf_ReturnsCrlf()
    {
        WriteEditorConfig(_root, "root = true\n[*]\nend_of_line = crlf\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(_root, "Foo.cs")), Is.EqualTo("crlf"));
    }

    [Test]
    public void Read_VbSectionOnly_ReturnsNullForCsFile()
    {
        WriteEditorConfig(_root, "root = true\n[*.vb]\nend_of_line = crlf\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(_root, "Foo.cs")), Is.Null);
    }

    [Test]
    public void Read_UppercaseValue_ReturnsLowercase()
    {
        WriteEditorConfig(_root, "root = true\n[*.cs]\nend_of_line = CRLF\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(_root, "Foo.cs")), Is.EqualTo("crlf"));
    }

    [Test]
    public void Read_NestedChildOverridesParent()
    {
        var child = Path.Combine(_root, "child");
        WriteEditorConfig(_root, "root = true\n[*.cs]\nend_of_line = lf\n");
        WriteEditorConfig(child, "[*.cs]\nend_of_line = crlf\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(child, "Foo.cs")), Is.EqualTo("crlf"));
    }

    [Test]
    public void Read_ChildRootStopsWalkBeforeParent()
    {
        var child = Path.Combine(_root, "child");
        WriteEditorConfig(_root, "[*.cs]\nend_of_line = lf\n");
        WriteEditorConfig(child, "root = true\n");
        Assert.That(EditorConfigEolReader.Read(Path.Combine(child, "Foo.cs")), Is.Null);
    }

    [Test]
    public void Read_ConfigRewrittenWithLaterTimestamp_ReturnsNewValue()
    {
        var path = WriteEditorConfig(_root, "root = true\n[*.cs]\nend_of_line = lf\n");
        var file = Path.Combine(_root, "Foo.cs");
        Assert.That(EditorConfigEolReader.Read(file), Is.EqualTo("lf"));
        File.WriteAllText(path, "root = true\n[*.cs]\nend_of_line = crlf\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.That(EditorConfigEolReader.Read(file), Is.EqualTo("crlf"));
    }

    [Test]
    public void Read_EmptyOrInvalidPath_ReturnsNullWithoutThrowing()
    {
        Assert.That(EditorConfigEolReader.Read(""), Is.Null);
        Assert.That(EditorConfigEolReader.Read("bad\0name.cs"), Is.Null);
    }
}
