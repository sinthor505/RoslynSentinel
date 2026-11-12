using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The in-process commit route of <c>GitImpl.CommitAsync</c> in linked worktrees and in repositories without a local
/// <c>core.autocrlf</c> pin. Companion to <c>GitInProcessCommitRouteTests</c>, which covers a plain repository. The route
/// switch lives in the common config, so a worktree inherits it from its main repository, and the commit-route log always
/// lands in the main repository's common directory, never under <c>.git/worktrees/</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // starts git processes; kept serial like GitInProcessCommitRouteTests.
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitInProcessCommitRouteSetupTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        // Short directory names: a long scenario name pushed a linked-worktree git dir past MAX_PATH once (see GitCommitSeamParityTests).
        _root = Path.Combine(Path.GetTempPath(), "rs-ipc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
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

    private static GitImpl NewImpl(InProcessCommitRoute route) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, Composite(), route);

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repository with its config pinned repo-locally (identity, no signing, no gc, hooks only from its own .git/hooks), optionally with core.autocrlf pinned to false, one commit with README.md and a.txt, and the route switched on or not.</summary>
    private string NewRepo(string name, bool inProcess, bool pinAutocrlf = true)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        if (pinAutocrlf)
        {
            RunGit(directory, "config", "core.autocrlf", "false");
        }

        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        RunGit(directory, "config", "maintenance.auto", "false");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        if (inProcess)
        {
            RunGit(directory, "config", InProcessCommitRoute.ConfigKey, "true");
        }

        Write(directory, "README.md", "hello\n");
        Write(directory, "a.txt", "a\n");
        CommitAll(directory, "initial commit");
        return directory;
    }

    private static string Head(string directory) => RunGit(directory, "rev-parse", "HEAD");

    /// <summary>The commit exists and is HEAD, whichever route made it.</summary>
    private static void AssertCommitted(string directory, GitCommitResult result)
    {
        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.CommitHash, Is.EqualTo(Head(directory)), "the reported hash is `git rev-parse HEAD`");
    }

    /// <summary>The route committed in process and declined nothing; the last commit-route log line of <paramref name="directory"/> is a taken line.</summary>
    private static void AssertTaken(InProcessCommitRoute route, string directory)
    {
        using var logged = JsonDocument.Parse(ReadCommitRouteLog(directory)[^1]);
        Assert.Multiple(() =>
        {
            Assert.That(route.TakenCount, Is.EqualTo(1), "the route should have committed (last decline: " + route.LastDeclineReason + ")");
            Assert.That(route.DeclineCount, Is.Zero, "last decline: " + route.LastDeclineReason);
            Assert.That(logged.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("taken"), "the log line is a taken commit");
        });
    }

    private static string[] ReadCommitRouteLog(string directory) =>
        File.ReadAllLines(Path.Combine(directory, ".git", "roslynsentinel", "commit-route.jsonl"));

    private static int CountTakenLines(string directory) =>
        ReadCommitRouteLog(directory).Count(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("outcome").GetString() == "taken";
        });

    private static void AssertFsckClean(string directory) => CasCommitTestSupport.AssertFsckClean(directory);

    // ---- linked worktrees ----

    [Test]
    public async Task InProcessCommit_LinkedWorktree_IsTakenOnTheWorktreeBranch_AndLogsToTheCommonDirectoryAsync()
    {
        var route = new InProcessCommitRoute();
        // The route switch is set only in the main repository's local config; the worktree reads it through the common config.
        var main = NewRepo("lw-main", inProcess: true);
        var wt = AddLinkedWorktree(main, _root, "lw-wt", "wt-feature");
        var twinMain = NewRepo("lw-twin", inProcess: false);
        var twinWt = AddLinkedWorktree(twinMain, _root, "lw-twin-wt", "wt-feature");
        var mainTipBefore = Head(main);
        var wtTipBefore = Head(wt);
        Write(wt, "w.txt", "w\n");
        RunGit(wt, "add", "w.txt");
        Write(twinWt, "w.txt", "w\n");
        RunGit(twinWt, "add", "w.txt");

        var result = await NewImpl(route).CommitAsync(wt, "worktree subject", null, null, amend: false, CancellationToken.None);
        RunGit(twinWt, "commit", "--quiet", "-m", "worktree subject");

        AssertCommitted(wt, result);
        AssertTaken(route, main);
        using var logged = JsonDocument.Parse(ReadCommitRouteLog(main)[^1]);
        Assert.Multiple(() =>
        {
            Assert.That(Head(main), Is.EqualTo(mainTipBefore), "the main working tree's branch must not move");
            Assert.That(logged.RootElement.GetProperty("ref").GetString(), Is.EqualTo("refs/heads/wt-feature"), "the log line names the worktree's branch");
            Assert.That(Directory.EnumerateFiles(Path.Combine(main, ".git", "worktrees"), "commit-route.jsonl", SearchOption.AllDirectories), Is.Empty, "no commit-route log under the worktree's git dir");
            Assert.That(CasCommitTestSupport.Reflog(wt, "HEAD")[0], Is.EqualTo(CasCommitTestSupport.Reflog(twinWt, "HEAD")[0]), "the worktree HEAD reflog matches the CLI twin");
            Assert.That(CasCommitTestSupport.Reflog(wt, "HEAD")[0], Is.EqualTo("Test|test@example.com|commit: worktree subject"));
            Assert.That(CasCommitTestSupport.Reflog(main, "refs/heads/wt-feature")[0], Is.EqualTo(CasCommitTestSupport.Reflog(twinMain, "refs/heads/wt-feature")[0]), "the branch reflog matches the CLI twin");
            // Old and new ids: the newest branch entry is the new commit, the one before it is the tip the worktree had.
            var branchIds = RunGit(main, "log", "-g", "--format=%H", "-2", "refs/heads/wt-feature").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.That(branchIds, Is.EqualTo(new[] { result.CommitHash, wtTipBefore }), "branch reflog: new commit, then the previous tip");
        });
        AssertFsckClean(main);
    }

    [Test]
    public async Task InProcessCommit_TwoLinkedWorktrees_CommitInParallel_BothTakenAndBothLoggedAsync()
    {
        var route = new InProcessCommitRoute();
        var impl = NewImpl(route);
        var main = NewRepo("par-main", inProcess: true);
        var wtA = AddLinkedWorktree(main, _root, "par-a", "par-a");
        var wtB = AddLinkedWorktree(main, _root, "par-b", "par-b");
        var tipA = Head(wtA);
        var tipB = Head(wtB);
        Write(wtA, "a-new.txt", "a\n");
        RunGit(wtA, "add", "a-new.txt");
        Write(wtB, "b-new.txt", "b\n");
        RunGit(wtB, "add", "b-new.txt");

        var tasks = new[]
        {
            Task.Run(() => impl.CommitAsync(wtA, "parallel a", null, null, amend: false, CancellationToken.None)),
            Task.Run(() => impl.CommitAsync(wtB, "parallel b", null, null, amend: false, CancellationToken.None)),
        };
        var results = await Task.WhenAll(tasks);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsError, Is.False, results[0].Error);
            Assert.That(results[1].IsError, Is.False, results[1].Error);
            Assert.That(results[0].CommitHash, Is.Not.EqualTo(tipA), "worktree A moved");
            Assert.That(results[1].CommitHash, Is.Not.EqualTo(tipB), "worktree B moved");
            Assert.That(results[0].CommitHash, Is.EqualTo(Head(wtA)), "worktree A's HEAD is its commit");
            Assert.That(results[1].CommitHash, Is.EqualTo(Head(wtB)), "worktree B's HEAD is its commit");
            Assert.That(route.TakenCount, Is.EqualTo(2), "both commits were taken in process");
            Assert.That(route.DeclineCount, Is.Zero, "last decline: " + route.LastDeclineReason);
            Assert.That(CountTakenLines(main), Is.EqualTo(2), "both taken commits are logged in the common directory");
        });
        AssertFsckClean(main);
    }

    // ---- no local core.autocrlf pin ----

    [Test]
    public async Task InProcessCommit_UnpinnedLfRepo_NoAutocrlfEntry_EolLfAttribute_IsTakenAndStoresLfAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("lf-unpinned", inProcess: true, pinAutocrlf: false);
        // Only the repository's local config is asserted on; a machine-global core.autocrlf is not pinned away here.
        Assert.That(RunGitRaw(directory, "config", "--local", "--get", "core.autocrlf").ExitCode, Is.Not.Zero, "precondition: no local core.autocrlf entry");
        Write(directory, ".gitattributes", "* text eol=lf\n");
        var crlf = Encoding.ASCII.GetBytes("one\r\ntwo\r\n");
        File.WriteAllBytes(Path.Combine(directory, "a.txt"), crlf);

        var result = await NewImpl(route).CommitAsync(directory, "lf file", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertTaken(route, directory);
        Assert.Multiple(() =>
        {
            Assert.That(RunGitRaw(directory, "cat-file", "-p", "HEAD:a.txt").StdOut, Is.EqualTo("one\ntwo\n"), "the stored blob is LF-only");
            Assert.That(File.ReadAllBytes(Path.Combine(directory, "a.txt")), Is.EqualTo(crlf), "the working file keeps its CRLF bytes");
        });
    }
}
