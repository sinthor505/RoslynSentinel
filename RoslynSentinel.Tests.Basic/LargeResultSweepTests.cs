using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class LargeResultSweepTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void SweepExpired_DeletesFilesOlderThanMaxAge()
    {
        var now = DateTime.UtcNow;
        var dir = Path.Combine(_tempRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "largeresult_20200101T000000Z_a.json");
        File.WriteAllText(file, "{}");
        File.SetLastWriteTimeUtc(file, now.AddDays(-8));

        var maxAge = TimeSpan.FromDays(LargeResultHelper.RetentionDays);
        var result = LargeResultHelper.SweepExpired(_tempRoot, maxAge, now);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(file), Is.False);
    }

    [Test]
    public void SweepExpired_KeepsFilesNewerThanMaxAge()
    {
        var now = DateTime.UtcNow;
        var dir = Path.Combine(_tempRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "largeresult_20200101T000000Z_a.json");
        File.WriteAllText(file, "{}");
        File.SetLastWriteTimeUtc(file, now.AddDays(-6));

        var maxAge = TimeSpan.FromDays(LargeResultHelper.RetentionDays);
        var result = LargeResultHelper.SweepExpired(_tempRoot, maxAge, now);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(file), Is.True);
    }

    [Test]
    public void SweepExpired_IgnoresNonMatchingNamesAndSubfolders()
    {
        var now = DateTime.UtcNow;
        var dir = Path.Combine(_tempRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);

        var notes = Path.Combine(dir, "notes.txt");
        File.WriteAllText(notes, "text");
        File.SetLastWriteTimeUtc(notes, now.AddDays(-8));

        var subdir = Path.Combine(dir, "subdir");
        Directory.CreateDirectory(subdir);
        var subfolder = Path.Combine(subdir, "largeresult_x.json");
        File.WriteAllText(subfolder, "{}");
        File.SetLastWriteTimeUtc(subfolder, now.AddDays(-8));

        var maxAge = TimeSpan.FromDays(LargeResultHelper.RetentionDays);
        var result = LargeResultHelper.SweepExpired(_tempRoot, maxAge, now);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(notes), Is.True);
        Assert.That(File.Exists(subfolder), Is.True);
    }

    [Test]
    public void SweepExpired_MissingDirectory_ReturnsZero()
    {
        var now = DateTime.UtcNow;
        var maxAge = TimeSpan.FromDays(LargeResultHelper.RetentionDays);
        var result = LargeResultHelper.SweepExpired(_tempRoot, maxAge, now);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SweepExpired_FileHeldOpen_IsSkippedWithoutThrowing()
    {
        var now = DateTime.UtcNow;
        var dir = Path.Combine(_tempRoot, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "largeresult_20200101T000000Z_a.json");
        File.WriteAllText(file, "{}");
        File.SetLastWriteTimeUtc(file, now.AddDays(-8));

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var maxAge = TimeSpan.FromDays(LargeResultHelper.RetentionDays);
            var result = LargeResultHelper.SweepExpired(_tempRoot, maxAge, now);
            Assert.That(result, Is.EqualTo(0));
            Assert.That(File.Exists(file), Is.True);
        }
    }
}
