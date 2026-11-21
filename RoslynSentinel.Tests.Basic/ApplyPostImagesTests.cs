using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// ApplyChangesResult.PostImages at the write chokepoint: the exact text written per file (post EOL pre-pass), with no
// entry for no-op writes, deleted files or rolled-back files. Every test uses the real manager, never FakeWorkspaceManager.
[TestFixture]
public class ApplyPostImagesTests
{
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSource = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string MixedSourceWithAddedLine = "namespace T;\r\npublic class A\n{\r\n    public int X() => 1;\r\n    public int Y() => 2;\r\n}\r\n";

    private static PersistentWorkspaceManager NewManager()
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(new AdhocWorkspace().CurrentSolution);
        return manager;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ApplyPostImages", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Write_PostImageEqualsDiskTextByteForByte()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var proposed = CrlfSource.Replace("=> 1", "=> 2");
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = proposed }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.PostImages, Is.Not.Null);
            Assert.That(result.PostImages!.ContainsKey(file), Is.True);
            Assert.That(Encoding.UTF8.GetBytes(result.PostImages[file]!), Is.EqualTo(await File.ReadAllBytesAsync(file)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task EolPrePassChangesEndings_PostImageIsTheDiskTextNotTheProposedText()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            var proposedLf = CrlfSource.Replace("=> 1", "=> 2").Replace("\r\n", "\n");
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = proposedLf }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            var onDisk = await File.ReadAllTextAsync(file);
            Assert.That(onDisk, Is.Not.EqualTo(proposedLf), "the pre-pass must have restored CRLF on a CRLF pre-image");
            Assert.That(result.PostImages![file], Is.EqualTo(onDisk));
            Assert.That(result.PostImages[file], Is.Not.EqualTo(proposedLf));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NoOpWrite_HasNoPostImageEntry()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.NoOpFiles, Does.Contain(file));
            Assert.That(result.PostImages, Is.Not.Null);
            Assert.That(result.PostImages!.ContainsKey(file), Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task DeletedFile_HasNoPostImageEntry()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes(CrlfSource));
            using var manager = NewManager();

            var result = await manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string>(), retryCount: 0, validateChanges: false,
                deletePaths: new List<FilePathWrapper> { file });

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.SucceededFiles, Does.Contain(file));
            Assert.That(result.PostImages, Is.Not.Null);
            Assert.That(result.PostImages!.ContainsKey(file), Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RolledBackBatch_HasNoPostImageEntries()
    {
        var dir = NewTempDir();
        try
        {
            var fileA = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(fileA, Encoding.UTF8.GetBytes(MixedSource));
            var fileB = Path.Combine(dir, "B.cs");
            File.WriteAllBytes(fileB, Encoding.UTF8.GetBytes(CrlfSource));
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            try
            {
                using var manager = NewManager();
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [fileA] = MixedSourceWithAddedLine,
                    [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                };

                var result = await manager.ApplyProposedChangesAsync(
                    changes, retryCount: 0, validateChanges: false, rollbackOnPartialFailure: true);

                Assert.That(result.IsError, Is.True, "B's refused write must fail the batch");
                Assert.That(result.PostImages, Is.Not.Null);
                Assert.That(result.PostImages!, Is.Empty);
            }
            finally
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PartialFailureWithoutRollback_HasPostImageOnlyForTheWrittenFile()
    {
        var dir = NewTempDir();
        try
        {
            var fileA = Path.Combine(dir, "A.cs");
            File.WriteAllBytes(fileA, Encoding.UTF8.GetBytes(MixedSource));
            var fileB = Path.Combine(dir, "B.cs");
            File.WriteAllBytes(fileB, Encoding.UTF8.GetBytes(CrlfSource));
            File.SetAttributes(fileB, FileAttributes.ReadOnly);
            try
            {
                using var manager = NewManager();
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [fileA] = MixedSourceWithAddedLine,
                    [fileB] = CrlfSource.Replace("=> 1", "=> 2"),
                };

                var result = await manager.ApplyProposedChangesAsync(
                    changes, retryCount: 0, validateChanges: false, rollbackOnPartialFailure: false);

                Assert.That(result.IsError, Is.True, "B's refused write must fail the batch");
                Assert.That(result.PostImages!.ContainsKey(fileA), Is.True);
                Assert.That(result.PostImages.ContainsKey(fileB), Is.False);
                Assert.That(result.PostImages[fileA], Is.EqualTo(await File.ReadAllTextAsync(fileA)));
            }
            finally
            {
                File.SetAttributes(fileB, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
