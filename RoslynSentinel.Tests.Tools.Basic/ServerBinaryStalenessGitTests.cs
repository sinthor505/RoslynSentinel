namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="ServerBinaryStaleness.EvaluateGit"/> against throwaway git repos. Takes the sha and directory as
/// arguments, so it never touches the static LoadedSolutionDirectory / Current cache (safe in parallel).
/// </summary>
[TestFixture]
public class ServerBinaryStalenessGitTests
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (GitProcess.TryRun(Path.GetTempPath(), GitTimeout, "--version") is not { ExitCode: 0 })
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repo = Path.Combine(Path.GetTempPath(), "rs-staleness-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        RunGit(_repo, "init", "--quiet");
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

    private static string RunGit(string directory, params string[] args)
    {
        var result = GitProcess.TryRun(directory, GitTimeout, args);
        Assert.That(result, Is.Not.Null, "git failed to run: " + string.Join(' ', args));
        Assert.That(result!.Value.ExitCode, Is.EqualTo(0), "git " + string.Join(' ', args));
        return result.Value.StdOut.Trim();
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

    [Test]
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
}
