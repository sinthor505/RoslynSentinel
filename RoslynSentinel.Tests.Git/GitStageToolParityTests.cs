using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Whole-file <c>stage</c> (<c>git add</c>, <c>add -u</c>, <c>add -A</c>) and <c>unstage</c> (<c>git reset -- paths</c>) of the Git tool run through the reader seam.
/// Every parity test builds three identical fresh repositories and runs the tool path on each: once through a libgit2-only reader, once through a CLI-only reader and once through
/// the production composite. It asserts the response DTOs serialize identically, that <c>git ls-files -s</c> (mode, blob id, stage, path) and <c>git status --porcelain</c> are identical, that
/// <c>git fsck</c> accepts the repository, and (composite) that libgit2 served every call: no fallback, no recorded reason. The decline tests assert, one rule at a time,
/// that libgit2 throws <see cref="NotSupportedException"/> without touching the index and that the composite then answers exactly as the CLI-only reader does. The single-backend readers cannot
/// fall back, so a libgit2 failure fails a parity test. Hunk staging is not part of the seam and not tested here.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitStageToolParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private int _counter;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitstage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // Every Repository the readers open is disposed inside the call that opened it, so nothing holds a handle here.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static GitImpl Impl(IGitReader reader) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Delete(string directory, string relativePath) =>
        File.Delete(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Commit(string directory, string message)
    {
        RunGit(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "-m", message);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    private static string Json(params string[] paths) => JsonSerializer.Serialize(paths);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>A fresh repository (autocrlf and filemode pinned repo-locally, so the machine's global config cannot change the outcome) with <paramref name="arrange"/> applied.</summary>
    private string NewRepo(Action<string>? arrange = null, string autocrlf = "false")
    {
        var directory = Path.Combine(_root, "r" + ++_counter);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", autocrlf);
        RunGit(directory, "config", "core.filemode", "false");
        arrange?.Invoke(directory);
        return directory;
    }

    // A gitlink's commit id is the nested repository's HEAD, which differs between twin repositories (commit timestamps): normalised.
    private static string IndexListing(string directory) =>
        System.Text.RegularExpressions.Regex.Replace(
            RunGit(directory, "-c", "core.quotePath=false", "ls-files", "-s"), "(?m)^160000 [0-9a-f]+", "160000 <gitlink>");

    private static string Porcelain(string directory) => RunGit(directory, "-c", "core.quotePath=false", "status", "--porcelain=v1", "--untracked-files=all");

    /// <summary>The index and working-tree state of <paramref name="actual"/> equals the CLI-staged <paramref name="expected"/>, and git accepts the repository.</summary>
    private static void AssertSameIndex(string actual, string expected, string what)
    {
        Assert.Multiple(() =>
        {
            Assert.That(IndexListing(actual), Is.EqualTo(IndexListing(expected)), $"{what}: git ls-files -s differs");
            Assert.That(Porcelain(actual), Is.EqualTo(Porcelain(expected)), $"{what}: git status --porcelain differs");
            var fsck = RunGitRaw(actual, "fsck", "--no-dangling", "--no-progress");
            Assert.That(fsck.ExitCode, Is.Zero, $"{what}: git fsck failed: {fsck.StdErr}");
            var index = File.ReadAllBytes(Path.Combine(GitDirectory(actual), "index"));
            Assert.That(Encoding.ASCII.GetString(index, 0, 4), Is.EqualTo("DIRC"), $"{what}: the index file lost its signature");
        });
    }

    private static string GitDirectory(string workDirectory) => RunGit(workDirectory, "rev-parse", "--absolute-git-dir");

    /// <summary>
    /// Runs <paramref name="run"/> on three identical repositories (libgit2-only, CLI-only, composite) and asserts the DTOs and index states agree and that the composite never
    /// fell back. <paramref name="locate"/> maps a repository to the directory the tool runs in (a linked worktree, say). Returns the CLI-backed result and its directory.
    /// </summary>
    private async Task<(GitStatusResult Result, string Directory)> ParityAsync(
        Action<string> arrange,
        Func<GitImpl, string, Task<GitStatusResult>> run,
        string autocrlf = "false",
        Func<string, string>? locate = null)
    {
        locate ??= repo => repo;
        var cliDir = locate(NewRepo(arrange, autocrlf));
        var libDir = locate(NewRepo(arrange, autocrlf));
        var compositeDir = locate(NewRepo(arrange, autocrlf));

        var cli = await run(Impl(new CliGitReader()), cliDir);
        var lib = await run(Impl(new Libgit2GitReader()), libDir);
        var composite = Composite();
        var viaComposite = await run(Impl(composite), compositeDir);

        Assert.Multiple(() =>
        {
            Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)), $"libgit2-backed DTO differs from the CLI-backed one\nlibgit2: {Serialize(lib)}\ncli:     {Serialize(cli)}");
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(cli)), $"composite DTO differs from the CLI-backed one\ncomposite: {Serialize(viaComposite)}\ncli:       {Serialize(cli)}");
            Assert.That(composite.CliFallbackCount, Is.Zero, "libgit2 should serve every call here; first fallback: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.DeclineCount, Is.Zero, "libgit2 should serve every call here; last decline: " + composite.LastDeclineReason);
            Assert.That(composite.Libgit2Count, Is.GreaterThan(0));
        });
        AssertSameIndex(libDir, cliDir, "libgit2-only");
        AssertSameIndex(compositeDir, cliDir, "composite");
        return (cli, cliDir);
    }

    /// <summary>
    /// Runs <paramref name="run"/> through the composite on a repository libgit2 declines to write, and through the CLI-only reader on a twin: same DTO, same index, and the
    /// composite recorded a fallback whose reason contains <paramref name="reasonFragment"/>. The libgit2 reader itself, called directly with <paramref name="direct"/>, throws and leaves the index byte-identical.
    /// </summary>
    private async Task<GitStatusResult> DeclineParityAsync(
        Action<string> arrange,
        Func<GitImpl, string, Task<GitStatusResult>> run,
        Action<IGitReader, string> direct,
        string reasonFragment,
        string autocrlf = "false")
    {
        var cliDir = NewRepo(arrange, autocrlf);
        var compositeDir = NewRepo(arrange, autocrlf);

        var before = File.ReadAllBytes(Path.Combine(GitDirectory(compositeDir), "index"));
        var thrown = Assert.Throws<NotSupportedException>(() => direct(new Libgit2GitReader(), compositeDir));
        Assert.That(thrown!.Message, Does.Contain(reasonFragment));
        Assert.That(File.ReadAllBytes(Path.Combine(GitDirectory(compositeDir), "index")), Is.EqualTo(before), "a decline must leave the index byte-identical");

        var cli = await run(Impl(new CliGitReader()), cliDir);
        var composite = Composite();
        var viaComposite = await run(Impl(composite), compositeDir);

        Assert.Multiple(() =>
        {
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(cli)), "composite DTO differs from the CLI-backed one");
            Assert.That(composite.DeclineCount, Is.GreaterThan(0), "the composite should have fallen back to the CLI (failure reason: " + composite.FirstFallbackReason + ")");
            // The LAST decline: a later reader call of the same tool call (the status read after a stage) may decline for another reason and overwrite it (a
            // submodule repository, say), so the specific reason is asserted on the direct libgit2 throw above, not here.
            Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
        });
        AssertSameIndex(compositeDir, cliDir, "composite after a decline");
        return cli;
    }

    private static Task<GitStatusResult> StageListed(GitImpl impl, string repo, params string[] paths) =>
        impl.StageAsync(repo, GitStageScope.listed, Json(paths), CancellationToken.None);

    private static Task<GitStatusResult> Unstage(GitImpl impl, string repo, params string[] paths) =>
        impl.UnstageAsync(repo, paths.Length == 0 ? null : Json(paths), CancellationToken.None);

    private static void StageAllDirect(IGitReader reader, string repo) =>
        reader.StageWholeFiles(repo, IndexStageMode.AddAll, [], Timeout, CancellationToken.None);

    private static void StageListedDirect(IGitReader reader, string repo, params string[] paths) =>
        reader.StageWholeFiles(repo, IndexStageMode.AddListed, paths, Timeout, CancellationToken.None);

    private static void UpdateListedDirect(IGitReader reader, string repo, params string[] paths) =>
        reader.StageWholeFiles(repo, IndexStageMode.UpdateListed, paths, Timeout, CancellationToken.None);

    private static void Baseline(string repo)
    {
        Write(repo, "a.txt", "a\n");
        Write(repo, "dir/b.txt", "b\n");
        Write(repo, "dir/sub/c.txt", "c\n");
        Write(repo, "dir with space/é ü file.txt", "e\n");
        Write(repo, "日本語/\U0001F600.txt", "j\n");
        Write(repo, ".gitignore", "*.log\nbuild/\n");
        RunGit(repo, "add", "-A");
        Commit(repo, "baseline");
    }

    // ---- stage: moved to libgit2 (every one asserts libgit2 served all calls) ----

    [Test]
    public async Task Stage_Listed_ModifiedNewNonAsciiAndSpaces_ParityAndServedByLibgit2Async()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\nchanged\n");
                Write(repo, "dir with space/é ü file.txt", "e\nchanged\n");
                Write(repo, "日本語/\U0001F600.txt", "j\nchanged\n");
                Write(repo, "new file.txt", "n\n");
                Write(repo, "fresh/x.txt", "x\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt", "dir with space/é ü file.txt", "日本語/\U0001F600.txt", "new file.txt", "fresh"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("fresh/x.txt").And.Contain("new file.txt"));
        Assert.That(result.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Stage_Tracked_ModifiedAndDeleted_UntrackedLeftAloneAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\nchanged\n");
                Delete(repo, "dir/b.txt");
                Write(repo, "untracked.txt", "u\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.tracked, null, CancellationToken.None));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Not.Contain("untracked.txt").And.Not.Contain("dir/b.txt"));
        Assert.That(result.Untracked, Does.Contain("untracked.txt"));
    }

    [Test]
    public async Task Stage_All_ModifiedDeletedRenamedNewDirectoryAndIgnoredFilesAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\nchanged\n");
                Delete(repo, "dir/b.txt");
                // rename: the old path is deleted, the new one untracked
                Delete(repo, "dir/sub/c.txt");
                Write(repo, "dir/sub/c2.txt", "c\n");
                Write(repo, "newdir/n.txt", "n\n");
                Write(repo, "newdir/deeper/m.txt", "m\n");
                Write(repo, "newdir/skip.log", "ignored\n");
                Write(repo, "build/out.txt", "ignored dir\n");
                Directory.CreateDirectory(Path.Combine(repo, "emptydir"));
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None));

        Assert.That(result.IsError, Is.False, result.Error);
        var index = IndexListing(dir);
        Assert.That(index, Does.Contain("newdir/n.txt").And.Contain("newdir/deeper/m.txt").And.Contain("dir/sub/c2.txt"));
        Assert.That(index, Does.Not.Contain("skip.log").And.Not.Contain("build/out.txt").And.Not.Contain("dir/b.txt").And.Not.Contain("dir/sub/c.txt"));
    }

    [Test]
    public async Task Stage_Listed_DeletedTrackedFile_StagesTheDeletionAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Delete(repo, "dir/b.txt");
            },
            (impl, repo) => StageListed(impl, repo, "dir/b.txt"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Not.Contain("dir/b.txt"));
    }

    [Test]
    public async Task Stage_Listed_TrackedDirectoryWithModificationDeletionAndNewFileAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "dir/b.txt", "b\nchanged\n");
                Delete(repo, "dir/sub/c.txt");
                Write(repo, "dir/new.txt", "new\n");
                Write(repo, "dir/ignored.log", "ignored\n");
            },
            (impl, repo) => StageListed(impl, repo, "dir"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("dir/new.txt").And.Not.Contain("ignored.log").And.Not.Contain("dir/sub/c.txt"));
    }

    [Test]
    public async Task Stage_Listed_UntrackedDirectoryWithIgnoredFileInsideAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "fresh/a.txt", "a\n");
                Write(repo, "fresh/skip.log", "ignored\n");
                Write(repo, "fresh/inner/b.txt", "b\n");
            },
            (impl, repo) => StageListed(impl, repo, "fresh"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("fresh/a.txt").And.Contain("fresh/inner/b.txt").And.Not.Contain("skip.log"));
    }

    [Test]
    public async Task Stage_Listed_ExplicitIgnoredFile_IsRefusedByTheToolBeforeAnythingIsStagedAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "x.log", "ignored\n");
                Write(repo, "ok.txt", "ok\n");
            },
            (impl, repo) => StageListed(impl, repo, "ok.txt", "x.log"));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Error, Does.Contain("ignored by .gitignore"));
        Assert.That(IndexListing(dir), Does.Not.Contain("ok.txt"));
    }

    [TestCase("true")]
    [TestCase("input")]
    [TestCase("false")]
    public async Task Stage_Autocrlf_LfWorkingFiles_BlobsEqualWhatGitAddProducesAsync(string autocrlf)
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\nchanged\nlines\n");
                Write(repo, "new.txt", "one\ntwo\n");
                Write(repo, "no-eol.txt", "no final newline");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None),
            autocrlf);

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("no-eol.txt"));
    }

    [Test]
    public async Task Stage_GitAttributesWithoutConversion_TextEolBinaryAndTextAutoAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, ".gitattributes", "* text=auto\n*.t text\n*.lf text eol=lf\n*.crlf text eol=crlf\n*.bin -text\n*.dat binary\n");
                Write(repo, "x.t", "t\n");
                Write(repo, "x.lf", "l\n");
                Write(repo, "x.crlf", "c\n");
                WriteBytes(repo, "x.bin", [1, 2, 0, 3, 10, 4]);
                WriteBytes(repo, "x.dat", [0, 0, 0, 10]);
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None),
            autocrlf: "true");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("x.bin").And.Contain("x.dat").And.Contain("x.crlf"));
    }

    [Test]
    public async Task Stage_GitAttributesWithFilterOnlyInASubdirectory_RootFilesStayOnLibgit2Async()
    {
        // sub/.gitattributes applies to sub/ only: a root file never meets it, so libgit2 serves the call.
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "sub/.gitattributes", "*.txt filter=nothing\n");
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.Staged.Select(entry => entry.Path), Does.Contain("a.txt"));
        Assert.That(dir, Is.Not.Null);
    }

    [TestCase("false")]
    [TestCase("true")]
    public async Task Stage_ExecutableBit_ExistingAndNewFiles_ModesMatchGitAsync(string filemode)
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Write(repo, "run.sh", "echo 1\n");
                Write(repo, "plain.txt", "p\n");
                RunGit(repo, "add", "-A");
                RunGit(repo, "update-index", "--chmod=+x", "run.sh");
                Commit(repo, "baseline");
                RunGit(repo, "config", "core.filemode", filemode);
                Write(repo, "run.sh", "echo 2\n");
                Write(repo, "plain.txt", "p\nchanged\n");
                Write(repo, "new.sh", "echo new\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("100644 ").And.Contain("new.sh"));
    }

    [TestCase("false")]
    [TestCase("true")]
    public async Task Stage_ExecutableBit_ModeOnlyDifference_ModesMatchGitAsync(string filemode)
    {
        // The content is unchanged: only core.filemode decides whether the 100755 index entry differs from the working file (on Windows, where files have no
        // executable bit, git with filemode=true sees every such entry as changed to 100644).
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Write(repo, "run.sh", "echo 1\n");
                RunGit(repo, "add", "-A");
                RunGit(repo, "update-index", "--chmod=+x", "run.sh");
                Commit(repo, "baseline");
                RunGit(repo, "config", "core.filemode", filemode);
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.tracked, null, CancellationToken.None));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("run.sh"));
    }

    [Test]
    public async Task Stage_UnbornBranch_ListedAndAllAsync()
    {
        var (listed, _) = await ParityAsync(
            repo => Write(repo, "first.txt", "f\n"),
            (impl, repo) => StageListed(impl, repo, "first.txt"));
        var (all, dir) = await ParityAsync(
            repo =>
            {
                Write(repo, "first.txt", "f\n");
                Write(repo, "sub/second.txt", "s\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None));

        Assert.That(listed.IsError, Is.False, listed.Error);
        Assert.That(all.IsError, Is.False, all.Error);
        Assert.That(IndexListing(dir), Does.Contain("sub/second.txt"));
    }

    [Test]
    public async Task Stage_InALinkedWorktree_WritesTheWorktreesOwnIndexAsync()
    {
        string mainRepo = null!;
        var (result, dir) = await ParityAsync(
            Baseline,
            (impl, repo) => StageListed(impl, repo, "a.txt", "wt-new.txt", "dir"),
            locate: repo =>
            {
                var worktree = Path.Combine(_root, "wt" + ++_counter);
                RunGit(repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
                Write(worktree, "a.txt", "a\nchanged in worktree\n");
                Write(worktree, "wt-new.txt", "w\n");
                Delete(worktree, "dir/b.txt");
                mainRepo = repo;
                return worktree;
            });

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Does.Contain("wt-new.txt").And.Not.Contain("dir/b.txt"));
        // The main repository's index is not the one that was written.
        Assert.That(Porcelain(mainRepo), Is.Empty);
        Assert.That(IndexListing(mainRepo), Does.Not.Contain("wt-new.txt"));
    }

    [Test]
    public async Task Stage_IndexWithUntrackedCacheExtension_StaysValidAfterLibgit2WriteAsync()
    {
        // The UNTR extension is optional: libgit2 drops it on write and git rebuilds it. With the config off, libgit2 serves the call.
        var (result, _) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "update-index", "--untracked-cache", "--force-untracked-cache");
                RunGitRaw(repo, "config", "--unset", "core.untrackedCache"); // exit 5 when this git did not set it
                RunGit(repo, "status", "--porcelain");
                Write(repo, "a.txt", "a\nchanged\n");
                Write(repo, "new.txt", "n\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None));

        Assert.That(result.IsError, Is.False, result.Error);
    }

    // ---- unstage ----

    private static void StageSeveralKindsOfChange(string repo, bool withIntentToAdd = false)
    {
        Write(repo, "run.sh", "echo 1\n");
        Write(repo, "gone.txt", "g\n");
        Write(repo, "old-name.txt", "renamed content that is long enough to be a rename\nsecond line\nthird line\n");
        Baseline(repo);
        RunGit(repo, "add", "-A");
        RunGit(repo, "update-index", "--chmod=+x", "run.sh");
        Commit(repo, "second");

        Write(repo, "a.txt", "a\nstaged change\n");
        Write(repo, "new.txt", "new\n");
        RunGit(repo, "rm", "--quiet", "gone.txt");
        RunGit(repo, "mv", "old-name.txt", "new-name.txt");
        RunGit(repo, "update-index", "--chmod=-x", "run.sh");
        Write(repo, "dir/sub/c.txt", "c\nstaged change\n");
        RunGit(repo, "add", "a.txt", "new.txt", "dir/sub/c.txt");
        if (withIntentToAdd)
        {
            // Status itself hands a lingering intent-to-add entry to the CLI, so only a test that unstages it can use this.
            Write(repo, "ita.txt", "ita\n");
            RunGit(repo, "add", "--intent-to-add", "ita.txt");
        }
    }

    [Test]
    public async Task Unstage_Listed_ModifiedNewDeletedRenamedModeAndIntentToAddEntriesAsync()
    {
        var (result, dir) = await ParityAsync(
            repo => StageSeveralKindsOfChange(repo, withIntentToAdd: true),
            (impl, repo) => Unstage(impl, repo, "a.txt", "new.txt", "gone.txt", "old-name.txt", "new-name.txt", "run.sh", "ita.txt", "dir/sub/c.txt", "nosuch.txt"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.Staged, Is.Empty);
        Assert.That(IndexListing(dir), Does.Not.Contain("new-name.txt").And.Not.Contain("ita.txt").And.Contain("gone.txt").And.Contain("old-name.txt"));
    }

    [Test]
    public async Task Unstage_Listed_DirectoryPathAsync()
    {
        var (result, _) = await ParityAsync(
            repo => StageSeveralKindsOfChange(repo),
            (impl, repo) => Unstage(impl, repo, "dir"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.Staged.Select(entry => entry.Path), Does.Not.Contain("dir/sub/c.txt").And.Contain("a.txt"));
    }

    [Test]
    public async Task Unstage_Listed_UnbornBranch_DropsTheEntriesAsync()
    {
        var (result, dir) = await ParityAsync(
            repo =>
            {
                Write(repo, "one.txt", "1\n");
                Write(repo, "two/three.txt", "3\n");
                RunGit(repo, "add", "-A");
            },
            (impl, repo) => Unstage(impl, repo, "one.txt", "two"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexListing(dir), Is.Empty);
    }

    [Test]
    public async Task Unstage_Listed_ExecutableEntryAbsentFromHeadAndIndexAsync()
    {
        var (result, _) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "tool.sh", "echo\n");
                RunGit(repo, "add", "tool.sh");
                RunGit(repo, "update-index", "--chmod=+x", "tool.sh");
            },
            (impl, repo) => Unstage(impl, repo, "tool.sh", "never-existed.txt"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.Untracked, Does.Contain("tool.sh"));
    }

    [Test]
    public async Task Unstage_Listed_FilterAttributeDoesNotMatterBecauseNothingIsConvertedAsync()
    {
        var (result, _) = await ParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, ".gitattributes", "*.txt filter=nothing\n");
                Write(repo, "a.txt", "a\nchanged\n");
                RunGit(repo, "add", "a.txt");
            },
            (impl, repo) => Unstage(impl, repo, "a.txt"));

        Assert.That(result.IsError, Is.False, result.Error);
    }

    // ---- decline rules: libgit2 steps aside, git answers ----

    [TestCase("true")]
    [TestCase("input")]
    [TestCase("false")]
    public async Task Decline_FileWithCarriageReturn_GitConvertsOrKeepsItAsConfiguredAsync(string autocrlf)
    {
        var cli = await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\r\nchanged\r\n");
                Write(repo, "lone-cr.txt", "x\ry\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt", "lone-cr.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt", "lone-cr.txt"),
            "carriage return",
            autocrlf);

        Assert.That(cli.IsError, Is.False, cli.Error);
    }

    [Test]
    public async Task Decline_CarriageReturnInTrackedFile_AddUpdateTrackedAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "a.txt", "a\r\nchanged\r\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.tracked, null, CancellationToken.None),
            (reader, repo) => reader.StageWholeFiles(repo, IndexStageMode.UpdateTracked, [], Timeout, CancellationToken.None),
            "carriage return",
            autocrlf: "true");
    }

    [Test]
    public async Task Decline_SafecrlfTrue_GitMayRefuseTheAddAsync()
    {
        var cli = await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "config", "core.safecrlf", "true");
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            "core.safecrlf",
            autocrlf: "true");

        // git itself decides whether this add is allowed; the test only pins that its answer is passed through unchanged.
        Assert.That(cli, Is.Not.Null);
    }

    [TestCase("*.txt filter=cleaner")]
    [TestCase("*.txt ident")]
    [TestCase("*.txt working-tree-encoding=UTF-8")]
    public async Task Decline_ConversionAttributeInRootGitAttributesAsync(string attribute)
    {
        var cli = await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "config", "filter.cleaner.clean", "cat");
                RunGit(repo, "config", "filter.cleaner.smudge", "cat");
                Write(repo, ".gitattributes", attribute + "\n");
                Write(repo, "a.txt", "a\nchanged $Id$\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            "filter, ident or working-tree-encoding");

        Assert.That(cli.IsError, Is.False, cli.Error);
    }

    [Test]
    public async Task Decline_ConversionAttributeInSubdirectoryAndInfoAttributesAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "dir/.gitattributes", "*.txt filter=cleaner\n");
                Write(repo, "dir/b.txt", "b\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "dir/b.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "dir/b.txt"),
            "filter, ident or working-tree-encoding");

        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Directory.CreateDirectory(Path.Combine(repo, ".git", "info"));
                File.WriteAllText(Path.Combine(repo, ".git", "info", "attributes"), "*.txt filter=cleaner\n");
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            "filter, ident or working-tree-encoding");
    }

    [Test]
    public async Task Decline_GitlinkInTheIndexAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "update-index", "--add", "--cacheinfo", "160000," + new string('a', 40) + ",vend");
                Commit(repo, "gitlink");
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            "gitlink");
    }

    [Test]
    public async Task Decline_SubmoduleConfiguredInGitmodulesAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, ".gitmodules", "[submodule \"vend\"]\n\tpath = vend\n\turl = https://example.invalid/vend.git\n");
                RunGit(repo, "add", ".gitmodules");
                Commit(repo, "gitmodules");
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            "submodules");
    }

    private static void MakeConflict(string repo)
    {
        Write(repo, "conflict.txt", "base\n");
        Write(repo, "other.txt", "o\n");
        RunGit(repo, "add", "-A");
        Commit(repo, "base");
        var main = RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(repo, "checkout", "--quiet", "-b", "side");
        Write(repo, "conflict.txt", "side\n");
        RunGit(repo, "add", "-A");
        Commit(repo, "side");
        RunGit(repo, "checkout", "--quiet", main);
        Write(repo, "conflict.txt", "main\n");
        RunGit(repo, "add", "-A");
        Commit(repo, "main");
        var merge = RunGitRaw(repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "side");
        Assert.That(merge.ExitCode, Is.Not.Zero, "the merge should conflict: " + merge.StdOut);
        Write(repo, "conflict.txt", "resolved\n");
    }

    [Test]
    public async Task Decline_UnmergedEntry_StagingTheResolutionAndUnstagingAreGitsAsync()
    {
        var staged = await DeclineParityAsync(
            MakeConflict,
            (impl, repo) => StageListed(impl, repo, "conflict.txt"),
            (reader, repo) => UpdateListedDirect(reader, repo, "conflict.txt"),
            "unmerged");
        Assert.That(staged.IsError, Is.False, staged.Error);

        await DeclineParityAsync(
            MakeConflict,
            (impl, repo) => Unstage(impl, repo, "conflict.txt"),
            (reader, repo) => reader.UnstagePaths(repo, ["conflict.txt"], Timeout, CancellationToken.None),
            "unmerged");
    }

    [Test]
    public async Task Decline_IntentToAddEntry_StageAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                Write(repo, "ita.txt", "i\n");
                RunGit(repo, "add", "--intent-to-add", "ita.txt");
                Write(repo, "ita.txt", "i\nmore\n");
            },
            (impl, repo) => StageListed(impl, repo, "ita.txt"),
            (reader, repo) => UpdateListedDirect(reader, repo, "ita.txt"),
            "intent-to-add");
    }

    [Test]
    public async Task Decline_AssumeUnchangedEntryAsync()
    {
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "update-index", "--assume-unchanged", "a.txt");
                Write(repo, "a.txt", "a\nchanged\n");
                Write(repo, "dir/b.txt", "b\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "dir/b.txt"),
            (reader, repo) => UpdateListedDirect(reader, repo, "dir/b.txt"),
            "assume-unchanged");
    }

    [Test]
    public async Task Decline_EmbeddedRepository_AddAllStagesItAsAGitlinkInGitAsync()
    {
        var cli = await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                InitRepo(Path.Combine(repo, "nested"));
                Write(repo, "nested/inner.txt", "inner\n");
                Commit(Path.Combine(repo, "nested"), "inner");
                Write(repo, "plain.txt", "p\n");
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None),
            (reader, repo) => StageAllDirect(reader, repo),
            "embedded");

        Assert.That(cli.IsError, Is.False, cli.Error);
    }

    [Test]
    public async Task Decline_Symlink_NeedsPrivilegeToCreateAsync()
    {
        // Skipped (Assert.Ignore) where creating a symlink is not permitted: Windows needs the privilege or developer mode.
        await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                try
                {
                    File.CreateSymbolicLink(Path.Combine(repo, "link.txt"), "a.txt");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    Assert.Ignore("Creating a symlink is not permitted here: " + ex.Message);
                }
            },
            (impl, repo) => impl.StageAsync(repo, GitStageScope.all, null, CancellationToken.None),
            (reader, repo) => StageAllDirect(reader, repo),
            "symlink");
    }

    [TestCase("core.untrackedCache", "true")]
    [TestCase("core.splitIndex", "true")]
    [TestCase("index.version", "4")]
    [TestCase("core.sparseCheckout", "true")]
    public async Task Decline_IndexFormatConfigAsync(string key, string value)
    {
        var cli = await DeclineParityAsync(
            repo =>
            {
                Baseline(repo);
                RunGit(repo, "config", key, value);
                Write(repo, "a.txt", "a\nchanged\n");
            },
            (impl, repo) => StageListed(impl, repo, "a.txt"),
            (reader, repo) => StageListedDirect(reader, repo, "a.txt"),
            key.ToLowerInvariant());

        Assert.That(cli.IsError, Is.False, cli.Error);
    }

    [Test]
    public async Task Decline_SplitIndexExtensionAlreadyInTheIndexFile_Libgit2CannotReadItAndTheCliAnswersAsync()
    {
        // The 'link' extension is mandatory: libgit2 cannot read the index. The reader declines (a sharedindex file is in the git directory) before it opens the index, so this is a planned
        // decline, never a LibGit2SharpException failure (GitReaderEnvironmentTests probes every reader method on a split index).
        var cliDir = NewRepo(repo => SplitIndex(repo));
        var compositeDir = NewRepo(repo => SplitIndex(repo));
        var cli = await Impl(new CliGitReader()).StageAsync(cliDir, GitStageScope.all, null, CancellationToken.None);
        var composite = Composite();
        var viaComposite = await Impl(composite).StageAsync(compositeDir, GitStageScope.all, null, CancellationToken.None);

        Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(cli)));
        Assert.That(composite.DeclineCount, Is.GreaterThan(0));
        Assert.That(composite.CliFallbackCount, Is.Zero, "a split index is a planned decline, not a failure");
        Assert.That(composite.FirstFallbackReason, Is.Null);
        AssertSameIndex(compositeDir, cliDir, "split index");

        static void SplitIndex(string repo)
        {
            Baseline(repo);
            RunGit(repo, "update-index", "--split-index");
            RunGitRaw(repo, "config", "--unset", "core.splitIndex"); // exit 5 when this git did not set it
            Write(repo, "a.txt", "a\nchanged\n");
            Write(repo, "new.txt", "n\n");
        }
    }

    [Test]
    public void Decline_PathsMatchingNothing_GitWordsTheErrorOrAdvisory()
    {
        var repo = NewRepo(r =>
        {
            Baseline(r);
            Directory.CreateDirectory(Path.Combine(r, "emptydir"));
            Write(r, "onlyignored/skip.log", "ignored\n");
            Write(r, "x.log", "ignored\n");
        });
        var reference = NewRepo(r =>
        {
            Baseline(r);
            Directory.CreateDirectory(Path.Combine(r, "emptydir"));
            Write(r, "onlyignored/skip.log", "ignored\n");
            Write(r, "x.log", "ignored\n");
        });

        foreach (var (mode, path) in new[]
        {
            (IndexStageMode.UpdateListed, "nosuch.txt"),
            (IndexStageMode.AddListed, "nosuch.txt"),
            (IndexStageMode.AddListed, "emptydir"),
            (IndexStageMode.AddListed, "onlyignored"),
            (IndexStageMode.AddListed, "x.log"),
        })
        {
            var before = IndexListing(repo);
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().StageWholeFiles(repo, mode, [path], Timeout, CancellationToken.None), $"{mode} {path}");
            Assert.That(IndexListing(repo), Is.EqualTo(before));

            var viaComposite = Composite().StageWholeFiles(repo, mode, [path], Timeout, CancellationToken.None);
            var viaCli = new CliGitReader().StageWholeFiles(reference, mode, [path], Timeout, CancellationToken.None);
            Assert.That((viaComposite.ExitCode, viaComposite.StdErr), Is.EqualTo((viaCli.ExitCode, viaCli.StdErr)), $"{mode} {path}");
        }
    }

    [Test]
    public void Stage_NothingToDo_IsServedByLibgit2AndDoesNotRewriteTheIndex()
    {
        var repo = NewRepo(Baseline);
        var indexFile = Path.Combine(GitDirectory(repo), "index");
        var before = File.ReadAllBytes(indexFile);
        var composite = Composite();

        var all = composite.StageWholeFiles(repo, IndexStageMode.AddAll, [], Timeout, CancellationToken.None);
        var tracked = composite.StageWholeFiles(repo, IndexStageMode.UpdateTracked, [], Timeout, CancellationToken.None);
        var listed = composite.StageWholeFiles(repo, IndexStageMode.AddListed, ["a.txt", "dir"], Timeout, CancellationToken.None);
        var unstaged = composite.UnstagePaths(repo, ["a.txt", "dir"], Timeout, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(new[] { all.ExitCode, tracked.ExitCode, listed.ExitCode, unstaged.ExitCode }, Is.All.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero, composite.FirstFallbackReason);
            Assert.That(composite.DeclineCount, Is.Zero, composite.LastDeclineReason);
            Assert.That(composite.Libgit2Count, Is.EqualTo(4));
            Assert.That(File.ReadAllBytes(indexFile), Is.EqualTo(before), "nothing changed, so the index file must not be rewritten");
            Assert.That(File.Exists(indexFile + ".lock"), Is.False, "the lock this call took must be gone");
        });
    }

    [TestCase("./a.txt")]
    [TestCase("dir//b.txt")]
    [TestCase("dir/../a.txt")]
    [TestCase("dir/")]
    [TestCase("")]
    [TestCase("/a.txt")]
    [TestCase("C:/x/a.txt")]
    [TestCase("dir\\b.txt")]
    public void Decline_Stage_PathsGitNormalisesOrRejects(string path)
    {
        var repo = NewRepo(r =>
        {
            Baseline(r);
            Write(r, "a.txt", "a\nchanged\n");
        });
        var before = IndexListing(repo);

        foreach (var mode in new[] { IndexStageMode.UpdateListed, IndexStageMode.AddListed })
        {
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().StageWholeFiles(repo, mode, [path], Timeout, CancellationToken.None), $"{mode} '{path}'");
        }

        Assert.That(IndexListing(repo), Is.EqualTo(before), "a decline must not touch the index");
    }

    [Test]
    public void Decline_WorkingDirectoryBelowTheRootAndNotARepository()
    {
        var repo = NewRepo(r =>
        {
            Baseline(r);
            Write(r, "dir/b.txt", "b\nchanged\n");
        });

        Assert.That(
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().StageWholeFiles(Path.Combine(repo, "dir"), IndexStageMode.UpdateTracked, [], Timeout, CancellationToken.None))!.Message,
            Does.Contain("below the repository root"));
        var outside = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(outside);
        Assert.That(
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().UnstagePaths(outside, ["a.txt"], Timeout, CancellationToken.None))!.Message,
            Does.Contain("not inside a git repository"));
    }

    [TestCase("a.txt")]
    [TestCase("")]
    [TestCase("*.txt")]
    [TestCase("dir/[bc].txt")]
    [TestCase("./a.txt")]
    [TestCase(":(top)a.txt")]
    public void Decline_Unstage_WholeIndexGlobsAndPathsGitNormalises(string path)
    {
        // "a.txt" alone is the served case; with no paths at all, a glob, a normalisable path or pathspec magic the CLI answers.
        var repo = NewRepo(r =>
        {
            Baseline(r);
            Write(r, "a.txt", "a\nstaged\n");
            RunGit(r, "add", "a.txt");
        });
        var reference = NewRepo(r =>
        {
            Baseline(r);
            Write(r, "a.txt", "a\nstaged\n");
            RunGit(r, "add", "a.txt");
        });

        IReadOnlyList<string> paths = path == "a.txt" ? [path] : path.Length == 0 ? [] : [path];
        var composite = Composite();
        if (path == "a.txt")
        {
            composite.UnstagePaths(repo, paths, Timeout, CancellationToken.None);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a plain literal path is served by libgit2: " + composite.FirstFallbackReason);
            Assert.That(composite.DeclineCount, Is.Zero, "a plain literal path is served by libgit2: " + composite.LastDeclineReason);
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().UnstagePaths(repo, paths, Timeout, CancellationToken.None));
            composite.UnstagePaths(repo, paths, Timeout, CancellationToken.None);
            Assert.That(composite.DeclineCount, Is.EqualTo(1));
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
        }

        new CliGitReader().UnstagePaths(reference, paths, Timeout, CancellationToken.None);
        AssertSameIndex(repo, reference, $"unstage '{path}'");
    }

    // ---- index.lock ----

    [Test]
    public async Task IndexLock_HeldByAnotherProcess_Libgit2DeclinesAndGitReportsItsOwnErrorAsync()
    {
        var repo = NewRepo(r =>
        {
            Baseline(r);
            Write(r, "a.txt", "a\nchanged\n");
            Write(r, "new.txt", "n\n");
        });
        var indexFile = Path.Combine(GitDirectory(repo), "index");
        var lockFile = indexFile + ".lock";
        File.WriteAllText(lockFile, "held by someone else");
        var indexBefore = File.ReadAllBytes(indexFile);

        // Direct libgit2 calls (stage with changes, a no-op stage, unstage): all declined, nothing overwritten.
        Assert.That(Assert.Throws<NotSupportedException>(() => StageListedDirect(new Libgit2GitReader(), repo, "a.txt", "new.txt"))!.Message, Does.Contain("index.lock"));
        Assert.That(Assert.Throws<NotSupportedException>(() => StageListedDirect(new Libgit2GitReader(), repo, "dir"))!.Message, Does.Contain("index.lock"));
        Assert.That(Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().UnstagePaths(repo, ["a.txt"], Timeout, CancellationToken.None))!.Message, Does.Contain("index.lock"));

        // Through the tool: the composite's answer is exactly what the CLI-only reader says on the same repository (git's own message).
        var cli = await Impl(new CliGitReader()).StageAsync(repo, GitStageScope.listed, Json("a.txt", "new.txt"), CancellationToken.None);
        var composite = Composite();
        var viaComposite = await Impl(composite).StageAsync(repo, GitStageScope.listed, Json("a.txt", "new.txt"), CancellationToken.None);
        var unstageCli = await Impl(new CliGitReader()).UnstageAsync(repo, Json("a.txt"), CancellationToken.None);
        var unstageComposite = await Impl(Composite()).UnstageAsync(repo, Json("a.txt"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cli.IsError, Is.True);
            Assert.That(cli.Error, Does.Contain("index.lock"));
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(cli)));
            Assert.That(composite.LastDeclineReason, Does.Contain("index.lock"));
            Assert.That(composite.CliFallbackCount, Is.Zero, "a held index.lock is a planned decline, not a failure");
            Assert.That(unstageCli.IsError, Is.True);
            Assert.That(Serialize(unstageComposite), Is.EqualTo(Serialize(unstageCli)));
            Assert.That(File.ReadAllText(lockFile), Is.EqualTo("held by someone else"), "the other process's lock must survive");
            Assert.That(File.ReadAllBytes(indexFile), Is.EqualTo(indexBefore), "the index must not be overwritten while the lock is held");
        });

        // Once the lock is gone libgit2 serves the same call.
        File.Delete(lockFile);
        var served = Composite();
        var result = await Impl(served).StageAsync(repo, GitStageScope.listed, Json("a.txt", "new.txt"), CancellationToken.None);
        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(served.CliFallbackCount, Is.Zero, served.FirstFallbackReason);
        Assert.That(served.DeclineCount, Is.Zero, served.LastDeclineReason);
        Assert.That(IndexListing(repo), Does.Contain("new.txt"));
        Assert.That(File.Exists(lockFile), Is.False, "libgit2 must release the lock it took");
    }

    [Test]
    public async Task IndexLock_RaceWithAConcurrentCliWriter_NeverLosesEitherUpdateAsync()
    {
        // A git add in another process and the server's libgit2 stage hammer the same index; each takes index.lock, so a loser either waits for the retry git does
        // or is declined to the CLI. After both finish every file is staged and git still accepts the repository.
        var repo = NewRepo(Baseline);
        const int rounds = 12;
        for (var i = 0; i < rounds; i++)
        {
            Write(repo, $"cli-{i}.txt", $"cli {i}\n");
            Write(repo, $"lib-{i}.txt", $"lib {i}\n");
        }

        var cliTask = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                // The CLI writer may lose the lock race to libgit2; git then exits non-zero and the round is repeated.
                for (var attempt = 0; attempt < 50 && RunGitRaw(repo, "add", "--", $"cli-{i}.txt").ExitCode != 0; attempt++)
                {
                    Thread.Sleep(10);
                }
            }
        });
        var composite = Composite();
        var libTask = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                // A declined call is answered by git add, which does not wait for index.lock either: a non-zero exit is the lock, so the round is repeated.
                for (var attempt = 0; attempt < 50 && composite.StageWholeFiles(repo, IndexStageMode.AddListed, [$"lib-{i}.txt"], Timeout, CancellationToken.None).ExitCode != 0; attempt++)
                {
                    Thread.Sleep(10);
                }
            }
        });
        await Task.WhenAll(cliTask, libTask);

        var listing = IndexListing(repo);
        for (var i = 0; i < rounds; i++)
        {
            Assert.That(listing, Does.Contain($"cli-{i}.txt"), "a CLI-staged file was lost");
            Assert.That(listing, Does.Contain($"lib-{i}.txt"), "a libgit2-staged file was lost");
        }

        Assert.That(RunGitRaw(repo, "fsck", "--no-dangling", "--no-progress").ExitCode, Is.Zero);
    }
}
