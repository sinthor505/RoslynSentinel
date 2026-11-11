using LibGit2Sharp;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Runs every <see cref="IGitReader"/> probe through <see cref="Libgit2GitReader"/> and <see cref="CliGitReader"/> directly
/// on throwaway repos built with <see cref="RoslynSentinel.Tests.Battery.Basic.GitTestRepo"/> and asserts the same answer,
/// then checks the composite <see cref="FallbackGitReader"/>: that libgit2 (not the CLI fallback) serves the calls in the
/// normal test environment, and that each kind of native failure falls back, logs once and is observable.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// The merge/ordering tests sleep between commits so committer dates differ; same-second commits would tie and git/libgit2 break ties differently.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitReaderParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly CliGitReader _cli = new();
    private readonly Libgit2GitReader _lib = new();

    private string _root = null!;
    private string _repo = null!;
    private int _messageFiles;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitreader-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);
        // Pin EOL handling per test: the machine's system/global autocrlf must not decide the outcome.
        RunGit(_repo, "config", "core.autocrlf", "false");
    }

    [TearDown]
    public void TearDown()
    {
        // Every reader disposes its Repository per call, so nothing here holds a pack-file handle.
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

    // ---- helpers ----

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string[] Rows(IReadOnlyList<WorkingTreeChange>? changes) =>
        changes is null
            ? ["<null>"]
            : [.. changes.Select(c => $"{c.IndexState}{c.WorkTreeState} {(c.OriginalPath is null ? string.Empty : c.OriginalPath + " -> ")}{c.Path}")];

    private static string[] Rows(IReadOnlyList<ChangedCommit>? commits) =>
        commits is null ? ["<null>"] : [.. commits.Select(c => $"{c.ShortHash}|{c.Subject}|{string.Join(',', c.Paths)}")];

    private static string[] Rows(IReadOnlyList<CommitMessage>? messages) =>
        messages is null
            ? ["<null>"]
            : [.. messages.Select(m => $"{m.Sha}|{m.Message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}")];

    /// <summary>Asserts both readers report the same status rows and returns the CLI rows for content assertions.</summary>
    private string[] StatusRows(string directory, params string[] pathspecs)
    {
        var specs = pathspecs.Length == 0 ? null : pathspecs;
        var cli = Rows(_cli.GetStatus(directory, specs, Timeout));
        var lib = Rows(_lib.GetStatus(directory, specs, Timeout));
        Assert.That(lib, Is.EqualTo(cli), "libgit2 status differs from git status --porcelain");
        return cli;
    }

    private string[] CommitsSinceRows(string directory, string sha)
    {
        var cli = Rows(_cli.GetCommitsSince(directory, sha, Timeout));
        var lib = Rows(_lib.GetCommitsSince(directory, sha, Timeout));
        Assert.That(lib, Is.EqualTo(cli), "libgit2 commits-since differs from git log");
        return cli;
    }

    private string? BlobParity(string directory, string relativePath)
    {
        var cli = _cli.ReadBlobAtHead(directory, relativePath, Timeout);
        var lib = _lib.ReadBlobAtHead(directory, relativePath, Timeout);
        Assert.That(lib, Is.EqualTo(cli), "libgit2 blob differs from git cat-file");
        return cli;
    }

    private string CommitVerbatim(string directory, byte[] message)
    {
        var file = Path.Combine(_root, $"message{_messageFiles++}.txt");
        File.WriteAllBytes(file, message);
        RunGit(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "--cleanup=verbatim", "-F", file);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    // ---- status ----

    [Test]
    public void Status_CleanRepo_IsEmpty()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");

        Assert.That(StatusRows(_repo), Is.Empty);
    }

    [Test]
    public void Status_ModifiedFile()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        Write(_repo, "a.txt", "changed\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { " M a.txt" }));
    }

    [Test]
    public void Status_StagedModifyAndStagedNewAndWorktreeDelete()
    {
        Write(_repo, "keep.txt", "k\n");
        Write(_repo, "gone.txt", "g\n");
        CommitAll(_repo, "first");
        Write(_repo, "keep.txt", "k2\n");
        Write(_repo, "added.txt", "n\n");
        RunGit(_repo, "add", "keep.txt", "added.txt");
        File.Delete(Path.Combine(_repo, "gone.txt"));

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { "A  added.txt", " D gone.txt", "M  keep.txt" }));
    }

    [Test]
    public void Status_StagedRename()
    {
        Write(_repo, "a.txt", "some stable content so the rename is detected\nmore content\n");
        CommitAll(_repo, "first");
        RunGit(_repo, "mv", "a.txt", "b.txt");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { "R  a.txt -> b.txt" }));
    }

    [Test]
    public void Status_UntrackedFileAndUntrackedDirectory_DirectoryIsCollapsed()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        Write(_repo, "new.txt", "n\n");
        Write(_repo, "newdir/one.txt", "1\n");
        Write(_repo, "newdir/deeper/two.txt", "2\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { "?? new.txt", "?? newdir/" }));
    }

    [Test]
    public void Status_TrackedChangesComeBeforeUntracked_EvenWhenPathsInterleave()
    {
        Write(_repo, "b.txt", "b\n");
        Write(_repo, "d.txt", "d\n");
        CommitAll(_repo, "first");
        Write(_repo, "a-untracked.txt", "u\n");
        Write(_repo, "b.txt", "b2\n");
        Write(_repo, "c-untracked.txt", "u\n");
        Write(_repo, "d.txt", "d2\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { " M b.txt", " M d.txt", "?? a-untracked.txt", "?? c-untracked.txt" }));
    }

    [Test]
    public void Status_IgnoredFilesAndDirectories_AreExcluded()
    {
        Write(_repo, ".gitignore", "*.log\nbuild/\n");
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        Write(_repo, "debug.log", "x\n");
        Write(_repo, "build/out.dll", "x\n");
        Write(_repo, "kept.txt", "x\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { "?? kept.txt" }));
    }

    [Test]
    public void Status_DeletedFromIndexAndPresentAgain_IsTwoRows()
    {
        Write(_repo, "f.txt", "f\n");
        CommitAll(_repo, "first");
        RunGit(_repo, "rm", "--cached", "--quiet", "f.txt");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { "D  f.txt", "?? f.txt" }));
    }

    [Test]
    public void Status_AutocrlfTrue_CrLfWorkingFileCommittedAsLf_IsClean()
    {
        RunGit(_repo, "config", "core.autocrlf", "true");
        // A CRLF checkout: git add converts it to LF in the blob, the working file keeps CRLF.
        File.WriteAllBytes(Path.Combine(_repo, "crlf.txt"), "line one\r\nline two\r\n"u8.ToArray());
        CommitAll(_repo, "first");

        Assert.That(StatusRows(_repo), Is.Empty);
        Assert.That(BlobParity(_repo, "crlf.txt"), Is.EqualTo("line one\nline two\n"), "the committed blob is LF");
    }

    [Test]
    public void Status_AutocrlfFalse_RewriteWithCrLf_IsModified()
    {
        Write(_repo, "crlf.txt", "line one\nline two\n");
        CommitAll(_repo, "first");

        Write(_repo, "crlf.txt", "line one\r\nline two\r\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { " M crlf.txt" }));
    }

    [Test]
    public void Status_AutocrlfTrue_LfCommitThenCrLfRewrite_BothBackendsAgree()
    {
        RunGit(_repo, "config", "core.autocrlf", "true");
        Write(_repo, "crlf.txt", "line one\nline two\n");
        CommitAll(_repo, "first");
        Write(_repo, "crlf.txt", "line one\r\nline two\r\n");

        // Only parity is asserted: whether git calls this clean or modified is git's decision, and libgit2 must make the same one.
        _ = StatusRows(_repo);
    }

    [Test]
    public void Status_FileInSubdirectoryAndPathWithSpace()
    {
        Write(_repo, "sub dir/inner file.txt", "i\n");
        Write(_repo, "sub dir/other.txt", "o\n");
        CommitAll(_repo, "first");
        Write(_repo, "sub dir/inner file.txt", "changed\n");
        Write(_repo, "sub dir/untracked file.txt", "u\n");

        Assert.That(StatusRows(_repo), Is.EqualTo(new[] { " M sub dir/inner file.txt", "?? sub dir/untracked file.txt" }));
    }

    [Test]
    public void Status_RunFromSubdirectory_PathsAreStillRepoRelative()
    {
        Write(_repo, "sub/inner.txt", "i\n");
        Write(_repo, "top.txt", "t\n");
        CommitAll(_repo, "first");
        Write(_repo, "sub/inner.txt", "changed\n");
        Write(_repo, "top.txt", "changed\n");

        Assert.That(StatusRows(Path.Combine(_repo, "sub")), Is.EqualTo(new[] { " M sub/inner.txt", " M top.txt" }));
    }

    [Test]
    public void Status_Pathspecs_PlainPathAndGlob()
    {
        foreach (var project in new[] { "Proj.Common", "Proj.Engines.A", "Proj.Engines.B", "Docs" })
        {
            Write(_repo, $"{project}/file.cs", "x\n");
        }

        CommitAll(_repo, "first");
        foreach (var project in new[] { "Proj.Common", "Proj.Engines.A", "Proj.Engines.B", "Docs" })
        {
            Write(_repo, $"{project}/file.cs", "changed\n");
        }

        Write(_repo, "Proj.Engines.B/newdir/new.cs", "n\n");
        Write(_repo, "Proj.Engines.C/new.cs", "n\n");

        Assert.Multiple(() =>
        {
            Assert.That(StatusRows(_repo, "Proj.Common"), Is.EqualTo(new[] { " M Proj.Common/file.cs" }));
            Assert.That(
                StatusRows(_repo, "Proj.Common", "Proj.Engines.*"),
                Is.EqualTo(new[] { " M Proj.Common/file.cs", " M Proj.Engines.A/file.cs", " M Proj.Engines.B/file.cs", "?? Proj.Engines.B/newdir/", "?? Proj.Engines.C/" }));
            Assert.That(StatusRows(_repo, "NoSuchProject.*"), Is.Empty);
        });
    }

    [Test]
    public void Status_PathspecFromSubdirectory_IsRelativeToThatDirectory()
    {
        Write(_repo, "sub/inner.txt", "i\n");
        Write(_repo, "inner.txt", "top\n");
        CommitAll(_repo, "first");
        Write(_repo, "sub/inner.txt", "changed\n");
        Write(_repo, "inner.txt", "changed\n");

        Assert.That(StatusRows(Path.Combine(_repo, "sub"), "inner.txt"), Is.EqualTo(new[] { " M sub/inner.txt" }));
    }

    [Test]
    public void Status_LinkedWorktree()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "sub/b.txt", "b\n");
        CommitAll(_repo, "first");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "sub/b.txt", "changed in worktree\n");
        Write(worktree, "fresh.txt", "f\n");

        Assert.Multiple(() =>
        {
            Assert.That(StatusRows(worktree), Is.EqualTo(new[] { " M sub/b.txt", "?? fresh.txt" }));
            Assert.That(StatusRows(Path.Combine(worktree, "sub")), Is.EqualTo(new[] { " M sub/b.txt", "?? fresh.txt" }));
            Assert.That(StatusRows(_repo), Is.Empty, "the main working tree is untouched");
            Assert.That(
                Rows(_lib.GetCommitsSince(worktree, RunGit(_repo, "rev-parse", "HEAD"), Timeout)),
                Is.Empty);
        });
    }

    [Test]
    public void Status_NotARepository_IsNullForBoth()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.That(StatusRows(plain), Is.EqualTo(new[] { "<null>" }));
    }

    // ---- head sha ----

    [Test]
    public void HeadSha_FullAndShort()
    {
        Write(_repo, "a.txt", "a\n");
        var sha = CommitAll(_repo, "first");

        Assert.Multiple(() =>
        {
            Assert.That(_lib.GetHeadSha(_repo, false, Timeout), Is.EqualTo(sha));
            Assert.That(_cli.GetHeadSha(_repo, false, Timeout), Is.EqualTo(sha));
            var cliShort = _cli.GetHeadSha(_repo, true, Timeout);
            Assert.That(cliShort, Has.Length.GreaterThanOrEqualTo(7));
            // Accepted difference (see Libgit2GitReader): equal on a small repository, both at least 7 and a prefix of the sha on a large one.
            Assert.That(_lib.GetHeadSha(_repo, true, Timeout), Is.EqualTo(cliShort));
        });
    }

    [Test]
    public void HeadSha_UnbornHeadAndNonRepository_AreNull()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Multiple(() =>
        {
            Assert.That(_cli.GetHeadSha(_repo, false, Timeout), Is.Null);
            Assert.That(_lib.GetHeadSha(_repo, false, Timeout), Is.Null);
            Assert.That(_cli.GetHeadSha(_repo, true, Timeout), Is.Null);
            Assert.That(_lib.GetHeadSha(_repo, true, Timeout), Is.Null);
            Assert.That(_cli.GetHeadSha(plain, false, Timeout), Is.Null);
            Assert.That(_lib.GetHeadSha(plain, false, Timeout), Is.Null);
        });
    }

    [Test]
    public void HeadSha_InLinkedWorktreeAndSubdirectory()
    {
        Write(_repo, "sub/a.txt", "a\n");
        CommitAll(_repo, "first");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "sub/b.txt", "b\n");
        var worktreeSha = CommitAll(worktree, "in worktree");

        Assert.Multiple(() =>
        {
            Assert.That(_lib.GetHeadSha(worktree, false, Timeout), Is.EqualTo(worktreeSha));
            Assert.That(_lib.GetHeadSha(Path.Combine(worktree, "sub"), false, Timeout), Is.EqualTo(worktreeSha));
            Assert.That(_lib.GetHeadSha(Path.Combine(worktree, "sub"), true, Timeout), Is.EqualTo(_cli.GetHeadSha(worktree, true, Timeout)));
        });
    }

    // ---- commits since ----

    [Test]
    public void CommitsSince_LinearHistory_ListsPathsIncludingRenameDeleteSubdirectoryAndSpace()
    {
        Write(_repo, "base.txt", "base\n");
        var since = CommitAll(_repo, "base");
        Write(_repo, "sub dir/new file.txt", "n\n");
        Write(_repo, "naive ü.txt", "u\n");
        CommitAll(_repo, "add files");
        RunGit(_repo, "mv", "base.txt", "renamed.txt");
        CommitAll(_repo, "rename base");
        File.Delete(Path.Combine(_repo, "sub dir", "new file.txt"));
        CommitAll(_repo, "delete one\ttabbed subject");

        var rows = CommitsSinceRows(_repo, since);

        Assert.That(rows, Has.Length.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0], Does.EndWith("|delete one\ttabbed subject|sub dir/new file.txt"));
            // No rename detection: the old and the new path are both listed.
            Assert.That(rows[1], Does.EndWith("|rename base|base.txt,renamed.txt"));
            Assert.That(rows[2], Does.EndWith("|add files|naive ü.txt,sub dir/new file.txt"));
        });
    }

    [Test]
    public void CommitsSince_AbbreviatedSha_IsAccepted()
    {
        Write(_repo, "a.txt", "a\n");
        var since = CommitAll(_repo, "first");
        Write(_repo, "b.txt", "b\n");
        CommitAll(_repo, "second");

        var rows = CommitsSinceRows(_repo, since[..7]);

        Assert.That(rows, Has.Length.EqualTo(1));
        Assert.That(rows[0], Does.EndWith("|second|b.txt"));
    }

    [Test]
    public void CommitsSince_HeadItself_IsEmpty()
    {
        Write(_repo, "a.txt", "a\n");
        var sha = CommitAll(_repo, "first");

        Assert.That(CommitsSinceRows(_repo, sha), Is.Empty);
    }

    [Test]
    public void CommitsSince_UnknownSha_IsNullForBoth()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");

        Assert.That(CommitsSinceRows(_repo, "abcdef1234567"), Is.EqualTo(new[] { "<null>" }));
    }

    [Test]
    public void CommitsSince_MergeCommitInRange_HasNoPathsAndOrderMatches()
    {
        Write(_repo, "base.txt", "base\n");
        var since = CommitAll(_repo, "base");
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "feature");
        Thread.Sleep(1100);
        Write(_repo, "feature.txt", "f\n");
        CommitAll(_repo, "on feature");
        RunGit(_repo, "checkout", "--quiet", mainBranch);
        Thread.Sleep(1100);
        Write(_repo, "main.txt", "m\n");
        CommitAll(_repo, "on main");
        Thread.Sleep(1100);
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");

        var rows = CommitsSinceRows(_repo, since);

        Assert.That(rows, Has.Length.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0], Does.EndWith("|merge feature|"), "a merge reports no paths");
            Assert.That(rows[1], Does.EndWith("|on main|main.txt"));
            Assert.That(rows[2], Does.EndWith("|on feature|feature.txt"));
        });
    }

    [Test]
    public void CommitsSince_RangeIncludingTheRootCommit_DiffsItAgainstTheEmptyTree()
    {
        Write(_repo, "root.txt", "r\n");
        Write(_repo, "dir/root2.txt", "r2\n");
        CommitAll(_repo, "root commit");
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        Write(_repo, "second.txt", "s\n");
        CommitAll(_repo, "second");

        // An unrelated root commit is not an ancestor of main, so main..HEAD style range "<unrelated>..HEAD" lists main's whole history.
        RunGit(_repo, "checkout", "--quiet", "--orphan", "unrelated");
        var unrelated = CommitAll(_repo, "unrelated root");
        RunGit(_repo, "checkout", "--quiet", "--force", mainBranch);

        var rows = CommitsSinceRows(_repo, unrelated);

        Assert.That(rows, Has.Length.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0], Does.EndWith("|second|second.txt"));
            Assert.That(rows[1], Does.EndWith("|root commit|dir/root2.txt,root.txt"));
        });
    }

    // ---- blob at HEAD ----

    [Test]
    public void ReadBlobAtHead_ReturnsCommittedContent_NotTheWorkingFile()
    {
        Write(_repo, "a.txt", "committed ü\n");
        CommitAll(_repo, "first");
        Write(_repo, "a.txt", "edited on disk\n");

        Assert.That(BlobParity(_repo, "a.txt"), Is.EqualTo("committed ü\n"));
    }

    [Test]
    public void ReadBlobAtHead_KeepsCrLfBytesExactly()
    {
        File.WriteAllBytes(Path.Combine(_repo, "crlf.txt"), "one\r\ntwo\r\n"u8.ToArray());
        CommitAll(_repo, "first");

        Assert.That(BlobParity(_repo, "crlf.txt"), Is.EqualTo("one\r\ntwo\r\n"));
    }

    [Test]
    public void ReadBlobAtHead_MissingFile_IsNull()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        Write(_repo, "untracked.txt", "u\n");

        Assert.Multiple(() =>
        {
            Assert.That(BlobParity(_repo, "nope.txt"), Is.Null);
            Assert.That(BlobParity(_repo, "untracked.txt"), Is.Null, "present on disk but not in HEAD");
        });
    }

    [Test]
    public void ReadBlobAtHead_Directory_IsNull()
    {
        Write(_repo, "dir/a.txt", "a\n");
        CommitAll(_repo, "first");

        Assert.That(BlobParity(_repo, "dir"), Is.Null);
    }

    [Test]
    public void ReadBlobAtHead_FromSubdirectory_PathIsRelativeToThatDirectory()
    {
        Write(_repo, "sub dir/file with space.txt", "in sub\n");
        Write(_repo, "file with space.txt", "at top\n");
        CommitAll(_repo, "first");
        var sub = Path.Combine(_repo, "sub dir");

        Assert.Multiple(() =>
        {
            Assert.That(BlobParity(sub, "file with space.txt"), Is.EqualTo("in sub\n"));
            Assert.That(BlobParity(sub, "../file with space.txt"), Is.EqualTo("at top\n"));
            Assert.That(BlobParity(_repo, "sub dir/file with space.txt"), Is.EqualTo("in sub\n"));
        });
    }

    [Test]
    public void ReadBlobAtHead_NoCommitsYet_IsNull()
    {
        Write(_repo, "a.txt", "a\n");
        RunGit(_repo, "add", "a.txt");

        Assert.That(BlobParity(_repo, "a.txt"), Is.Null);
    }

    // ---- tracked paths ----

    [Test]
    public void ListTrackedPaths_IndexContents_IgnoreRulesDoNotApply()
    {
        Write(_repo, ".gitignore", "*.log\n");
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "sub dir/b c.txt", "b\n");
        Write(_repo, "forced.log", "tracked although ignored\n");
        RunGit(_repo, "add", "-f", "forced.log");
        CommitAll(_repo, "first");
        Write(_repo, "untracked.txt", "u\n");
        Write(_repo, "other.log", "ignored and untracked\n");
        RunGit(_repo, "rm", "--cached", "--quiet", "a.txt");
        Write(_repo, "staged-only.txt", "s\n");
        RunGit(_repo, "add", "staged-only.txt");

        var cli = _cli.ListTrackedPaths(_repo, Timeout);
        var lib = _lib.ListTrackedPaths(_repo, Timeout);

        Assert.That(lib, Is.EqualTo(cli), "libgit2 index paths differ from git ls-files");
        Assert.That(cli, Is.EqualTo(new[] { ".gitignore", "forced.log", "staged-only.txt", "sub dir/b c.txt" }));
    }

    [Test]
    public void ListTrackedPaths_FromSubdirectory_ListsOnlyItsFilesRelativeToIt()
    {
        Write(_repo, "top.txt", "t\n");
        Write(_repo, "sub/one.txt", "1\n");
        Write(_repo, "sub/deeper/two.txt", "2\n");
        Write(_repo, "subtle/three.txt", "3\n");
        CommitAll(_repo, "first");

        var sub = Path.Combine(_repo, "sub");
        var cli = _cli.ListTrackedPaths(sub, Timeout);
        var lib = _lib.ListTrackedPaths(sub, Timeout);

        Assert.That(lib, Is.EqualTo(cli));
        Assert.That(cli, Is.EqualTo(new[] { "deeper/two.txt", "one.txt" }));
    }

    [Test]
    public void ListTrackedPaths_EmptyIndexAndNonRepository()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Multiple(() =>
        {
            Assert.That(_lib.ListTrackedPaths(_repo, Timeout), Is.EqualTo(_cli.ListTrackedPaths(_repo, Timeout)));
            Assert.That(_lib.ListTrackedPaths(_repo, Timeout), Is.Empty);
            Assert.That(_lib.ListTrackedPaths(plain, Timeout), Is.Null);
            Assert.That(_cli.ListTrackedPaths(plain, Timeout), Is.Null);
        });
    }

    [Test]
    public void ListTrackedPaths_InLinkedWorktree()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "sub/b.txt", "b\n");
        CommitAll(_repo, "first");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");

        Assert.That(_lib.ListTrackedPaths(worktree, Timeout), Is.EqualTo(_cli.ListTrackedPaths(worktree, Timeout)));
        Assert.That(_lib.ListTrackedPaths(worktree, Timeout), Is.EqualTo(new[] { "a.txt", "sub/b.txt" }));
    }

    // ---- commit messages ----

    [Test]
    public void CommitMessages_NormalCommitsWithBodies()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first subject", "Body paragraph.\n\nPlan: plan_x\nCloses: issue_y");
        Write(_repo, "b.txt", "b\n");
        CommitAll(_repo, "second subject");

        var cli = Rows(_cli.GetCommitMessages(_repo, Timeout));
        var lib = Rows(_lib.GetCommitMessages(_repo, Timeout));

        Assert.That(lib, Is.EqualTo(cli), "libgit2 messages differ from git log %B");
        Assert.That(cli, Has.Length.EqualTo(2));
        Assert.That(cli[1], Does.Contain("first subject\\n\\nBody paragraph.\\n\\nPlan: plan_x\\nCloses: issue_y"));
    }

    [Test]
    public void CommitMessages_CrLfLfAndTrailingNewlineVariantsMatch()
    {
        CommitVerbatim(_repo, "crlf subject\r\n\r\nbody line\r\nPlan: plan_crlf\r\n"u8.ToArray());
        CommitVerbatim(_repo, "lf subject\n\nbody line\nPlan: plan_lf\n"u8.ToArray());
        CommitVerbatim(_repo, "no trailing newline"u8.ToArray());
        CommitVerbatim(_repo, "several trailing newlines\n\n\n"u8.ToArray());
        CommitVerbatim(_repo, "unicode ü 中\n"u8.ToArray());

        var cli = Rows(_cli.GetCommitMessages(_repo, Timeout));
        var lib = Rows(_lib.GetCommitMessages(_repo, Timeout));

        Assert.That(lib, Is.EqualTo(cli), "libgit2 messages differ from git log %B");
        Assert.That(cli, Has.Length.EqualTo(5));
        Assert.That(cli[4], Does.EndWith("|crlf subject\\r\\n\\r\\nbody line\\r\\nPlan: plan_crlf\\r\\n"), "CRLF survives");
    }

    [Test]
    public void CommitMessages_UnbornHeadAndNonRepository_AreNull()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Multiple(() =>
        {
            Assert.That(_cli.GetCommitMessages(_repo, Timeout), Is.Null);
            Assert.That(_lib.GetCommitMessages(_repo, Timeout), Is.Null);
            Assert.That(_cli.GetCommitMessages(plain, Timeout), Is.Null);
            Assert.That(_lib.GetCommitMessages(plain, Timeout), Is.Null);
        });
    }

    [Test]
    public void CommitMessages_MergeCommit_IsIncludedInHeadHistoryOrder()
    {
        Write(_repo, "base.txt", "base\n");
        CommitAll(_repo, "base");
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "feature");
        Thread.Sleep(1100);
        Write(_repo, "feature.txt", "f\n");
        CommitAll(_repo, "on feature");
        RunGit(_repo, "checkout", "--quiet", mainBranch);
        Thread.Sleep(1100);
        Write(_repo, "main.txt", "m\n");
        CommitAll(_repo, "on main");
        Thread.Sleep(1100);
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");

        var cli = Rows(_cli.GetCommitMessages(_repo, Timeout));
        var lib = Rows(_lib.GetCommitMessages(_repo, Timeout));

        Assert.That(cli, Has.Length.EqualTo(4));
        Assert.That(lib, Is.EqualTo(cli));
    }

    // ---- composite: libgit2 must serve the calls, a fallback must never be silent ----

    [Test]
    public void Composite_InTheNormalEnvironment_Libgit2ServesEveryProbe_NotTheCliFallback()
    {
        Write(_repo, "sub/a.txt", "a\n");
        var sha = CommitAll(_repo, "first");
        Write(_repo, "sub/a.txt", "changed\n");
        var log = new List<string>();
        var reader = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), log.Add);

        var results = new object?[]
        {
            reader.GetHeadSha(_repo, shortForm: false, Timeout),
            reader.GetCommitsSince(_repo, sha, Timeout),
            reader.GetStatus(_repo, pathspecs: null, Timeout),
            reader.ReadBlobAtHead(_repo, "sub/a.txt", Timeout),
            reader.ListTrackedPaths(_repo, Timeout),
            reader.GetCommitMessages(_repo, Timeout),
        };

        // If native libgit2 is missing or broken the composite would still answer (from the CLI), so these three
        // assertions are what make this test fail on a silent fallback.
        Assert.Multiple(() =>
        {
            Assert.That(reader.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
            Assert.That(reader.CliFallbackCount, Is.EqualTo(0));
            Assert.That(reader.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
            Assert.That(reader.Libgit2Count, Is.EqualTo(results.Length));
            Assert.That(log, Is.Empty);
            Assert.That(results, Has.All.Not.Null);
        });
    }

    [Test]
    public void SharedDefaultReader_InTheNormalEnvironment_UsesLibgit2()
    {
        Write(_repo, "a.txt", "a\n");
        var sha = CommitAll(_repo, "first");
        var fallbacksBefore = GitReaders.Default.CliFallbackCount;
        var libgit2Before = GitReaders.Default.Libgit2Count;

        var head = GitReaders.Default.GetHeadSha(_repo, shortForm: false, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(head, Is.EqualTo(sha));
            Assert.That(GitReaders.Default.FirstFallbackReason, Is.Null, "libgit2 fell back to the CLI: native library missing or broken?");
            Assert.That(GitReaders.Default.CliFallbackCount, Is.EqualTo(fallbacksBefore));
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(libgit2Before));
            Assert.That(GitReaders.Default.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    [Test]
    public void Composite_PathspecMagic_IsDeclinedByLibgit2AndAnsweredByTheCli()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        Write(_repo, "a.txt", "changed\n");
        var log = new List<string>();
        var reader = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), log.Add);

        var rows = Rows(reader.GetStatus(_repo, [":(top)a.txt"], Timeout));

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.EqualTo(new[] { " M a.txt" }));
            Assert.That(reader.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(reader.CliFallbackCount, Is.EqualTo(1));
            Assert.That(reader.FirstFallbackReason, Does.StartWith("NotSupportedException"));
        });
    }

    public static IEnumerable<TestCaseData> NativeFailures()
    {
        yield return new TestCaseData(new DllNotFoundException("Unable to load shared library 'git2-test'")).SetName("DllNotFoundException");
        yield return new TestCaseData(new TypeInitializationException("LibGit2Sharp.Core.NativeMethods", new DllNotFoundException("inner"))).SetName("TypeInitializationException");
        yield return new TestCaseData(new LibGit2SharpException("libgit2 could not open the repository")).SetName("LibGit2SharpException");
        yield return new TestCaseData(new BadImageFormatException("wrong architecture")).SetName("BadImageFormatException");
    }

    [TestCaseSource(nameof(NativeFailures))]
    public void Composite_NativeFailure_FallsBackLogsOnceAndIsObservable(Exception failure)
    {
        var log = new List<string>();
        var reader = new FallbackGitReader(new ScriptedReader("primary", _ => failure), new ScriptedReader("fallback", _ => null), log.Add);

        var first = reader.GetHeadSha(_repo, false, Timeout);
        var second = reader.GetHeadSha(_repo, false, Timeout);
        var third = reader.GetStatus(_repo, null, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("fallback"));
            Assert.That(second, Is.EqualTo("fallback"));
            Assert.That(third, Is.Null);
            Assert.That(reader.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(reader.CliFallbackCount, Is.EqualTo(3));
            Assert.That(reader.Libgit2Count, Is.EqualTo(0));
            Assert.That(reader.FirstFallbackReason, Does.StartWith(failure.GetType().Name).And.Contain(failure.Message));
            Assert.That(log, Has.Count.EqualTo(1), "the fallback is logged once, not per call");
            Assert.That(log[0], Does.Contain(failure.GetType().Name).And.Contain(failure.Message));
        });
    }

    [Test]
    public void Composite_FailureOnALaterCall_IsAlsoCaught()
    {
        // The first call works (native library loaded), the second throws (a later native-load failure): both are served.
        var log = new List<string>();
        var primary = new ScriptedReader("primary", call => call == 0 ? null : new DllNotFoundException("late native failure"));
        var reader = new FallbackGitReader(primary, new ScriptedReader("fallback", _ => null), log.Add);

        var first = reader.GetHeadSha(_repo, false, Timeout);
        var firstBackend = reader.LastBackend;
        var second = reader.GetHeadSha(_repo, false, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("primary"));
            Assert.That(firstBackend, Is.EqualTo(GitReaderBackend.Libgit2));
            Assert.That(second, Is.EqualTo("fallback"));
            Assert.That(reader.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(reader.Libgit2Count, Is.EqualTo(1));
            Assert.That(reader.CliFallbackCount, Is.EqualTo(1));
            Assert.That(log, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Composite_DifferentFailureKinds_AreEachLoggedOnce()
    {
        var log = new List<string>();
        var primary = new ScriptedReader("primary", call => call switch
        {
            0 => new NotSupportedException("pathspec magic"),
            1 => new DllNotFoundException("native library missing"),
            2 => new NotSupportedException("pathspec magic again"),
            _ => new DllNotFoundException("native library missing again"),
        });
        var reader = new FallbackGitReader(primary, new ScriptedReader("fallback", _ => null), log.Add);

        for (var i = 0; i < 4; i++)
        {
            reader.GetHeadSha(_repo, false, Timeout);
        }

        Assert.Multiple(() =>
        {
            Assert.That(log, Has.Count.EqualTo(2), "a harmless NotSupportedException must not hide a later native-load failure");
            Assert.That(log[0], Does.Contain("NotSupportedException"));
            Assert.That(log[1], Does.Contain("DllNotFoundException"));
            Assert.That(reader.FirstFallbackReason, Does.StartWith("NotSupportedException"));
        });
    }

    [Test]
    public void Composite_UnexpectedExceptionTypes_Propagate()
    {
        var reader = new FallbackGitReader(
            new ScriptedReader("primary", _ => new InvalidOperationException("a bug, not a native failure")),
            new ScriptedReader("fallback", _ => null),
            _ => { });

        Assert.Throws<InvalidOperationException>(() => reader.GetHeadSha(_repo, false, Timeout));
        Assert.That(reader.CliFallbackCount, Is.EqualTo(0));
    }

    /// <summary>A reader that answers "its name" for <see cref="GetHeadSha"/> and null elsewhere, throwing what <c>failure</c> returns for the zero-based call number.</summary>
    private sealed class ScriptedReader(string name, Func<int, Exception?> failure) : IGitReader
    {
        private int _calls;

        private void Gate()
        {
            var call = _calls++;
            if (failure(call) is { } exception)
            {
                throw exception;
            }
        }

        public string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout)
        {
            Gate();
            return name;
        }

        public IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public WorkingTreeStatus? GetRepositoryStatus(string workingDirectory, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public IReadOnlyList<LogCommit>? GetLog(string workingDirectory, int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }

        public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout)
        {
            Gate();
            return null;
        }
    }
}
