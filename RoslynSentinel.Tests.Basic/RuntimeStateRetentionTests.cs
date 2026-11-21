using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class RuntimeStateRetentionTests
{
    private static readonly DateTime NowUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

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

    private string OperationsDir => Path.Combine(_tempRoot, ".roslynsentinel", "operations");

    private string ValidationDir => Path.Combine(_tempRoot, ".roslynsentinel", "validation");

    private string BuildRoot => Path.Combine(_tempRoot, ".roslynsentinel", "build");

    private static string WriteFileAged(string path, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static void SetTreeWriteTimeUtc(string dir, DateTime writtenUtc)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, writtenUtc);
        }

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(sub, writtenUtc);
        }

        Directory.SetLastWriteTimeUtc(dir, writtenUtc);
    }

    [Test]
    public void SweepOperations_BlobOlderThanMaxAge_DeletesBlobAndItsUndoneMarker()
    {
        var blob = WriteFileAged(Path.Combine(OperationsDir, "apply_20261105T000000Z_0000000_a.json"), NowUtc.AddDays(-15));
        var marker = WriteFileAged(Path.ChangeExtension(blob, OperationBlobWriter.UndoneMarkerExtension), NowUtc.AddDays(-15));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(blob), Is.False);
        Assert.That(File.Exists(marker), Is.False);
    }

    [Test]
    public void SweepOperations_BlobNewerThanMaxAge_IsKept()
    {
        var blob = WriteFileAged(Path.Combine(OperationsDir, "apply_20261107T000000Z_0000000_b.json"), NowUtc.AddDays(-13));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(blob), Is.True);
    }

    [Test]
    public void SweepOperations_BlobWithinProtectedAge_IsKeptEvenOverMaxCount()
    {
        var blob = WriteFileAged(Path.Combine(OperationsDir, "apply_20261120T000000Z_0000000_c.json"), NowUtc.AddHours(-12));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), 0, NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(blob), Is.True);
    }

    [Test]
    public void SweepOperations_OverMaxCount_DeletesOldestSurvivorsAndKeepsNewest()
    {
        var ages = new[] { 2, 3, 4, 5, 6, 7 };
        var blobs = ages
            .Select(age => (Age: age, Path: WriteFileAged(Path.Combine(OperationsDir, $"apply_age{age}d_0000000_d.json"), NowUtc.AddDays(-age))))
            .ToList();

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), 3, NowUtc);

        Assert.That(result, Is.EqualTo(3));
        foreach (var blob in blobs.Where(b => b.Age >= 5))
        {
            Assert.That(File.Exists(blob.Path), Is.False, $"Blob aged {blob.Age} days should have been trimmed");
        }

        foreach (var blob in blobs.Where(b => b.Age <= 4))
        {
            Assert.That(File.Exists(blob.Path), Is.True, $"Blob aged {blob.Age} days should have been kept");
        }
    }

    [Test]
    public void SweepOperations_OrphanUndoneMarkerOlderThanMaxAge_IsDeleted()
    {
        var marker = WriteFileAged(Path.Combine(OperationsDir, "apply_20261101T000000Z_0000000_e.undone"), NowUtc.AddDays(-15));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(0), "Only blobs are counted");
        Assert.That(File.Exists(marker), Is.False);
    }

    [Test]
    public void SweepOperations_OrphanUndoneMarkerNewerThanMaxAge_IsKept()
    {
        var marker = WriteFileAged(Path.Combine(OperationsDir, "apply_20261107T000000Z_0000000_f.undone"), NowUtc.AddDays(-13));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(marker), Is.True);
    }

    [Test]
    public void SweepOperations_SubfolderAndNonMatchingFiles_AreUntouched()
    {
        var note = WriteFileAged(Path.Combine(OperationsDir, "note.txt"), NowUtc.AddDays(-30));
        var nested = WriteFileAged(Path.Combine(OperationsDir, "sub", "apply_old.json"), NowUtc.AddDays(-30));

        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(note), Is.True);
        Assert.That(File.Exists(nested), Is.True);
    }

    [Test]
    public void SweepOperations_MissingFolder_ReturnsZero()
    {
        var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SweepOperations_BlobHeldOpen_IsSkippedWithoutThrowing()
    {
        var blob = WriteFileAged(Path.Combine(OperationsDir, "apply_20261101T000000Z_0000000_g.json"), NowUtc.AddDays(-15));

        using (new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = RuntimeStateRetention.SweepOperations(_tempRoot, TimeSpan.FromDays(14), RuntimeStateRetention.OperationsMaxCount, NowUtc);

            // Windows refuses the delete while the handle is open; other platforms may allow it.
            Assert.That(result, Is.EqualTo(0).Or.EqualTo(1));
            Assert.That(File.Exists(blob), Is.EqualTo(result == 0));
        }
    }

    [Test]
    public void SweepValidation_OldValidationFile_IsDeleted()
    {
        var file = WriteFileAged(Path.Combine(ValidationDir, "validation_20261110T000000Z_a.json"), NowUtc.AddDays(-8));

        var result = RuntimeStateRetention.SweepValidation(_tempRoot, TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(file), Is.False);
    }

    [Test]
    public void SweepValidation_NewerValidationFile_IsKept()
    {
        var file = WriteFileAged(Path.Combine(ValidationDir, "validation_20261114T000000Z_b.json"), NowUtc.AddDays(-6));

        var result = RuntimeStateRetention.SweepValidation(_tempRoot, TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(file), Is.True);
    }

    [Test]
    public void SweepValidation_NonValidationFileOld_IsKept()
    {
        var file = WriteFileAged(Path.Combine(ValidationDir, "other_20261101.json"), NowUtc.AddDays(-30));

        var result = RuntimeStateRetention.SweepValidation(_tempRoot, TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(file), Is.True);
    }

    [Test]
    public void SweepValidation_MissingFolder_ReturnsZero()
    {
        var result = RuntimeStateRetention.SweepValidation(_tempRoot, TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SweepBuildInstances_StaleNamedDirectory_IsRemoved()
    {
        var instance = Path.Combine(BuildRoot, "test-10752-1");
        WriteFileAged(Path.Combine(instance, "bin", "x.dll"), NowUtc.AddDays(-10));
        WriteFileAged(Path.Combine(instance, "readme.txt"), NowUtc.AddDays(-10));
        SetTreeWriteTimeUtc(instance, NowUtc.AddDays(-10));

        var result = RuntimeStateRetention.SweepBuildInstances(BuildRoot, "test-current-1", TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(Directory.Exists(instance), Is.False);
    }

    [Test]
    public void SweepBuildInstances_CurrentInstanceId_IsKeptEvenWhenStale()
    {
        var instance = Path.Combine(BuildRoot, "test-current-1");
        WriteFileAged(Path.Combine(instance, "readme.txt"), NowUtc.AddDays(-10));
        SetTreeWriteTimeUtc(instance, NowUtc.AddDays(-10));

        var result = RuntimeStateRetention.SweepBuildInstances(BuildRoot, "test-current-1", TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(Directory.Exists(instance), Is.True);
    }

    [Test]
    public void SweepBuildInstances_FreshFileTwoLevelsDown_KeepsDirectory()
    {
        var instance = Path.Combine(BuildRoot, "test-26936-1");
        WriteFileAged(Path.Combine(instance, "readme.txt"), NowUtc.AddDays(-10));
        WriteFileAged(Path.Combine(instance, "bin", "x.dll"), NowUtc.AddDays(-10));
        SetTreeWriteTimeUtc(instance, NowUtc.AddDays(-10));
        // Written after the tree was aged: only the file two levels down is fresh.
        File.SetLastWriteTimeUtc(Path.Combine(instance, "bin", "x.dll"), NowUtc.AddHours(-1));

        var result = RuntimeStateRetention.SweepBuildInstances(BuildRoot, "test-current-1", TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(Directory.Exists(instance), Is.True);
    }

    [Test]
    public void SweepBuildInstances_LooseGitignoreFile_IsNeverTouched()
    {
        Directory.CreateDirectory(BuildRoot);
        var gitignore = WriteFileAged(Path.Combine(BuildRoot, ".gitignore"), NowUtc.AddDays(-30));
        var instance = Path.Combine(BuildRoot, "test-10752-1");
        WriteFileAged(Path.Combine(instance, "readme.txt"), NowUtc.AddDays(-10));
        SetTreeWriteTimeUtc(instance, NowUtc.AddDays(-10));

        var result = RuntimeStateRetention.SweepBuildInstances(BuildRoot, "test-current-1", TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(gitignore), Is.True);
    }

    [Test]
    public void SweepBuildInstances_MissingBuildRoot_ReturnsZero()
    {
        var result = RuntimeStateRetention.SweepBuildInstances(BuildRoot, "test-current-1", TimeSpan.FromDays(7), NowUtc);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SweepLogs_CurrentLogOlderThanMaxAge_IsKept()
    {
        var logDir = Path.Combine(_tempRoot, "logs");
        var current = WriteFileAged(Path.Combine(logDir, "server_20261101T000000Z.log"), NowUtc.AddDays(-30));

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", current, 50, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(current), Is.True);
    }

    [Test]
    public void SweepLogs_OtherLogOlderThanMaxAge_IsDeleted()
    {
        var logDir = Path.Combine(_tempRoot, "logs");
        var current = WriteFileAged(Path.Combine(logDir, "server_20261120T110000Z.log"), NowUtc.AddHours(-1));
        var old = WriteFileAged(Path.Combine(logDir, "server_20261101T000000Z.log"), NowUtc.AddDays(-20));

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", current, 50, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(old), Is.False);
        Assert.That(File.Exists(current), Is.True);
    }

    [Test]
    public void SweepLogs_OverMaxFiles_TrimsOldestOthersUntilWithCurrentFitsLimit()
    {
        var logDir = Path.Combine(_tempRoot, "logs");
        var current = WriteFileAged(Path.Combine(logDir, "server_20261120T110000Z.log"), NowUtc.AddHours(-1));
        var others = Enumerable.Range(1, 5)
            .Select(age => (Age: age, Path: WriteFileAged(Path.Combine(logDir, $"server_age{age}d.log"), NowUtc.AddDays(-age))))
            .ToList();

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", current, 3, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(3));
        Assert.That(File.Exists(current), Is.True);
        foreach (var log in others.Where(l => l.Age >= 3))
        {
            Assert.That(File.Exists(log.Path), Is.False, $"Log aged {log.Age} days should have been trimmed");
        }

        foreach (var log in others.Where(l => l.Age <= 2))
        {
            Assert.That(File.Exists(log.Path), Is.True, $"Log aged {log.Age} days should have been kept");
        }
    }

    [Test]
    public void SweepLogs_CrashLogOld_IsNeverDeleted()
    {
        var logDir = Path.Combine(_tempRoot, "logs");
        var crash = WriteFileAged(Path.Combine(logDir, "crash.log"), NowUtc.AddDays(-30));

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", string.Empty, 50, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(crash), Is.True);
    }

    [Test]
    public void SweepLogs_OtherStemAndNonLogFiles_AreUntouched()
    {
        var logDir = Path.Combine(_tempRoot, "logs");
        var otherStem = WriteFileAged(Path.Combine(logDir, "http-host_x.log"), NowUtc.AddDays(-30));
        var notLog = WriteFileAged(Path.Combine(logDir, "server_notes.txt"), NowUtc.AddDays(-30));

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", string.Empty, 50, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(File.Exists(otherStem), Is.True);
        Assert.That(File.Exists(notLog), Is.True);
    }

    [Test]
    public void SweepLogs_MissingDirectory_ReturnsZero()
    {
        var logDir = Path.Combine(_tempRoot, "logs");

        var result = RuntimeStateRetention.SweepLogs(logDir, "server", string.Empty, 50, TimeSpan.FromDays(14), NowUtc);

        Assert.That(result, Is.EqualTo(0));
    }
}
