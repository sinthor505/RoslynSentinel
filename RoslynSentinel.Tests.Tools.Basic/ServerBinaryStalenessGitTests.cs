using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="ServerBinaryStaleness.EvaluateGit"/> against throwaway git repos. Takes the sha and directory as
/// arguments, so it never touches the static LoadedSolutionDirectory / Current cache (safe in parallel).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class ServerBinaryStalenessGitTests
{
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repo = Path.Combine(Path.GetTempPath(), "rs-staleness-git-" + Guid.NewGuid().ToString("N"));
        InitRepo(_repo);
    }

    [TearDown]
    public void TearDown()
    {
        if (_repo is null || !Directory.Exists(_repo))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repo, recursive: true);
    }

    /// <summary>Writes <paramref name="relativePath"/>, commits it and returns the full sha.</summary>
    private string Commit(string relativePath, string message)
    {
        var path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, message + "\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
        return RunGit(_repo, "rev-parse", "HEAD");
    }

    [Test]
    public void SameSha_IsNotStale()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");

        var report = ServerBinaryStaleness.EvaluateGit(sha, _repo);

        Assert.That(report.IsStale, Is.False);
        Assert.That(report.StaleCommits, Is.Empty);
    }

    [Test]
    public void LaterCommitTouchingOnlyDocs_IsNotStale()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("docs/notes.md", "docs only");

        Assert.That(ServerBinaryStaleness.EvaluateGit(sha, _repo).IsStale, Is.False);
    }

    // Known flake (see the class comment); do not paper over it with retry loops.
    [Test]
    [NonParallelizable]
    public void LaterCommitTouchingServerSource_IsStaleAndListed()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("docs/notes.md", "docs only");
        var sourceCommit = Commit("RoslynSentinel.Common/x.cs", "change common");

        var report = ServerBinaryStaleness.EvaluateGit(sha, _repo);

        Assert.That(report.IsStale, Is.True);
        var listed = report.StaleCommits!.Single();
        Assert.That(listed.Subject, Is.EqualTo("change common"));
        Assert.That(sourceCommit, Does.StartWith(listed.Hash));
    }

    [Test]
    public void ShortStampedSha_Works()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("RoslynSentinel.Tools.Basic/t.cs", "tools change");

        Assert.That(ServerBinaryStaleness.EvaluateGit(sha[..9], _repo).IsStale, Is.True);
    }

    [Test]
    public void UnknownSha_IsNotStale()
    {
        Commit("RoslynSentinel.Common/a.cs", "first");

        var report = ServerBinaryStaleness.EvaluateGit("0123456789abcdef0123456789abcdef01234567", _repo);

        Assert.That(report.IsStale, Is.False);
    }

    [Test]
    public void NonRepoDirectory_IsNotStale()
    {
        // A sibling of the fixture repo under the temp root (assumed not inside any git work tree).
        var plain = Path.Combine(Path.GetTempPath(), "rs-staleness-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try
        {
            var report = ServerBinaryStaleness.EvaluateGit("abcdef0123456", plain);

            Assert.That(report.IsStale, Is.False);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Test]
    public void MissingDirectoryOrMalformedSha_IsNotStaleAndDoesNotThrow()
    {
        Assert.That(ServerBinaryStaleness.EvaluateGit("abcdef0", Path.Combine(_repo, "nope")).IsStale, Is.False);
        Assert.That(ServerBinaryStaleness.EvaluateGit("--output=x", _repo).IsStale, Is.False);
        Assert.That(ServerBinaryStaleness.EvaluateGit("zzzzzzz", _repo).IsStale, Is.False);
    }

    [Test]
    public void CheckNow_WithBuildShaAndSolutionDirectory_UsesTheGitPath()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("RoslynSentinel.Server.Basic/s.cs", "server change");

        var report = ServerBinaryStaleness.CheckNow(sha, _repo);

        Assert.That(report.IsStale, Is.True);
        Assert.That(report.StaleCommits, Has.Count.EqualTo(1));
    }

    [Test]
    public void EvaluateGit_InTheNormalEnvironment_IsServedByLibgit2NotTheCliFallback()
    {
        var sha = Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("RoslynSentinel.Tools.Basic/t.cs", "tools change");
        var libgit2Before = GitReaders.Default.Libgit2Count;
        var fallbacksBefore = GitReaders.Default.CliFallbackCount;

        var report = ServerBinaryStaleness.EvaluateGit(sha, _repo);

        // Counters are process-wide and other tests may add to them in parallel, so assert on direction, not exact deltas;
        // a silent fallback leaves FirstFallbackReason set and raises CliFallbackCount.
        Assert.That(report.IsStale, Is.True);
        Assert.That(GitReaders.Default.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
        Assert.That(GitReaders.Default.CliFallbackCount, Is.EqualTo(fallbacksBefore));
        Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(libgit2Before));
    }

    [Test]
    public void RenameOutOfServerSource_IsStale_BecauseTheOldPathIsListedToo()
    {
        // The commit reader does no rename detection, so the old path (under server source) is listed beside the new one (docs).
        // git log's default rename detection used to list only the new path and call this not stale.
        var sha = Commit("RoslynSentinel.Common/moved.cs", "first");
        Commit("docs/other.md", "docs only");
        RunGit(_repo, "mv", "RoslynSentinel.Common/moved.cs", "docs/moved.cs");
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "move source file into docs");

        var report = ServerBinaryStaleness.EvaluateGit(sha, _repo);

        Assert.That(report.IsStale, Is.True);
        Assert.That(report.StaleCommits!.Single().Subject, Is.EqualTo("move source file into docs"));
    }

    [Test]
    public void DescribeUncommittedServerSource_CleanOrDocsOnly_IsNull_ServerSourceChange_IsDescribed()
    {
        Commit("RoslynSentinel.Common/a.cs", "first");
        Commit("docs/notes.md", "docs");
        var libgit2Before = GitReaders.Default.Libgit2Count;

        Assert.That(ServerBinaryStaleness.DescribeUncommittedServerSource(_repo), Is.Null, "clean");

        File.AppendAllText(Path.Combine(_repo, "docs", "notes.md"), "edited\n");
        Assert.That(ServerBinaryStaleness.DescribeUncommittedServerSource(_repo), Is.Null, "a docs-only change is not server source");

        File.AppendAllText(Path.Combine(_repo, "RoslynSentinel.Common", "a.cs"), "edited\n");
        Directory.CreateDirectory(Path.Combine(_repo, "RoslynSentinel.Tools.Basic"));
        File.WriteAllText(Path.Combine(_repo, "RoslynSentinel.Tools.Basic", "new.cs"), "x\n");
        var message = ServerBinaryStaleness.DescribeUncommittedServerSource(_repo);

        Assert.That(message, Does.StartWith("2 uncommitted file(s) under server source (first: RoslynSentinel.Common/a.cs)"));
        Assert.That(GitReaders.Default.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
        Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(libgit2Before));
    }
}
