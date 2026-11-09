namespace RoslynSentinel.Tests.Basic;

// Pure unit tests for WriteRefusal.TryMap: the write-chokepoint refusal to structured ResultError mapping.
// No workspace and no manager; ApplyChangesResult is built directly. Dictionary enumeration order is insertion
// order (no removals), which the "first 10 listed" assertions rely on, matching the production Take(10).
[TestFixture]
public class WriteRefusalTests
{
    private const string ToolName = "WriteFile";
    private const string Dir = @"C:\repo\src\";

    private static ApplyChangesResult Refused(string? refusalCode, bool isError, params string[] files)
    {
        var failed = new Dictionary<FilePathWrapper, string>();
        foreach (var file in files)
        {
            failed.Add(new FilePathWrapper(file), "changed on disk since your last read");
        }

        return new ApplyChangesResult(
            IsError: isError,
            SucceededFiles: new List<string>(),
            FailedFiles: failed,
            Summary: "refused",
            RefusalCode: refusalCode);
    }

    private static string FileNamed(int index) => $@"{Dir}F{index:D2}.cs";

    [Test]
    public void StaleReadSingleFile_MapsToStaleReadErrorNamingFileToolAndReread()
    {
        var file = FileNamed(1);
        var result = Refused(ToolErrorCode.StaleRead, true, file);

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead));
        Assert.That(error.Message, Does.Contain(file));
        Assert.That(error.Message, Does.Contain(ToolName));
        Assert.That(error.Message, Does.Contain("ReadFile"));
        Assert.That(error.Message, Does.Contain("wait a moment"));
        Assert.That(error.StructuredDetail, Is.Null);
    }

    [Test]
    public void StaleReadManyFiles_ListsFirstTenAndCountsRestWithStructuredDetailForAll()
    {
        var files = Enumerable.Range(1, 25).Select(FileNamed).ToArray();
        var result = Refused(ToolErrorCode.StaleRead, true, files);

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead));
        for (var i = 0; i < 10; i++)
        {
            Assert.That(error.Message, Does.Contain(files[i]), $"first ten should be listed: {files[i]}");
        }

        for (var i = 10; i < 25; i++)
        {
            Assert.That(error.Message, Does.Not.Contain(files[i]), $"file beyond the first ten should not be listed: {files[i]}");
        }

        Assert.That(error.Message, Does.Contain("and 15 more"));
        Assert.That(error.Message, Does.Contain("25 target file(s)"));

        Assert.That(error.StructuredDetail, Is.Not.Null);
        var details = error.StructuredDetail!.Select(d => d.ToString()).ToList();
        Assert.That(details, Has.Count.EqualTo(25));
        for (var i = 0; i < files.Length; i++)
        {
            Assert.That(details[i], Does.Contain(files[i]));
        }
    }

    [Test]
    public void StaleReadWithNoFailedFiles_ReturnsTrueWithoutThrowing()
    {
        var result = Refused(ToolErrorCode.StaleRead, true);

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead));
        Assert.That(error.Message, Does.Contain("ReadFile"));
        Assert.That(error.StructuredDetail, Is.Null);
    }

    [Test]
    public void UnmappedRefusalCode_ReturnsFalseAndNullError()
    {
        var result = Refused(ToolErrorCode.EolChangeRefused, true, FileNamed(1));

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.False);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void NotAnError_ReturnsFalseEvenWithStaleReadCode()
    {
        var result = Refused(ToolErrorCode.StaleRead, false, FileNamed(1));

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.False);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void ErrorWithoutRefusalCode_ReturnsFalseAndNullError()
    {
        var result = Refused(null, true, FileNamed(1));

        var mapped = WriteRefusal.TryMap(ToolName, result, out var error);

        Assert.That(mapped, Is.False);
        Assert.That(error, Is.Null);
    }
}
