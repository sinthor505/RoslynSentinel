using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// <see cref="TestInstanceLauncher.ReadGitStamp"/> (the build-source stamp: short HEAD sha and a dirty flag restricted to
/// the server-source pathspecs) against throwaway repos, through <see cref="GitReaders.Default"/>.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git for fixture setup): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("TestInstanceLauncher")] // sentinel:auto-category
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class TestInstanceLauncherGitStampTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitstamp-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);
        RunGit(_repo, "config", "core.autocrlf", "false");
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

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Test]
    public void CleanRepo_StampsTheShortHeadShaAndIsNotDirty()
    {
        Write("RoslynSentinel.Common/a.cs", "a\n");
        Write("docs/notes.md", "n\n");
        CommitAll(_repo, "first");

        var (sha, dirty) = TestInstanceLauncher.ReadGitStamp(_repo);

        Assert.That(sha, Is.EqualTo(RunGit(_repo, "rev-parse", "--short", "HEAD")));
        Assert.That(dirty, Is.False);
    }

    [Test]
    public void ChangeOutsideServerSource_IsNotDirty()
    {
        Write("RoslynSentinel.Common/a.cs", "a\n");
        Write("docs/notes.md", "n\n");
        CommitAll(_repo, "first");
        Write("docs/notes.md", "edited\n");
        Write("docs/untracked.md", "u\n");

        Assert.That(TestInstanceLauncher.ReadGitStamp(_repo).Dirty, Is.False);
    }

    [TestCase("RoslynSentinel.Common/a.cs", "modified tracked file")]
    [TestCase("RoslynSentinel.Engines.Basic/sub/new.cs", "untracked file in a new directory matching the Engines.* glob")]
    [TestCase("RoslynSentinel.Tools.Basic/new.cs", "untracked file matching the Tools.* glob")]
    [TestCase("RoslynSentinel.Server.Advanced/new.cs", "untracked file matching the Server.* glob")]
    public void ChangeUnderServerSource_IsDirty(string changedPath, string why)
    {
        Write("RoslynSentinel.Common/a.cs", "a\n");
        Write("docs/notes.md", "n\n");
        CommitAll(_repo, "first");
        Write(changedPath, "changed\n");

        Assert.That(TestInstanceLauncher.ReadGitStamp(_repo).Dirty, Is.True, why);
    }

    [Test]
    public void NotARepositoryOrNoCommits_GivesNullShaAndNullOrFalseDirty()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.That(TestInstanceLauncher.ReadGitStamp(plain), Is.EqualTo(((string?)null, (bool?)null)));

        // An unborn HEAD has no sha, but status still works and reports clean.
        var (sha, dirty) = TestInstanceLauncher.ReadGitStamp(_repo);
        Assert.That(sha, Is.Null);
        Assert.That(dirty, Is.False);
    }

    [Test]
    public void ReadGitStamp_InTheNormalEnvironment_IsServedByLibgit2NotTheCliFallback()
    {
        Write("RoslynSentinel.Common/a.cs", "a\n");
        CommitAll(_repo, "first");
        var libgit2Before = GitReaders.Default.Libgit2Count;
        var fallbacksBefore = GitReaders.Default.CliFallbackCount;

        var (sha, dirty) = TestInstanceLauncher.ReadGitStamp(_repo);

        // Counters are process-wide and other tests may add to them in parallel, so assert on direction, not exact deltas;
        // a silent fallback leaves FirstFallbackReason set and raises CliFallbackCount.
        Assert.That(sha, Is.Not.Null);
        Assert.That(dirty, Is.False);
        Assert.That(GitReaders.Default.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
        Assert.That(GitReaders.Default.CliFallbackCount, Is.EqualTo(fallbacksBefore));
        Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThanOrEqualTo(libgit2Before + 2));
    }
}
