using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The index and tree listings (<see cref="IGitReader.ListTrackedPaths"/>, <see cref="IGitReader.ListIndexPathsMatching"/>,
/// <see cref="IGitReader.ListHeadPathsMatching"/>) come back in git's order, the byte order of the UTF-8 path, from <see cref="Libgit2GitReader"/> as from
/// <see cref="CliGitReader"/>. libgit2 enumerates the index case-insensitively under core.ignorecase=true, and an ordinal UTF-16 sort would place a
/// supplementary-plane character before a U+FB00 one that git (UTF-8 bytes) lists after it. The entries are written straight into the index so that case-only
/// neighbours ("A", "a/b") and non-ASCII names do not depend on this machine's file system.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitPathOrderParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Written in an order a case-insensitive listing would not give; git's byte order is the one in Expected.
    private static readonly string[] Entries =
    [
        "B", "a/b", "_x", "a.b", "A", "Z", "a-b", "a0", "aé", "ﬀ", "\U00010000",
    ];

    private static readonly string[] Expected =
    [
        "A", "B", "Z", "_x", "a-b", "a.b", "a/b", "a0", "aé", "ﬀ", "\U00010000",
    ];

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-pathorder-" + Guid.NewGuid().ToString("N"));
        InitRepo(_root);
        var blobFile = Path.Combine(_root, "blob.tmp");
        File.WriteAllText(blobFile, "content\n");
        var blob = RunGit(_root, "hash-object", "-w", blobFile);
        File.Delete(blobFile);
        foreach (var entry in Entries)
        {
            RunGit(_root, "update-index", "--add", "--cacheinfo", $"100644,{blob},{entry}");
        }

        RunGit(_root, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "paths");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [TestCase("true")]
    [TestCase("false")]
    public void IndexAndTreeListings_AreInUtf8ByteOrder_LikeGit(string ignoreCase)
    {
        RunGit(_root, "config", "--local", "core.ignorecase", ignoreCase);
        var lib = new Libgit2GitReader();
        var cli = new CliGitReader();
        var scrambled = Entries.Reverse().ToList(); // the head listing must not follow the order the paths are asked for in

        Assert.Multiple(() =>
        {
            Assert.That(cli.ListTrackedPaths(_root, Timeout), Is.EqualTo(Expected), "git ls-files");
            Assert.That(lib.ListTrackedPaths(_root, Timeout), Is.EqualTo(Expected), "libgit2 ListTrackedPaths");
            Assert.That(cli.ListIndexPathsMatching(_root, scrambled, Timeout), Is.EqualTo(Expected), "git ls-files <paths>");
            Assert.That(lib.ListIndexPathsMatching(_root, scrambled, Timeout), Is.EqualTo(Expected), "libgit2 ListIndexPathsMatching");
            Assert.That(cli.ListHeadPathsMatching(_root, scrambled, Timeout), Is.EqualTo(Expected), "git ls-tree HEAD <paths>");
            Assert.That(lib.ListHeadPathsMatching(_root, scrambled, Timeout), Is.EqualTo(Expected), "libgit2 ListHeadPathsMatching");
        });
    }
}
