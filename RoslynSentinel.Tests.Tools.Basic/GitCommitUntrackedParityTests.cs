using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <c>GitImpl.CommitAsync</c> with untracked files: the pre-stage <c>git add</c> (scope listed) and the stage of scope all run through the in-process stage of the reader seam, with the
/// CLI's <c>git --literal-pathspecs add</c> as the decline fallback. Every scenario runs on twin repositories, once behind a CLI-only reader and once behind the production composite, and
/// asserts the commit DTO, the facts about the new commit and the index state (<c>ls-files -s</c>, <c>status --porcelain</c>) are identical; an ordinary scenario must be served by libgit2
/// end to end (no decline, no recorded failure), a scenario libgit2 must decline (a file with a carriage return) must show a planned decline and still equal the CLI. The held-index.lock
/// test runs both readers on the SAME repository, because git's error text names the lock path.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitUntrackedParityTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-commituntracked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private static GitImpl Impl(IGitReader reader) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);

    private static void Write(string directory, string relativePath, string content) =>
        WriteBytes(directory, relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static string Paths(params string[] paths) => JsonSerializer.Serialize(paths);

    /// <summary>A repository with its config pinned repo-locally and, optionally, one commit holding README.md, a.txt and a .gitignore ("*.log").</summary>
    private string NewRepo(string name, bool initialCommit = true)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "core.filemode", "false");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        if (initialCommit)
        {
            Write(directory, "README.md", "hello\n");
            Write(directory, "a.txt", "a\n");
            Write(directory, ".gitignore", "*.log\n");
            CommitAll(directory, "initial commit");
        }

        return directory;
    }

    private static string IndexState(string directory) =>
        RunGit(directory, "-c", "core.quotePath=false", "ls-files", "-s") + "\n--\n" +
        RunGit(directory, "-c", "core.quotePath=false", "status", "--porcelain=v1", "--untracked-files=all");

    /// <summary>One scenario: the repository (made fresh per call) and the commit the tool is asked for.</summary>
    private (string Directory, string? Message, GitStageScope? Scope, string? Paths) Arrange(string scenario, string name)
    {
        switch (scenario)
        {
            case "listed-new-file":
            {
                var repo = NewRepo(name);
                Write(repo, "new.txt", "new\n");
                return (repo, "add new file", GitStageScope.listed, "new.txt");
            }

            case "listed-modified-and-new":
            {
                var repo = NewRepo(name);
                Write(repo, "a.txt", "changed\n");
                Write(repo, "new.txt", "new\n");
                return (repo, "modified and new", GitStageScope.listed, Paths("a.txt", "new.txt"));
            }

            case "listed-non-ascii-and-spaces":
            {
                var repo = NewRepo(name);
                Write(repo, "dir with space/é naïve file.txt", "text\n");
                Write(repo, "plain.txt", "plain\n");
                return (repo, "non-ascii and spaces", GitStageScope.listed, Paths("dir with space/é naïve file.txt", "plain.txt"));
            }

            case "listed-untracked-directory":
            {
                var repo = NewRepo(name);
                Write(repo, "newdir/one.txt", "1\n");
                Write(repo, "newdir/two.txt", "2\n");
                return (repo, "new directory", GitStageScope.listed, "newdir");
            }

            case "scope-all":
            {
                var repo = NewRepo(name);
                Write(repo, "a.txt", "changed\n");
                Write(repo, "new.txt", "new\n");
                Write(repo, "x.log", "ignored\n");
                return (repo, "stage everything", GitStageScope.all, null);
            }

            case "ignored-file-listed":
            {
                var repo = NewRepo(name);
                Write(repo, "x.log", "ignored\n");
                return (repo, "commit an ignored file", GitStageScope.listed, "x.log");
            }

            case "ignored-file-among-new-files":
            {
                var repo = NewRepo(name);
                Write(repo, "new.txt", "new\n");
                Write(repo, "x.log", "ignored\n");
                return (repo, "mixed list", GitStageScope.listed, Paths("new.txt", "x.log"));
            }

            case "carriage-return-file":
            {
                var repo = NewRepo(name);
                WriteBytes(repo, "crlf.txt", "one\r\ntwo\r\n"u8.ToArray());
                return (repo, "a file with CRLF", GitStageScope.listed, "crlf.txt");
            }

            case "unborn-branch":
            {
                var repo = NewRepo(name, initialCommit: false);
                Write(repo, "first.txt", "first\n");
                return (repo, "first ever commit", GitStageScope.listed, "first.txt");
            }

            case "pre-commit-hook-fails":
            {
                var repo = NewRepo(name);
                File.WriteAllText(Path.Combine(repo, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho 'hook says no' >&2\nexit 1\n");
                Write(repo, "new.txt", "new\n");
                return (repo, "blocked by the hook", GitStageScope.listed, "new.txt");
            }

            case "linked-worktree":
            {
                var main = NewRepo(name + "-main");
                var worktree = Path.Combine(_root, name);
                RunGit(main, "-c", "worktree.useRelativePaths=false", "worktree", "add", "--quiet", worktree, "-b", "linked");
                Write(worktree, "wt-new.txt", "in the worktree\n");
                return (worktree, "worktree commit", GitStageScope.listed, "wt-new.txt");
            }

            default:
                throw new ArgumentException("no scenario " + scenario);
        }
    }

    private static readonly string[] Scenarios =
    [
        "listed-new-file", "listed-modified-and-new", "listed-non-ascii-and-spaces", "listed-untracked-directory", "scope-all", "ignored-file-listed",
        "ignored-file-among-new-files", "carriage-return-file", "unborn-branch", "pre-commit-hook-fails", "linked-worktree",
    ];

    private async Task<(string Json, string Facts, string Index, bool IsError)> RunAsync(string scenario, IGitReader reader, string suffix)
    {
        var (directory, message, scope, paths) = Arrange(scenario, scenario + suffix);
        var before = Path.Combine(directory);
        var named = new Dictionary<string, string>();
        if (scenario != "unborn-branch")
        {
            named[RunGit(before, "rev-parse", "HEAD")] = "before";
        }

        var result = await Impl(reader).CommitAsync(directory, message, scope, paths, amend: false, CancellationToken.None);
        var facts = result.IsError ? "no hash" : GitCommitReadsParityTests.Facts(directory, result.CommitHash, named);
        return (GitCommitReadsParityTests.CommitJson(result), facts, IndexState(directory), result.IsError);
    }

    [TestCaseSource(nameof(Scenarios))]
    public async Task Commit_WithUntrackedFiles_CompositeEqualsTheCliOnlyReader_AndLibgit2ServesTheOrdinaryOnesAsync(string scenario)
    {
        var viaCli = await RunAsync(scenario, new CliGitReader(), "-cli");
        var composite = Composite();
        var viaComposite = await RunAsync(scenario, composite, "-composite");
        var mustDecline = scenario == "carriage-return-file";

        Assert.Multiple(() =>
        {
            Assert.That(viaComposite.Json, Is.EqualTo(viaCli.Json), "tool DTO differs between the composite and the CLI-only reader");
            Assert.That(viaComposite.Facts, Is.EqualTo(viaCli.Facts), "the commit that was made differs");
            Assert.That(viaComposite.Index, Is.EqualTo(viaCli.Index), "the index and working-tree state afterwards differ");
            Assert.That(composite.CliFallbackCount, Is.Zero, "no real libgit2 failure: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.Libgit2Count, Is.GreaterThan(0), "libgit2 served part of the flow");
            if (mustDecline)
            {
                Assert.That(composite.DeclineCount, Is.GreaterThan(0), "libgit2 must decline the stage of a file with a carriage return");
                Assert.That(composite.LastDeclineReason, Does.Contain("carriage return").IgnoreCase);
            }
            else
            {
                Assert.That(composite.DeclineCount, Is.Zero, "nothing here may be declined: " + composite.LastDeclineReason);
            }
        });
    }

    [Test]
    public async Task Commit_Outcomes_AreWhatTheScenariosPromiseAsync()
    {
        var ignored = await RunAsync("ignored-file-listed", Composite(), "-outcome1");
        var mixed = await RunAsync("ignored-file-among-new-files", Composite(), "-outcome2");
        var hook = await RunAsync("pre-commit-hook-fails", Composite(), "-outcome3");
        var all = await RunAsync("scope-all", Composite(), "-outcome4");
        var nonAscii = await RunAsync("listed-non-ascii-and-spaces", Composite(), "-outcome5");

        Assert.Multiple(() =>
        {
            Assert.That(ignored.IsError, Is.True);
            Assert.That(ignored.Json, Does.Contain("ignored by .gitignore"));
            Assert.That(mixed.IsError, Is.True);
            Assert.That(mixed.Index, Does.Contain("?? new.txt").And.Not.Contain("A  new.txt"), "the refusal happens before anything is staged");
            Assert.That(hook.IsError, Is.True);
            Assert.That(hook.Json, Does.Contain("hook says no").And.Contain("Staging already ran; the index now holds 1 staged paths"), "the untracked file was staged before the hook refused");
            Assert.That(hook.Index, Does.Contain("A  new.txt"));
            Assert.That(all.Facts, Does.Contain("files=a.txt,new.txt"), "scope all stages the modified and the new file, not the ignored one");
            Assert.That(nonAscii.Facts, Does.Contain("files=dir with space/é naïve file.txt,plain.txt"));
        });
    }

    [Test]
    public async Task Commit_IndexLockHeldByAnotherProcess_BothReadersGiveGitsOwnErrorAndLeaveTheLockAsync()
    {
        var repo = NewRepo("locked");
        Write(repo, "new.txt", "new\n");
        var indexFile = Path.Combine(RunGit(repo, "rev-parse", "--absolute-git-dir"), "index");
        var lockFile = indexFile + ".lock";
        File.WriteAllText(lockFile, "held by someone else");
        var indexBefore = File.ReadAllBytes(indexFile);

        var viaCli = await Impl(new CliGitReader()).CommitAsync(repo, "locked", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
        var composite = Composite();
        var viaComposite = await Impl(composite).CommitAsync(repo, "locked", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(viaCli.IsError, Is.True);
            Assert.That(viaCli.Error, Does.Contain("index.lock"), "git's own message names the lock");
            Assert.That(GitCommitReadsParityTests.CommitJson(viaComposite), Is.EqualTo(GitCommitReadsParityTests.CommitJson(viaCli)), "the composite reports exactly what the CLI-only reader reports");
            Assert.That(composite.LastDeclineReason, Does.Contain("index.lock"));
            Assert.That(composite.CliFallbackCount, Is.Zero, "a held lock is a planned decline, not a failure");
            Assert.That(File.ReadAllText(lockFile), Is.EqualTo("held by someone else"), "the other process's lock must survive");
            Assert.That(File.ReadAllBytes(indexFile), Is.EqualTo(indexBefore), "the index must not be touched while the lock is held");
        });

        // Once the lock is gone libgit2 serves the whole call.
        File.Delete(lockFile);
        var served = Composite();
        var result = await Impl(served).CommitAsync(repo, "locked", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(served.CliFallbackCount, Is.Zero, served.FirstFallbackReason);
            Assert.That(served.DeclineCount, Is.Zero, served.LastDeclineReason);
            Assert.That(File.Exists(lockFile), Is.False, "libgit2 must release the lock it took");
        });
    }
}
