using LibGit2Sharp;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="Libgit2Native"/>: the function-pointer binding to the libgit2 library LibGit2Sharp loaded. Pins that the library name is read from the package (no hard-coded
/// <c>git2-&lt;hash&gt;</c>), that the file exists, and the compare-and-swap semantics of <c>git_reference_create_matching</c> the commit primitive relies on (wrong expected id is -15,
/// an all-zero expected id means the ref must not exist, a matching id moves the ref and writes the reflog entry). A LibGit2Sharp upgrade that breaks any of it fails here.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCasCommitNativeTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-casnative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => DeleteTree(_root);

    private static byte[] Raw(string sha) => new ObjectId(sha).RawId;

    [Test]
    public void LibraryName_IsReadFromThePackage_AndTheFileIsPresent()
    {
        var name = Libgit2Native.LibraryName;

        Assert.That(name, Does.StartWith("git2-"));
        var found = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(Repository).Assembly.Location)! }
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, name + ".*", SearchOption.AllDirectories))
            .Any();
        Assert.That(found, Is.True, $"no file named {name}.* under the test output; the native asset layout changed.");
    }

    [Test]
    public void CompareAndSwap_MovesOnlyWhenTheExpectedIdMatches_AndWritesTheReflog()
    {
        var dir = NewRepo(_root, "native");
        var first = Tip(dir);
        Write(dir, "b.txt", "b\n");
        var second = CommitAllReturningSha(dir, "second");
        RunGit(dir, "update-ref", "refs/heads/master", first);
        var gitDirectory = Path.Combine(dir, ".git");

        var stale = Libgit2Native.CompareAndSwapReference(gitDirectory, "refs/heads/master", Raw(second), Raw(second), "commit: stale");
        Assert.That(stale.Code, Is.EqualTo(Libgit2Native.ErrorModified), stale.Message);
        Assert.That(Resolve(dir, "refs/heads/master"), Is.EqualTo(first));

        var mustNotExist = Libgit2Native.CompareAndSwapReference(gitDirectory, "refs/heads/master", Raw(second), new byte[20], "commit: exists");
        Assert.That(mustNotExist.Code, Is.EqualTo(Libgit2Native.ErrorModified), "an all-zero expected id on an existing ref must fail: " + mustNotExist.Message);
        Assert.That(Resolve(dir, "refs/heads/master"), Is.EqualTo(first));

        var created = Libgit2Native.CompareAndSwapReference(gitDirectory, "refs/heads/fresh", Raw(first), new byte[20], "branch: Created");
        Assert.That(created.Code, Is.Zero, created.Message);
        Assert.That(Resolve(dir, "refs/heads/fresh"), Is.EqualTo(first));

        var moved = Libgit2Native.CompareAndSwapReference(gitDirectory, "refs/heads/master", Raw(second), Raw(first), "commit: moved");
        Assert.That(moved.Code, Is.Zero, moved.Message);
        Assert.That(Resolve(dir, "refs/heads/master"), Is.EqualTo(second));
        Assert.That(Reflog(dir, "refs/heads/master")[0], Is.EqualTo("Test User|test@example.com|commit: moved"));
        Assert.That(Reflog(dir, "HEAD")[0], Is.EqualTo("Test User|test@example.com|commit: moved"), "HEAD's reflog mirrors the branch update when HEAD points at it.");
    }

    [Test]
    public void CompareAndSwap_OnAMissingRepository_ReturnsALibgit2Error_NotAnException()
    {
        var result = Libgit2Native.CompareAndSwapReference(Path.Combine(_root, "does-not-exist"), "refs/heads/master", Raw("4b825dc642cb6eb9a060e54bf8d69288fbee4904"), new byte[20], "x");

        Assert.That(result.Code, Is.Not.Zero);
        Assert.That(result.Message, Is.Not.Empty);
    }

    private static string CommitAllReturningSha(string dir, string subject)
    {
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "--quiet", "-m", subject);
        return Tip(dir);
    }
}
